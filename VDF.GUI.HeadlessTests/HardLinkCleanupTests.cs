using System.Reactive.Linq;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.HeadlessTests;

public sealed class HardLinkCleanupTests {
    static async Task WithSettings(Func<string, Task> work) {
        string folder = Directory.CreateTempSubdirectory("vdf-hardlink-cleanup-").FullName;
        var settings = SettingsFile.Instance;
        string originalDatabase = settings.CustomDatabaseFolder;
        string settingsPath = Path.Combine(folder, "Settings.json");
        try {
            SettingsFile.SetSettingsPath(settingsPath);
            settings.CustomDatabaseFolder = folder;
            SettingsFile.SaveSettings();
            await work(folder);
        }
        finally {
            settings.CustomDatabaseFolder = originalDatabase;
            SettingsFile.SetSettingsPath(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static DuplicateItemVM Add(MainWindowVM vm, string folder, Guid group, string name) {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return AddExisting(vm, group, path);
    }

    static DuplicateItemVM AddExisting(MainWindowVM vm, Guid group, string path) {
        var info = new FileInfo(path);
        var row = new DuplicateItemVM(new DuplicateItem {
            Path = path, Folder = info.DirectoryName!, GroupId = group, SizeLong = info.Length,
            DateModified = info.LastWriteTimeUtc, DateCreated = info.CreationTimeUtc
        });
        vm.Duplicates.Add(row);
        return row;
    }

    [Fact]
    public Task QuarantineCannotDropAColdUnreadableIgnoredGroupMember() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var target = Add(vm, folder, groupId, "target.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        var third = Add(vm, folder, groupId, "third.mp4");
        var thirdIdentity = FileIdentity.Capture(third.ItemInfo.Path)!;
        await new ReviewStore(vm.AutomationReviewFile).SaveAsync([
            new() { Identity = thirdIdentity, Choice = ReviewChoice.Ignore }
        ]);
        target.Checked = true;
        using (var locked = new FileStream(third.ItemInfo.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.Null(FileIdentity.Capture(third.ItemInfo.Path));
            var preview = await vm.CreateQuarantinePreviewAsync();
            Assert.True(Assert.Single(preview.Items).CanQuarantine);
            await preview.ExecuteCommand.Execute();
            Assert.True(File.Exists(target.ItemInfo.Path));
            Assert.True(File.Exists(keeper.ItemInfo.Path));
            Assert.Contains(target, vm.Duplicates);
            var store = new ReviewStore(vm.AutomationReviewFile);
            await store.LoadAsync();
            Assert.Equal(ReviewChoice.Ignore, store.Records.Single(record => record.Identity.Path == third.ItemInfo.Path).Choice);
        }
        Assert.True(thirdIdentity.MatchesCurrent());
    }));

    [Theory]
    [InlineData(ReviewChoice.Ignore)]
    [InlineData(ReviewChoice.RejectGroup)]
    public Task CachedGroupMemberAfterLateVetoRollbackBlocksTheNextTarget(ReviewChoice choice) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var group = Guid.NewGuid();
        var first = Add(vm, folder, group, "first.mp4");
        var second = Add(vm, folder, group, "second.mp4");
        var keeper = Add(vm, folder, group, "keeper.mp4");
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = first.ItemInfo.Path, GroupId = group, RemoveCandidate = true },
            new() { Path = second.ItemInfo.Path, GroupId = group, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = group, IsKeeper = true }
        ] });
        await vm.SaveAutomationReviewRecordsAsync();
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        var old = first.AutomationIdentity!;
        var guard = new CleanupProtectionGuard(folder);
        bool vetoWritten = false;
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => guard.RemoveAsync(
            approved[first].Target, approved[first].Keeper, claim => {
                vetoWritten = true;
                new ReviewStore(vm.AutomationReviewFile).UpdateAsync(records =>
                    records.Where(record => record.Identity.Path != old.Path).Concat([
                        new ReviewRecord { Identity = old, Choice = choice }
                    ])).GetAwaiter().GetResult();
                throw new IOException("Synthetic interrupted cleanup after the late review.");
            }, groupMembers: vm.CaptureCompleteCleanupGroup(group))));
        Assert.True(vetoWritten);
        var saved = new ReviewStore(vm.AutomationReviewFile);
        await saved.LoadAsync();
        var veto = saved.Find(old.Path)!;
        Assert.Equal(choice, veto.Choice);
        Assert.NotEqual(old.ChangeStamp, veto.Identity.ChangeStamp);
        Assert.Equal(old, first.AutomationIdentity);
        // The UI still holds its pre-rollback identity; group capture must refuse
        // it before any later target can reach the destructive callback.
        await Assert.ThrowsAsync<IOException>(() => Task.Run(async () => {
            var members = vm.CaptureCompleteCleanupGroup(group);
            await guard.RemoveAsync(approved[second].Target, approved[second].Keeper, File.Delete, groupMembers: members);
        }));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first.ItemInfo.Path));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(second.ItemInfo.Path));
        Assert.True(File.Exists(keeper.ItemInfo.Path));
        await vm.PrepareAutomationRescanAsync();
    }));

    [Fact]
    public Task CleanupBatchWithUncachedIdentitiesSkipsOnlyCompletedRows() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var first = Add(vm, folder, groupId, "first.mp4");
        var second = Add(vm, folder, groupId, "second.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = first.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = second.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = groupId, IsKeeper = true }
        ] });
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        Assert.Equal(2, approved.Count);
        Assert.Null(first.AutomationIdentity);
        Assert.Null(second.AutomationIdentity);
        var completed = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer.Instance);
        var guard = new CleanupProtectionGuard(folder);
        foreach (var row in new[] { first, second }) {
            var action = approved[row];
            await Task.Run(async () => {
                var members = vm.CaptureCompleteCleanupGroup(groupId, completed);
                await guard.RemoveAsync(action.Target, action.Keeper, File.Delete, groupMembers: members);
            });
            completed.Add(row);
        }
        Assert.Equal(2, completed.Count);
        Assert.Contains(first, vm.Duplicates);
        Assert.Contains(second, vm.Duplicates);
        Assert.False(File.Exists(first.ItemInfo.Path));
        Assert.False(File.Exists(second.ItemInfo.Path));
        Assert.True(approved[second].Keeper.MatchesCurrent(forceHash: true));
        foreach (var row in completed) vm.Duplicates.Remove(row);
        await vm.PrepareAutomationRescanAsync();
    }));

    [Fact]
    public Task SymlinkBatchWithUncachedIdentitiesSkipsOnlyCompletedRows() => HeadlessUi.Run(() => WithSettings(async folder => {
        if (OperatingSystem.IsWindows()) return; // Symbolic links require privileges on some Windows runners.
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var first = Add(vm, folder, groupId, "first.mp4");
        var second = Add(vm, folder, groupId, "second.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = first.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = second.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = groupId, IsKeeper = true }
        ] });
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        Assert.Equal(2, approved.Count);
        Assert.Null(first.AutomationIdentity);
        Assert.Null(second.AutomationIdentity);
        var completed = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer.Instance);
        var guard = new CleanupProtectionGuard(folder);
        foreach (var row in new[] { first, second }) {
            await Task.Run(async () => {
                var members = vm.CaptureCompleteCleanupGroup(groupId, completed);
                await vm.ReplaceApprovedWithLinkAsync(guard, approved[row], approved, members, hardLink: false);
            });
            completed.Add(row);
        }
        Assert.Equal(2, completed.Count);
        Assert.Contains(first, vm.Duplicates);
        Assert.Contains(second, vm.Duplicates);
        Assert.Equal(keeper.ItemInfo.Path, new FileInfo(first.ItemInfo.Path).LinkTarget);
        Assert.Equal(keeper.ItemInfo.Path, new FileInfo(second.ItemInfo.Path).LinkTarget);
        Assert.True(approved[second].Keeper.MatchesCurrent(forceHash: true));
        foreach (var row in completed) vm.Duplicates.Remove(row);
        await vm.PrepareAutomationRescanAsync();
    }));

    [Fact]
    public Task CleanupBatchStillRetainsAnUnreadableIgnoredMemberAfterAnEarlierSuccess() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var first = Add(vm, folder, groupId, "first.mp4");
        var second = Add(vm, folder, groupId, "second.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        var third = Add(vm, folder, groupId, "third.mp4");
        await vm.InitialiseAutomationReviewAsync();
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        Assert.Equal(2, approved.Count);
        var completed = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer.Instance);
        var guard = new CleanupProtectionGuard(folder);
        await guard.RemoveAsync(approved[first].Target, approved[first].Keeper, File.Delete,
            groupMembers: vm.CaptureCompleteCleanupGroup(groupId, completed));
        completed.Add(first);
        var ignored = Assert.IsType<FileIdentity>(FileIdentity.Capture(third.ItemInfo.Path));
        await new ReviewStore(vm.AutomationReviewFile).UpdateAsync(records => records.Concat([
            new ReviewRecord { Identity = ignored, Choice = ReviewChoice.Ignore }
        ]));
        Assert.Null(third.AutomationIdentity);
        using (var locked = new FileStream(third.ItemInfo.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.Null(FileIdentity.Capture(third.ItemInfo.Path));
            await Assert.ThrowsAsync<IOException>(() => Task.Run(async () => {
                var members = vm.CaptureCompleteCleanupGroup(groupId, completed);
                await guard.RemoveAsync(approved[second].Target, approved[second].Keeper, File.Delete, groupMembers: members);
            }));
        }
        Assert.Single(completed);
        Assert.True(approved[second].Target.MatchesCurrent(forceHash: true));
        Assert.True(approved[second].Keeper.MatchesCurrent(forceHash: true));
        Assert.True(ignored.MatchesCurrent(forceHash: true));
        var saved = new ReviewStore(vm.AutomationReviewFile);
        await saved.LoadAsync();
        Assert.Equal(ReviewChoice.Ignore, saved.Find(third.ItemInfo.Path)!.Choice);
        vm.Duplicates.Remove(first);
        await vm.PrepareAutomationRescanAsync();
    }));

    [Fact]
    public Task HardLinkBatchRefreshesAnAutomaticKeeperAndEveryRemainingAction() => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var first = Add(vm, folder, groupId, "first.mp4");
        var second = Add(vm, folder, groupId, "second.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = first.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = second.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = groupId, IsKeeper = true }
        ] });
        await vm.SaveAutomationReviewRecordsAsync();
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        Assert.Equal(2, approved.Count);
        var guard = new CleanupProtectionGuard(folder);
        foreach (var row in new[] { first, second }) {
            var action = approved[row];
            var members = vm.Duplicates.Select(member => member.AutomationIdentity).OfType<FileIdentity>().ToArray();
            // The production loop runs off the UI thread; keeper row changes must
            // marshal back to it while updating the pending batch's identities.
            await Task.Run(() => vm.ReplaceApprovedWithLinkAsync(guard, action, approved, members, hardLink: true));
            Assert.True(approved[second].Keeper.MatchesCurrent(forceHash: true));
            Assert.True(keeper.AutomationFileSnapshot!.Matches(keeper.AutomationIdentity!));
            Assert.Equal(new FileInfo(keeper.ItemInfo.Path).CreationTimeUtc, keeper.ItemInfo.DateCreated);
        }
        Assert.Equal(keeper.AutomationIdentity!.StableFileId, FileIdentity.Capture(first.ItemInfo.Path)!.StableFileId);
        Assert.Equal(keeper.AutomationIdentity.StableFileId, FileIdentity.Capture(second.ItemInfo.Path)!.StableFileId);
        vm.Duplicates.Remove(first);
        vm.Duplicates.Remove(second);
        await vm.SaveAutomationReviewRecordsAsync();
        await vm.InitialiseAutomationReviewAsync();
        Assert.False(keeper.ManuallyProtected);
        Assert.Equal(ReviewChoice.None, keeper.ManualReviewChoice);
        Assert.True(keeper.IsAutomationKeeper);
        Assert.False(keeper.AutomationSnapshotInvalid);
        var store = new ReviewStore(vm.AutomationReviewFile);
        await store.LoadAsync();
        Assert.True(store.Find(keeper.ItemInfo.Path)!.IsKeeper);
        await vm.PrepareAutomationRescanAsync();
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HardLinkBatchRefreshesExistingKeeperAliasesAndTheirReviews(bool cacheAliasIdentity) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var first = Add(vm, folder, groupId, "first.mp4");
        var second = Add(vm, folder, groupId, "second.mp4");
        string keeperPath = Path.Combine(folder, "keeper.mp4");
        string aliasPath = Path.Combine(folder, "alias.mp4");
        File.WriteAllBytes(keeperPath, [1, 2, 3, 4]);
        // The fixture uses the same platform hardlink implementation as cleanup;
        // Core's helper is internal to the GUI rather than this test assembly.
        typeof(FileIdentity).Assembly.GetType("VDF.Core.Utils.HardLinkUtils")!.GetMethod("CreateHardLink")!
            .Invoke(null, [aliasPath, keeperPath]);
        var keeper = AddExisting(vm, groupId, keeperPath);
        var alias = AddExisting(vm, groupId, aliasPath);
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = first.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = second.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = keeperPath, GroupId = groupId, IsKeeper = true },
            new() { Path = aliasPath, GroupId = groupId, IsKeeper = true }
        ] });
        if (cacheAliasIdentity) await vm.SaveAutomationReviewRecordsAsync();
        else Assert.Null(alias.AutomationIdentity);
        var approved = await vm.PrepareCleanupGuardAsync([first, second]);
        Assert.Equal(2, approved.Count);
        var guard = new CleanupProtectionGuard(folder);
        var completed = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer.Instance);
        foreach (var row in new[] { first, second }) {
            await Task.Run(async () => {
                var members = vm.CaptureCompleteCleanupGroup(groupId, completed);
                await vm.ReplaceApprovedWithLinkAsync(guard, approved[row], approved, members, hardLink: true);
            });
            completed.Add(row);
            Assert.True(alias.AutomationIdentity!.MatchesCurrent(forceHash: true));
            Assert.True(alias.AutomationFileSnapshot!.Matches(alias.AutomationIdentity));
            Assert.Equal(new FileInfo(aliasPath).CreationTimeUtc, alias.ItemInfo.DateCreated);
            Assert.True(approved[second].Keeper.MatchesCurrent(forceHash: true));
        }
        Assert.Equal(keeper.AutomationIdentity!.StableFileId, FileIdentity.Capture(first.ItemInfo.Path)!.StableFileId);
        Assert.Equal(keeper.AutomationIdentity.StableFileId, FileIdentity.Capture(second.ItemInfo.Path)!.StableFileId);
        foreach (var row in completed) vm.Duplicates.Remove(row);
        var candidates = await vm.BuildAutomationCandidatesAsync();
        Assert.True(candidates[aliasPath].IsAvailable);
        Assert.False(alias.AutomationSnapshotInvalid);
        await vm.SaveAutomationReviewRecordsAsync();
        var saved = new ReviewStore(vm.AutomationReviewFile);
        await saved.LoadAsync();
        Assert.True(saved.Find(aliasPath)!.IsKeeper);
        Assert.True(saved.Find(keeperPath)!.IsKeeper);
        await vm.PrepareAutomationRescanAsync();
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HardLinkKeeperRefreshPreservesCompetingWriterDecisions(bool replaceKeeperReview) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var target = Add(vm, folder, groupId, "target.mp4");
        string keeperPath = Path.Combine(folder, "keeper.mp4");
        string aliasPath = Path.Combine(folder, "keeper-alias.mp4");
        File.WriteAllBytes(keeperPath, [1, 2, 3, 4]);
        typeof(FileIdentity).Assembly.GetType("VDF.Core.Utils.HardLinkUtils")!.GetMethod("CreateHardLink")!
            .Invoke(null, [aliasPath, keeperPath]);
        var keeper = AddExisting(vm, groupId, keeperPath);
        var alias = AddExisting(vm, groupId, aliasPath);
        await vm.InitialiseAutomationReviewAsync();
        vm.ApplyAutomationPlan(new SelectionPlan { Decisions = [
            new() { Path = target.ItemInfo.Path, GroupId = groupId, RemoveCandidate = true },
            new() { Path = keeper.ItemInfo.Path, GroupId = groupId, IsKeeper = true },
            new() { Path = alias.ItemInfo.Path, GroupId = groupId, IsKeeper = true }
        ] });
        await vm.SaveAutomationReviewRecordsAsync();
        var approved = await vm.PrepareCleanupGuardAsync([target]);
        var previous = approved[target].Keeper;
        string unrelatedPath = Path.Combine(folder, "unrelated.mp4");
        File.WriteAllBytes(unrelatedPath, [7, 8, 9]);
        var unrelated = Assert.IsType<FileIdentity>(FileIdentity.Capture(unrelatedPath));
        FileIdentity? replacement = null;
        if (replaceKeeperReview) {
            string replacementPath = Path.Combine(folder, "replacement.mp4");
            File.WriteAllBytes(replacementPath, [1, 2, 3, 4]);
            // A cached review of another native file at the keeper path must
            // remain stale rather than borrowing this keeper's new identity.
            replacement = Assert.IsType<FileIdentity>(FileIdentity.Capture(replacementPath)) with { Path = previous.Path };
        }
        var members = vm.Duplicates.Select(member => member.AutomationIdentity).OfType<FileIdentity>().ToArray();
        bool writerRan = false;
        vm.BeforeCleanupKeeperReviewRefreshAsync = async () => {
            writerRan = true;
            var writer = new ReviewStore(vm.AutomationReviewFile);
            await writer.UpdateAsync(records => records.Where(record => record.Identity.Path != previous.Path && record.Identity.Path != aliasPath).Concat([
                new ReviewRecord {
                    Identity = replacement ?? previous, Choice = ReviewChoice.Keep, ManuallyProtected = true,
                    IsKeeper = true, Profile = "competing writer", Reason = "Keep this decision"
                },
                new ReviewRecord {
                    Identity = (replacement ?? previous) with { Path = aliasPath }, Choice = ReviewChoice.Keep,
                    ManuallyProtected = true, IsKeeper = true, Profile = "competing writer", Reason = "Keep alias decision"
                },
                new ReviewRecord { Identity = unrelated, ManuallyProtected = true, Reason = "Unrelated protection" }
            ]));
        };
        try {
            await Task.Run(() => vm.ReplaceApprovedWithLinkAsync(new CleanupProtectionGuard(folder), approved[target], approved, members, hardLink: true));
            Assert.True(writerRan);
            var saved = new ReviewStore(vm.AutomationReviewFile);
            await saved.LoadAsync();
            var keeperReview = Assert.Single(saved.Records, record => record.Identity.Path == previous.Path);
            Assert.True(keeperReview.ManuallyProtected);
            Assert.Equal(ReviewChoice.Keep, keeperReview.Choice);
            Assert.Equal("competing writer", keeperReview.Profile);
            Assert.Equal("Keep this decision", keeperReview.Reason);
            if (replacement is not null) Assert.Equal(replacement, keeperReview.Identity);
            else Assert.True(keeperReview.Identity.MatchesCurrent(forceHash: true));
            Assert.True(saved.Find(unrelatedPath)!.ManuallyProtected);
            Assert.True(approved[target].Keeper.MatchesCurrent(forceHash: true));
            Assert.True(keeper.AutomationFileSnapshot!.Matches(keeper.AutomationIdentity!));
            Assert.Equal(previous.StableFileId, FileIdentity.Capture(target.ItemInfo.Path)!.StableFileId);
        }
        finally { vm.BeforeCleanupKeeperReviewRefreshAsync = null; }
        await vm.PrepareAutomationRescanAsync();
        var reloaded = new ReviewStore(vm.AutomationReviewFile);
        await reloaded.LoadAsync();
        var afterFlush = Assert.Single(reloaded.Records, record => record.Identity.Path == previous.Path);
        Assert.True(afterFlush.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, afterFlush.Choice);
        Assert.Equal("competing writer", afterFlush.Profile);
        if (replacement is not null) Assert.Equal(replacement, afterFlush.Identity);
        else {
            Assert.True(afterFlush.Identity.MatchesCurrent(forceHash: true));
            Assert.True(keeper.ManuallyProtected);
            Assert.Equal(ReviewChoice.Keep, keeper.ManualReviewChoice);
        }
        var aliasAfterFlush = Assert.Single(reloaded.Records, record => record.Identity.Path == aliasPath);
        Assert.True(aliasAfterFlush.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, aliasAfterFlush.Choice);
        Assert.Equal("Keep alias decision", aliasAfterFlush.Reason);
        if (replacement is not null) Assert.Equal(replacement with { Path = aliasPath }, aliasAfterFlush.Identity);
        else {
            Assert.True(aliasAfterFlush.Identity.MatchesCurrent(forceHash: true));
            Assert.True(alias.ManuallyProtected);
            Assert.Equal(ReviewChoice.Keep, alias.ManualReviewChoice);
        }
        Assert.True(reloaded.Find(unrelatedPath)!.ManuallyProtected);
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HardLinkReplacementRefusesDeliberateKeeperProtectionBeforeMutation(bool protect) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var target = Add(vm, folder, groupId, "target.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        if (protect) await vm.ProtectAutomationItemCommand.Execute(keeper);
        else await vm.KeepAutomationItemCommand.Execute(keeper);
        target.Checked = true;
        await vm.PrepareAutomationRescanAsync();
        var approved = await vm.PrepareCleanupGuardAsync([target]);
        var action = approved[target];
        var members = vm.Duplicates.Select(member => member.AutomationIdentity).OfType<FileIdentity>().ToArray();
        byte[] journal = File.ReadAllBytes(vm.AutomationReviewFile);
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => vm.ReplaceApprovedWithLinkAsync(
            new CleanupProtectionGuard(folder), action, approved, members, hardLink: true)));
        Assert.True(action.Target.MatchesCurrent(forceHash: true));
        Assert.True(action.Keeper.MatchesCurrent(forceHash: true));
        Assert.NotEqual(FileIdentity.Capture(target.ItemInfo.Path)!.StableFileId, FileIdentity.Capture(keeper.ItemInfo.Path)!.StableFileId);
        Assert.Equal(journal, File.ReadAllBytes(vm.AutomationReviewFile));
        Assert.False(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
        await vm.PrepareAutomationRescanAsync();
    }));

    [Theory]
    [InlineData("bytes")]
    [InlineData("file-id")]
    [InlineData("length")]
    [InlineData("modified")]
    public Task HardLinkRefreshRejectsChangesBeyondTheIntentionalChangeStamp(string change) => HeadlessUi.Run(() => WithSettings(async folder => {
        var vm = new MainWindowVM();
        var groupId = Guid.NewGuid();
        var target = Add(vm, folder, groupId, "target.mp4");
        var keeper = Add(vm, folder, groupId, "keeper.mp4");
        await vm.ProtectAutomationItemCommand.Execute(keeper);
        target.Checked = true;
        await vm.PrepareAutomationRescanAsync();
        var approved = await vm.PrepareCleanupGuardAsync([target]);
        var previous = approved[target].Keeper;
        byte[] journal = File.ReadAllBytes(vm.AutomationReviewFile);
        switch (change) {
            case "bytes":
                File.WriteAllBytes(previous.Path, [4, 3, 2, 1]);
                File.SetLastWriteTimeUtc(previous.Path, previous.LastWriteUtc);
                break;
            case "file-id":
                File.Move(previous.Path, previous.Path + ".old");
                File.WriteAllBytes(previous.Path, [1, 2, 3, 4]);
                File.SetLastWriteTimeUtc(previous.Path, previous.LastWriteUtc);
                break;
            case "length":
                File.WriteAllBytes(previous.Path, [1, 2, 3, 4, 5]);
                File.SetLastWriteTimeUtc(previous.Path, previous.LastWriteUtc);
                break;
            case "modified":
                File.SetLastWriteTimeUtc(previous.Path, previous.LastWriteUtc.AddHours(1));
                break;
        }
        Assert.False(await Task.Run(() => vm.RefreshCleanupKeeperAfterHardLinkAsync(previous, approved)));
        Assert.Same(previous, approved[target].Keeper);
        Assert.Same(previous, keeper.AutomationIdentity);
        Assert.Equal(journal, File.ReadAllBytes(vm.AutomationReviewFile));
        await vm.PrepareAutomationRescanAsync();
    }));
}
