using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.HeadlessTests;

public sealed class ConcurrentReviewSaveTests {
    static async Task WithSettings(Func<string, Task> work) {
        string folder = Directory.CreateTempSubdirectory("vdf-concurrent-review-").FullName;
        var settings = SettingsFile.Instance;
        string database = settings.CustomDatabaseFolder;
        var selection = settings.AutomationSelection;
        var nfo = settings.NfoFavourites;
        try {
            SettingsFile.SetSettingsPath(Path.Combine(folder, "Settings.json"));
            settings.CustomDatabaseFolder = folder;
            settings.AutomationSelection = new() { AutoAfterScan = false };
            settings.NfoFavourites = new NfoOptions();
            SettingsFile.SaveSettings();
            await work(folder);
        }
        finally {
            settings.CustomDatabaseFolder = database;
            settings.AutomationSelection = selection;
            settings.NfoFavourites = nfo;
            SettingsFile.SetSettingsPath(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static DuplicateItemVM Add(MainWindowVM vm, string path, Guid group) {
        if (!File.Exists(path)) File.WriteAllBytes(path, [1, 2, 3, 4]);
        var file = new FileInfo(path);
        var row = new DuplicateItemVM(new DuplicateItem {
            Path = path, Folder = file.DirectoryName!, GroupId = group, SizeLong = file.Length,
            DateModified = file.LastWriteTimeUtc, DateCreated = file.CreationTimeUtc
        });
        vm.Duplicates.Add(row);
        return row;
    }

    static async Task<ReviewRecord> Read(MainWindowVM vm, string path) {
        var store = new ReviewStore(vm.AutomationReviewFile);
        await store.LoadAsync();
        return Assert.Single(store.Records, record => record.Identity.Path == path);
    }

    static async Task<ReviewRecord> ForeignKeepAndProtect(MainWindowVM vm, string path) {
        var record = new ReviewRecord {
            Identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(path)),
            Choice = ReviewChoice.Keep, ManuallyProtected = true, IsKeeper = true,
            Profile = "other reviewer", Reason = "Keep and protect from another session",
            UpdatedUtc = DateTime.UtcNow.AddSeconds(1)
        };
        await new ReviewStore(vm.AutomationReviewFile).UpdateAsync(records =>
            records.Where(existing => existing.Identity.Path != path).Append(record));
        return record;
    }

    static void AssertKeptAndProtected(DuplicateItemVM row) {
        Assert.Equal(ReviewChoice.Keep, row.ManualReviewChoice);
        Assert.True(row.ManuallyProtected);
        Assert.True(row.IsProtected);
        Assert.True(row.IsAutomationKeeper);
        Assert.False(row.Checked);
        Assert.False(row.AutomaticallySelected);
        Assert.False(row.CanMarkForRemoval);
    }

    [Fact]
    public Task OverlappingEmptySaveCannotReanchorAnUnreviewedInspectionToForeignProtection() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        string targetPath = Path.Combine(folder, "target.mp4");
        string keeperPath = Path.Combine(folder, "keeper.mp4");
        File.WriteAllBytes(targetPath, [1, 2, 3, 4]);
        File.WriteAllBytes(keeperPath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(targetPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(keeperPath, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var target = Add(vm, targetPath, group);
        Add(vm, keeperPath, group);
        SettingsFile.Instance.AutomationSelection.AutoAfterScan = true;
        SettingsFile.Instance.AutomationSelection.Profile = SelectionProfile.NewestFirst;
        SettingsFile.SaveSettings();
        ReviewRecord? foreign = null;
        int callbacks = 0;
        vm.BeforeAutomationInspectionAppliedAsync = async () => {
            Assert.Equal(0, callbacks++);
            Assert.Null(target.AutomationIdentity);
            Assert.False(target.AutomaticallySelected || target.ManuallyProtected || target.IsAutomationKeeper);
            Assert.Equal(ReviewChoice.None, target.ManualReviewChoice);
            int version = target.AutomationReviewVersion;
            foreign = await ForeignKeepAndProtect(vm, targetPath);
            // Reload/update the same store via the normal save path while the
            // captured inspection still has no review for this row.
            await vm.SaveAutomationReviewRecordsAsync();
            Assert.Equal(version, target.AutomationReviewVersion);
            Assert.False(target.ManuallyProtected);
        };
        try { await vm.ApplyAutomaticSelectionAsync(); }
        finally { vm.BeforeAutomationInspectionAppliedAsync = null; }
        Assert.Equal(1, callbacks);
        AssertKeptAndProtected(target);
        var saved = await Read(vm, targetPath);
        Assert.Equal(foreign!.UpdatedUtc, saved.UpdatedUtc);
        Assert.Equal(foreign.Reason, saved.Reason);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        await vm.PrepareAutomationRescanAsync();
        var next = new MainWindowVM();
        var restored = Add(next, targetPath, group);
        Add(next, keeperPath, group);
        await next.InitialiseAutomationReviewAsync();
        AssertKeptAndProtected(restored);
        await next.PrepareAutomationRescanAsync();
    }));

    [Fact]
    public Task AutomaticFlushReconcilesNewSamePathForeignKeepAndProtect() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        var keeper = Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = target.ItemInfo.Path, GroupId = group, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = group, IsKeeper = true }
        ] });
        Assert.True(target.Checked);
        var foreign = await ForeignKeepAndProtect(vm, target.ItemInfo.Path);

        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(target);
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.Equal(foreign.Identity, saved.Identity);
        Assert.Equal(foreign.UpdatedUtc, saved.UpdatedUtc);
        Assert.Equal(foreign.Profile, saved.Profile);
        Assert.Equal(foreign.Reason, saved.Reason);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);

        await vm.InitialiseAutomationReviewAsync();
        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(target);
        saved = await Read(vm, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
    }));

    [Fact]
    public Task StaleUnprotectConflictsThenDeliberateReapplyAndRemovalPersist() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        await vm.ProtectAutomationItemCommand.Execute(target);
        var foreign = await ForeignKeepAndProtect(vm, target.ItemInfo.Path);

        await vm.UnprotectAutomationItemCommand.Execute(target);
        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(target);
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        Assert.Equal(foreign.Reason, saved.Reason);

        await vm.UnprotectAutomationItemCommand.Execute(target);
        Assert.False(target.ManuallyProtected);
        Assert.True(target.CanMarkForRemoval);
        target.Checked = true;
        Assert.Equal(ReviewChoice.Remove, target.ManualReviewChoice);
        await vm.PrepareAutomationRescanAsync();
        saved = await Read(vm, target.ItemInfo.Path);
        Assert.False(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Remove, saved.Choice);
        Assert.False(saved.IsKeeper);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        Assert.True(restored.Checked);
        Assert.Equal(ReviewChoice.Remove, restored.ManualReviewChoice);
        Assert.False(restored.ManuallyProtected);
        await reloaded.PrepareAutomationRescanAsync();
        Assert.Equal(ReviewChoice.Remove, (await Read(reloaded, target.ItemInfo.Path)).Choice);
    }));

    [Fact]
    public Task HardLinkAliasConflictPreservesForeignReviewAndOtherAliasChoice() => HeadlessUi.Run(() => WithSettings(async folder => {
        string firstPath = Path.Combine(folder, "first.mp4");
        string aliasPath = Path.Combine(folder, "alias.mp4");
        File.WriteAllBytes(firstPath, [1, 2, 3, 4]);
        typeof(FileIdentity).Assembly.GetType("VDF.Core.Utils.HardLinkUtils")!.GetMethod("CreateHardLink")!
            .Invoke(null, [aliasPath, firstPath]);
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var first = Add(vm, firstPath, group);
        var alias = Add(vm, aliasPath, group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        first.Checked = true;
        alias.Checked = true;
        await vm.SaveAutomationReviewRecordsAsync();
        Assert.Equal(first.AutomationIdentity!.StableFileId, alias.AutomationIdentity!.StableFileId);
        await ForeignKeepAndProtect(vm, aliasPath);

        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(alias);
        Assert.True(first.Checked);
        Assert.Equal(ReviewChoice.Remove, first.ManualReviewChoice);
        var firstSaved = await Read(vm, firstPath);
        var aliasSaved = await Read(vm, aliasPath);
        Assert.Equal(ReviewChoice.Remove, firstSaved.Choice);
        Assert.False(firstSaved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, aliasSaved.Choice);
        Assert.True(aliasSaved.ManuallyProtected);
        Assert.Equal(firstSaved.Identity.StableFileId, aliasSaved.Identity.StableFileId);
        await vm.InitialiseAutomationReviewAsync();
        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(alias);
        Assert.Equal(ReviewChoice.Remove, (await Read(vm, firstPath)).Choice);
        Assert.True((await Read(vm, aliasPath)).ManuallyProtected);
    }));

    [Fact]
    public Task CheckboxChoiceMadeWhileCommitAwaitsSurvivesFlushAndReload() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        target.Checked = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BeforeAutomationReviewCommitAsync = async () => {
            entered.TrySetResult();
            await release.Task;
        };
        Task save = vm.SaveAutomationReviewRecordsAsync();
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            target.Checked = false;
            Assert.Equal(ReviewChoice.Keep, target.ManualReviewChoice);
        }
        finally {
            vm.BeforeAutomationReviewCommitAsync = null;
            release.TrySetResult();
            await save;
        }
        Assert.Equal(ReviewChoice.Keep, target.ManualReviewChoice);
        await vm.PrepareAutomationRescanAsync();
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        Assert.True(saved.IsKeeper);
        Assert.False(saved.AutomaticallySelected);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        Assert.Equal(ReviewChoice.Keep, restored.ManualReviewChoice);
        Assert.True(restored.IsAutomationKeeper);
        Assert.False(restored.Checked);
        await reloaded.PrepareAutomationRescanAsync();
        Assert.Equal(ReviewChoice.Keep, (await Read(reloaded, target.ItemInfo.Path)).Choice);
    }));

    [Fact]
    public Task PendingCheckboxRemovalCannotClearForeignProtectionDuringCommitAwait() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        await vm.KeepAutomationItemCommand.Execute(target);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BeforeAutomationReviewCommitAsync = async () => {
            entered.TrySetResult();
            await release.Task;
        };
        Task save = vm.SaveAutomationReviewRecordsAsync();
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ForeignKeepAndProtect(vm, target.ItemInfo.Path);
            target.Checked = true;
            Assert.Equal(ReviewChoice.Remove, target.ManualReviewChoice);
            Assert.False(target.ManuallyProtected); // This session has not observed the foreign decision yet.
        }
        finally {
            vm.BeforeAutomationReviewCommitAsync = null;
            release.TrySetResult();
            await save;
        }
        Assert.Equal(ReviewChoice.Remove, target.ManualReviewChoice);
        Assert.True(target.ManuallyProtected);
        Assert.True(target.IsProtected);
        Assert.False(target.Checked);
        Assert.False(target.CanMarkForRemoval);
        await vm.PrepareAutomationRescanAsync();
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Remove, saved.Choice);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        Assert.True(restored.ManuallyProtected);
        Assert.False(restored.Checked);
        Assert.Equal(ReviewChoice.Remove, restored.ManualReviewChoice);
        await reloaded.PrepareAutomationRescanAsync();
        saved = await Read(reloaded, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Remove, saved.Choice);
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReviewCommandMadeWhileCommitAwaitsSurvivesQueuedSave(bool protect) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        target.Checked = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BeforeAutomationReviewCommitAsync = async () => {
            entered.TrySetResult();
            await release.Task;
        };
        Task save = vm.SaveAutomationReviewRecordsAsync();
        Task? command = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Subscribe to the real command, but do not await its queued save
            // until the earlier transaction releases the save gate.
            command = protect ? vm.ProtectAutomationItemCommand.Execute(target).ToTask() :
                vm.KeepAutomationItemCommand.Execute(target).ToTask();
            Assert.False(target.Checked);
            if (protect) Assert.True(target.ManuallyProtected);
            else Assert.Equal(ReviewChoice.Keep, target.ManualReviewChoice);
        }
        finally {
            vm.BeforeAutomationReviewCommitAsync = null;
            release.TrySetResult();
            await save;
            if (command is not null) await command;
        }
        await vm.PrepareAutomationRescanAsync();
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.Equal(protect, saved.ManuallyProtected);
        Assert.Equal(protect ? ReviewChoice.Remove : ReviewChoice.Keep, saved.Choice);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        Assert.Equal(protect, restored.ManuallyProtected);
        Assert.False(restored.Checked);
        await reloaded.PrepareAutomationRescanAsync();
        saved = await Read(reloaded, target.ItemInfo.Path);
        Assert.Equal(protect, saved.ManuallyProtected);
        Assert.Equal(protect ? ReviewChoice.Remove : ReviewChoice.Keep, saved.Choice);
    }));

    [Fact]
    public Task ExplicitSameValueProtectDuringCommitAwaitSurvivesForeignUnprotect() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        await vm.KeepAutomationItemCommand.Execute(target);
        await vm.ProtectAutomationItemCommand.Execute(target);
        var original = await Read(vm, target.ItemInfo.Path);
        Assert.True(original.ManuallyProtected);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BeforeAutomationReviewCommitAsync = async () => {
            entered.TrySetResult();
            await release.Task;
        };
        Task save = vm.SaveAutomationReviewRecordsAsync();
        Task? command = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await new ReviewStore(vm.AutomationReviewFile).UpdateAsync(records => records
                .Where(record => record.Identity.Path != target.ItemInfo.Path)
                .Append(new ReviewRecord {
                    Identity = original.Identity, Choice = ReviewChoice.Keep, ManuallyProtected = false,
                    IsKeeper = true, Reason = "Unprotected by another session", UpdatedUtc = original.UpdatedUtc.AddSeconds(1)
                }));
            // It is already protected in this session. Repeating the command
            // still expresses protection intent newer than the captured save.
            Assert.True(target.ManuallyProtected);
            command = vm.ProtectAutomationItemCommand.Execute(target).ToTask();
        }
        finally {
            vm.BeforeAutomationReviewCommitAsync = null;
            release.TrySetResult();
            await save;
            if (command is not null) await command;
        }
        AssertKeptAndProtected(target);
        await vm.PrepareAutomationRescanAsync();
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        Assert.Equal("Protected by you.", saved.Reason);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        AssertKeptAndProtected(restored);
        await reloaded.PrepareAutomationRescanAsync();
        saved = await Read(reloaded, target.ItemInfo.Path);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
    }));

    [Fact]
    public Task SharedStoreReloadAfterCommitCannotAdvanceBaselinePastUnreconciledProtection() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var target = Add(vm, Path.Combine(folder, "target.mp4"), group);
        Add(vm, Path.Combine(folder, "keeper.mp4"), group);
        await vm.InitialiseAutomationReviewAsync();
        target.Checked = true;
        await vm.PrepareAutomationRescanAsync();
        ReviewRecord? foreign = null;
        bool hookRan = false;
        vm.BeforeAutomationReviewReconciliationAsync = async () => {
            hookRan = true;
            foreign = await ForeignKeepAndProtect(vm, target.ItemInfo.Path);
            // This loads the same cached ReviewStore while the save awaits its
            // UI reconciliation. Already-restored rows keep their local flags.
            await vm.BuildAutomationCandidatesAsync();
            Assert.Equal(ReviewChoice.Remove, target.ManualReviewChoice);
            Assert.False(target.ManuallyProtected);
        };
        try {
            await vm.SaveAutomationReviewRecordsAsync();
        }
        finally { vm.BeforeAutomationReviewReconciliationAsync = null; }
        Assert.True(hookRan);
        Assert.NotNull(foreign);

        // The next automatic save must compare its actual committed generation
        // with the foreign write instead of treating the shared cache as observed.
        await vm.InitialiseAutomationReviewAsync();
        await vm.PrepareAutomationRescanAsync();
        AssertKeptAndProtected(target);
        var saved = await Read(vm, target.ItemInfo.Path);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(foreign.Reason, saved.Reason);

        var reloaded = new MainWindowVM();
        var restored = Add(reloaded, target.ItemInfo.Path, group);
        Add(reloaded, Path.Combine(folder, "keeper.mp4"), group);
        await reloaded.InitialiseAutomationReviewAsync();
        AssertKeptAndProtected(restored);
        await reloaded.PrepareAutomationRescanAsync();
        saved = await Read(reloaded, target.ItemInfo.Path);
        Assert.Equal(ReviewChoice.Keep, saved.Choice);
        Assert.True(saved.ManuallyProtected);
        Assert.Equal(foreign.Reason, saved.Reason);
    }));
}
