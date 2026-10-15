using System.IO.Compression;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.HeadlessTests;

public sealed class ResultsStateAuthorityTests {
    sealed class Scenario {
        internal string Root { get; } = Directory.CreateTempSubdirectory("vdf-results-authority-").FullName;
        internal string A => Path.Combine(Root, "state-a");
        internal string B => Path.Combine(Root, "state-b");
        internal List<MainWindowVM> Hosts { get; } = [];
        internal MainWindowVM Host() { var vm = new MainWindowVM(); Hosts.Add(vm); return vm; }
        internal void Switch(string folder, bool save = true) {
            SettingsFile.Instance.CustomDatabaseFolder = folder;
            if (save) SettingsFile.SaveSettings();
            StopShellSettingsAutosave();
        }
    }

    static async Task WithSettings(Func<Scenario, Task> work) {
        var s = new Scenario();
        Directory.CreateDirectory(s.A);
        Directory.CreateDirectory(s.B);
        var settings = SettingsFile.Instance;
        string database = settings.CustomDatabaseFolder;
        string quarantine = settings.QuarantineFolder;
        var selection = settings.AutomationSelection;
        var nfo = settings.NfoFavourites;
        bool previews = settings.GeneratePreviewThumbnails;
        bool scheduled = settings.EnableScheduledScan;
        bool backups = settings.BackupAfterListChanged;
        bool notify = settings.NotifyOnScanComplete;
        try {
            SettingsFile.SetSettingsPath(Path.Combine(s.Root, "Settings.json"));
            settings.CustomDatabaseFolder = s.A;
            settings.QuarantineFolder = Path.Combine(s.Root, "quarantine");
            settings.AutomationSelection = new() { AutoAfterScan = false };
            settings.NfoFavourites = new() { Enabled = false };
            settings.GeneratePreviewThumbnails = false;
            settings.EnableScheduledScan = false;
            settings.BackupAfterListChanged = false;
            settings.NotifyOnScanComplete = false;
            SettingsFile.SaveSettings();
            StopShellSettingsAutosave();
            await work(s);
        }
        finally {
            // Cancel and flush each host's debounce before restoring shared settings.
            foreach (var host in s.Hosts) {
                host.BeforeAutomationReviewCommitAsync = null;
                host.BeforeAutomationReviewReconciliationAsync = null;
                host.BeforeAutomationInspectionAppliedAsync = null;
                host.BeforeCleanupAuthorityCheckAsync = null;
                await host.PrepareAutomationRescanAsync();
            }
            settings.CustomDatabaseFolder = database;
            settings.QuarantineFolder = quarantine;
            settings.AutomationSelection = selection;
            settings.NfoFavourites = nfo;
            settings.GeneratePreviewThumbnails = previews;
            settings.EnableScheduledScan = scheduled;
            settings.BackupAfterListChanged = backups;
            settings.NotifyOnScanComplete = notify;
            SettingsFile.SetSettingsPath(null);
            StopShellSettingsAutosave();
            try { Directory.Delete(s.Root, true); } catch { }
        }
    }

    static void StopShellSettingsAutosave() {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) {
            Avalonia.Threading.Dispatcher.UIThread.Invoke(StopShellSettingsAutosave);
            return;
        }
        // This fixture saves explicitly and injects its own authority changes.
        // The shared shell's delayed policy writer must not independently take
        // a protection lease before the intended final cleanup callback.
        var window = HeadlessUi.Shell().Window;
        var timer = (Avalonia.Threading.DispatcherTimer)typeof(MainWindow)
            .GetField("settingsSaveDebounce", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        timer.Stop();
    }

    static DuplicateItemVM Add(MainWindowVM host, string path, Guid group) {
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var file = new FileInfo(path);
        var row = new DuplicateItemVM(new DuplicateItem {
            Path = path, Folder = file.DirectoryName!, GroupId = group, SizeLong = file.Length,
            DateModified = file.LastWriteTimeUtc, DateCreated = file.CreationTimeUtc
        });
        host.Duplicates.Add(row);
        return row;
    }

    static async Task<(MainWindowVM Host, DuplicateItemVM Target, DuplicateItemVM Keeper)> Pair(Scenario s) {
        var host = s.Host();
        var group = Guid.NewGuid();
        var target = Add(host, Path.Combine(s.Root, "target.mp4"), group);
        var keeper = Add(host, Path.Combine(s.Root, "keeper.mp4"), group);
        await host.InitialiseAutomationReviewAsync();
        target.Checked = true;
        await host.PrepareAutomationRescanAsync();
        return (host, target, keeper);
    }

    static async Task<ReviewStore> Read(string folder) {
        var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
        await store.LoadAsync();
        return store;
    }

    static async Task<MessageBoxView> Dialog(Func<MessageBoxVM, bool>? matches = null) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) {
            var dialog = ApplicationHelpers.MainWindow.OwnedWindows.OfType<MessageBoxView>()
                .FirstOrDefault(w => w.IsVisible && w.DataContext is MessageBoxVM vm && (matches?.Invoke(vm) ?? true));
            if (dialog is not null) return dialog;
            HeadlessUi.Pump();
            await Task.Delay(10);
        }
        throw new TimeoutException("Expected cleanup/discard dialog did not appear.");
    }

    static async Task Idle(MainWindowVM host) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.IsBusy && DateTime.UtcNow < deadline) { HeadlessUi.Pump(); await Task.Delay(10); }
        Assert.False(host.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedAndUnsavedFolderSwitchRefusesOldRowsAndAllowsSwitchBack(bool save) => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        var preview = await host.CreateQuarantinePreviewAsync();
        Assert.True(Assert.Single(preview.Items).CanQuarantine);
        var original = FileIdentity.Capture(target.ItemInfo.Path)!;
        s.Switch(s.B, save);
        Assert.Equal(Path.Combine(s.A, "ReviewedFiles.json"), host.AutomationReviewFile);
        await Assert.ThrowsAsync<IOException>(() => host.CreateQuarantinePreviewAsync());
        await Assert.ThrowsAsync<IOException>(() => host.PrepareCleanupGuardAsync([target]));
        await preview.ExecuteCommand.Execute();
        Assert.False(preview.IsComplete);
        Assert.Contains("database folder changed", preview.Status);
        Assert.True(original.MatchesCurrent(forceHash: true));
        Assert.Contains(target, host.Duplicates);
        Assert.True(File.Exists(keeper.ItemInfo.Path));
        if (save) Assert.Empty((await Read(s.B)).Records);
        else Assert.False(File.Exists(Path.Combine(s.B, "ReviewedFiles.json")));

        s.Switch(s.A);
        await preview.ExecuteCommand.Execute();
        Assert.True(preview.IsComplete);
        Assert.Contains("Quarantined 1 files", preview.Status);
        Assert.False(File.Exists(target.ItemInfo.Path));
        Assert.True(File.Exists(keeper.ItemInfo.Path));
        Assert.DoesNotContain(target, host.Duplicates);
        Assert.False(File.Exists(Path.Combine(s.B, "QuarantineHistory.json")));
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FolderSwitchCannotInvalidateAHiddenProtectedNativeAlias(bool save) => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        string alias = Path.Combine(s.Root, "hidden-alias.mp4");
        // Core's native helper is shared by the actual cleanup adapter.
        typeof(FileIdentity).Assembly.GetType("VDF.Core.Utils.HardLinkUtils")!.GetMethod("CreateHardLink")!
            .Invoke(null, [alias, target.ItemInfo.Path]);
        // A fresh displayed scan records the link-count stamp after alias creation.
        target.ItemInfo.DateCreated = new FileInfo(target.ItemInfo.Path).CreationTimeUtc;
        target.AutomationIdentity = FileIdentity.Capture(target.ItemInfo.Path);
        target.AutomationFileSnapshot = FileIdentity.CaptureSnapshot(target.ItemInfo.Path);
        var aliasIdentity = FileIdentity.Capture(alias)!;
        await new ReviewStore(host.AutomationReviewFile).UpdateAsync(records => records
            .Where(record => record.Identity.Path != target.ItemInfo.Path).Append(new ReviewRecord {
                Identity = aliasIdentity, ManuallyProtected = true, Choice = ReviewChoice.Keep,
                Reason = "Hidden native alias is deliberately retained"
            }));
        // The alias is deliberately absent from the displayed duplicate rows.
        var preview = await host.CreateQuarantinePreviewAsync();
        Assert.True(Assert.Single(preview.Items).CanQuarantine);
        s.Switch(s.B, save);
        await Assert.ThrowsAsync<IOException>(() => host.CreateQuarantinePreviewAsync());
        await preview.ExecuteCommand.Execute();
        Assert.False(preview.IsComplete);
        Assert.True(aliasIdentity.MatchesCurrent(forceHash: true));
        Assert.True((await Read(s.A)).Find(alias)!.ManuallyProtected);
        if (save) Assert.Empty((await Read(s.B)).Records);
        else Assert.False(File.Exists(Path.Combine(s.B, "ReviewedFiles.json")));

        // Restoring A permits a retry, whose live A-journal check retains the alias.
        s.Switch(s.A);
        await preview.ExecuteCommand.Execute();
        Assert.True(preview.IsComplete);
        Assert.Contains("Quarantined 0 files", preview.Status);
        Assert.True(File.Exists(target.ItemInfo.Path));
        Assert.True(File.Exists(keeper.ItemInfo.Path));
        Assert.True(aliasIdentity.MatchesCurrent(forceHash: true));
        var retained = (await Read(s.A)).Find(alias)!;
        Assert.True(retained.ManuallyProtected);
        Assert.Equal(aliasIdentity, retained.Identity);
        Assert.DoesNotContain(alias, host.Duplicates.Select(row => row.ItemInfo.Path));
    }));

    [Theory]
    [InlineData("delete")]
    [InlineData("symbolic")]
    [InlineData("hard")]
    public Task FolderEditWhileConfirmationIsOpenRefusesActualDeleteAndLinkAdapters(string operation) => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        var identity = FileIdentity.Capture(target.ItemInfo.Path)!;
        var command = operation == "delete" ? host.DeleteCheckedItemsPermanentlyCommand :
            operation == "symbolic" ? host.CreateSymbolLinksForCheckedItemsCommand : host.CreateHardLinksForCheckedItemsCommand;
        await command.Execute();
        var confirm = await Dialog();
        Assert.True(host.IsBusy);
        s.Switch(s.B);
        ((MessageBoxVM)confirm.DataContext!).YesCommand.Execute().Subscribe();
        var failure = await Dialog(vm => vm.Message.Contains("Cleanup could not complete"));
        ((MessageBoxVM)failure.DataContext!).OKCommand.Execute().Subscribe();
        await Idle(host);
        Assert.True(identity.MatchesCurrent(forceHash: true));
        Assert.Null(new FileInfo(target.ItemInfo.Path).LinkTarget);
        Assert.NotEqual(FileIdentity.Capture(target.ItemInfo.Path)!.StableFileId, FileIdentity.Capture(keeper.ItemInfo.Path)!.StableFileId);
        Assert.Contains(target, host.Duplicates);
        Assert.False(File.Exists(Path.Combine(s.B, "LegacyCleanupHistory.json")));
        await Assert.ThrowsAsync<IOException>(() => host.PrepareCleanupGuardAsync([target]));
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FolderEditAcrossFinalCleanupAwaitRefusesQuarantineAndPermanentRemoval(bool quarantine) => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        var original = FileIdentity.Capture(target.ItemInfo.Path)!;
        bool hookRan = false;
        string outcome = "Permanent removal did not reach the final authority callback.";
        host.BeforeCleanupAuthorityCheckAsync = async () => {
            await Task.Yield();
            hookRan = true;
            // This hook runs inside the current A protection check. An unsaved
            // edit avoids testing a competing policy writer instead of authority.
            s.Switch(s.B, save: false);
        };
        if (quarantine) {
            var preview = await host.CreateQuarantinePreviewAsync();
            await preview.ExecuteCommand.Execute();
            Assert.Contains("Quarantined 0 files", preview.Status);
            outcome = preview.Status;
        }
        else {
            var approved = await host.PrepareCleanupGuardAsync([target]);
            var action = approved[target];
            var guard = host.CreateResultsCleanupGuard(host.CaptureResultsStateAuthority());
            await Assert.ThrowsAsync<IOException>(() => guard.RemoveAsync(action.Target, action.Keeper,
                claim => File.Delete(claim), groupMembers: host.CaptureCompleteCleanupGroup(target.ItemInfo.GroupId)));
        }
        Assert.True(hookRan, outcome);
        // The verified rollback keeps the native source and bytes, but its
        // rename changes the change stamp, so the old scan cannot be reused.
        var restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.ItemInfo.Path));
        Assert.Equal(original.StableFileId, restored.StableFileId);
        Assert.Equal(original.Length, restored.Length);
        Assert.Equal(original.Sha256, restored.Sha256);
        Assert.True(File.Exists(keeper.ItemInfo.Path));
        Assert.Contains(target, host.Duplicates);
        Assert.False(File.Exists(Path.Combine(s.B, "ReviewedFiles.json")));
    }));

    [Fact]
    public Task ReviewSaveAcrossFolderEditAndRescanFlushStayInProducingJournal() => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, _) = await Pair(s);
        target.ManuallyProtected = true;
        target.ManualReviewChoice = ReviewChoice.Keep;
        host.BeforeAutomationReviewCommitAsync = async () => { await Task.Yield(); s.Switch(s.B); };
        await host.SaveAutomationReviewRecordsAsync();
        host.BeforeAutomationReviewCommitAsync = null;
        Assert.True((await Read(s.A)).Find(target.ItemInfo.Path)!.ManuallyProtected);
        Assert.Empty((await Read(s.B)).Records);
        target.ManualReviewChoice = ReviewChoice.Ignore;
        await host.PrepareAutomationRescanAsync();
        Assert.Equal(ReviewChoice.Ignore, (await Read(s.A)).Find(target.ItemInfo.Path)!.Choice);
        Assert.Empty((await Read(s.B)).Records);
        Assert.Equal(Path.Combine(s.A, "backup.scanresults"), host.BackupScanResultsFile);
    }));

    [Fact]
    public Task FolderEditDuringInspectionRetainsDisplayedReviewAndRefusesPreview() => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, _) = await Pair(s);
        await host.ProtectAutomationItemCommand.Execute(target);
        var baseline = target.ObservedReview;
        host.BeforeAutomationInspectionAppliedAsync = async () => { await Task.Yield(); s.Switch(s.B); };
        await Assert.ThrowsAsync<IOException>(() => host.CreateQuarantinePreviewAsync());
        host.BeforeAutomationInspectionAppliedAsync = null;
        Assert.True(target.ManuallyProtected);
        Assert.Same(baseline, target.ObservedReview);
        Assert.False(target.AutomationSnapshotInvalid);
        Assert.Empty((await Read(s.B)).Records);
        s.Switch(s.A);
        await host.InitialiseAutomationReviewAsync();
        Assert.True(target.ManuallyProtected);
    }));

    [Fact]
    public Task ExplicitNewScanFlushesAThenFreshResultsBindBAndRejectOldPreview() => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        var preview = await host.CreateQuarantinePreviewAsync();
        // An immediate choice has a pending debounce when the explicit switch starts.
        keeper.Checked = false;
        host.TrackReviewChange(keeper);
        s.Switch(s.B, save: false);
        var command = host.NewScanCommand.Execute().ToTask();
        var confirm = await Dialog();
        ((MessageBoxVM)confirm.DataContext!).YesCommand.Execute().Subscribe();
        await command;
        Assert.Empty(host.Duplicates);
        Assert.Equal(ReviewChoice.Keep, (await Read(s.A)).Find(keeper.ItemInfo.Path)!.Choice);
        Assert.False(File.Exists(Path.Combine(s.B, "ReviewedFiles.json")));

        s.Switch(s.B);
        host.Scanner.Settings.CustomDatabaseFolder = s.B;
        host.Scanner.Duplicates.Add(target.ItemInfo);
        host.Scanner.Duplicates.Add(keeper.ItemInfo);
        await host.FinishScanResultsAsync();
        Assert.Equal(Path.Combine(s.B, "ReviewedFiles.json"), host.AutomationReviewFile);
        Assert.Equal(2, host.Duplicates.Count);
        Assert.All(host.Duplicates, row => Assert.Equal(ReviewChoice.None, row.ManualReviewChoice));
        host.Duplicates.Single(row => row.ItemInfo.Path == target.ItemInfo.Path).Checked = true;
        await preview.ExecuteCommand.Execute();
        Assert.False(preview.IsComplete);
        Assert.Contains("replaced", preview.Status);
        Assert.True(File.Exists(target.ItemInfo.Path));
        var fresh = await host.CreateQuarantinePreviewAsync();
        Assert.True(Assert.Single(fresh.Items).CanQuarantine);
    }));

    [Fact]
    public Task ScannerCompletionUsesProducingScannerFolderDespiteLiveSettingsEdit() => HeadlessUi.Run(() => WithSettings(async s => {
        var host = s.Host();
        var group = Guid.NewGuid();
        var target = Add(host, Path.Combine(s.Root, "scanned-target.mp4"), group);
        var keeper = Add(host, Path.Combine(s.Root, "scanned-keeper.mp4"), group);
        host.Duplicates.Clear();
        host.Scanner.Settings.CustomDatabaseFolder = s.A;
        host.Scanner.Duplicates.Add(target.ItemInfo);
        host.Scanner.Duplicates.Add(keeper.ItemInfo);
        s.Switch(s.B);
        await host.FinishScanResultsAsync();
        Assert.Equal(Path.Combine(s.A, "ReviewedFiles.json"), host.AutomationReviewFile);
        Assert.Equal(2, host.Duplicates.Count);
        await Assert.ThrowsAsync<IOException>(() => host.PrepareCleanupGuardAsync(host.Duplicates.ToArray()));
        Assert.Empty((await Read(s.B)).Records);
        s.Switch(s.A);
        await host.InitialiseAutomationReviewAsync();
        host.Duplicates.Single(row => row.ItemInfo.Path == target.ItemInfo.Path).Checked = true;
        Assert.Single(await host.PrepareCleanupGuardAsync([host.Duplicates.Single(row => row.Checked)]));
    }));

    [Fact]
    public Task BackupRestoreAndExplicitImportBindTheirCapturedState() => HeadlessUi.Run(() => WithSettings(async s => {
        var (host, target, keeper) = await Pair(s);
        string backup = Path.Combine(s.A, "backup.scanresults");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create)) {
            using var writer = new StreamWriter(zip.CreateEntry("scan.json").Open());
            writer.Write(JsonSerializer.Serialize(new List<DuplicateItemVM> { target, keeper }, GuiJsonFieldsContext.Default.ListDuplicateItemVM));
        }
        await host.PrepareAutomationRescanAsync();
        host.DiscardResultsToSetup();
        SettingsFile.Instance.EnableScheduledScan = true; // Avoid thumbnail prompt for this startup-style restore.
        await host.RestoreBackupScanResultsAsync();
        Assert.Equal(Path.Combine(s.A, "ReviewedFiles.json"), host.AutomationReviewFile);
        Assert.Equal(2, host.Duplicates.Count);
        Assert.Equal(ReviewChoice.Remove, host.Duplicates.Single(row => row.ItemInfo.Path == target.ItemInfo.Path).ManualReviewChoice);
        await host.PrepareAutomationRescanAsync();
        host.DiscardResultsToSetup();
        s.Switch(s.B);
        await host.ImportScanResultsIncludingThumbnails(backup, startup: true);
        Assert.Equal(Path.Combine(s.B, "ReviewedFiles.json"), host.AutomationReviewFile);
        Assert.Equal(2, host.Duplicates.Count);
        Assert.All(host.Duplicates, row => Assert.Equal(ReviewChoice.None, row.ManualReviewChoice));
        Assert.Equal(ReviewChoice.Remove, (await Read(s.A)).Find(target.ItemInfo.Path)!.Choice);
    }));
}
