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
        var info = new FileInfo(path);
        var row = new DuplicateItemVM(new DuplicateItem {
            Path = path, Folder = folder, GroupId = group, SizeLong = info.Length,
            DateModified = info.LastWriteTimeUtc, DateCreated = info.CreationTimeUtc
        });
        vm.Duplicates.Add(row);
        return row;
    }

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
