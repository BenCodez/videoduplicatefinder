using System.Reactive.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.HeadlessTests;

public sealed class AutomationIntegrationTests : IDisposable {
	readonly string folder = Directory.CreateTempSubdirectory("vdf-automation-ui-").FullName;
	public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

	DuplicateItemVM Add(MainWindowVM vm, Guid group, string name, int year) {
		string path = Path.Combine(folder, name);
		if (!File.Exists(path)) { File.WriteAllBytes(path, [1, 2, 3, 4]); File.SetLastWriteTimeUtc(path, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)); }
		var info = new FileInfo(path);
		var row = new DuplicateItemVM(new DuplicateItem {
			Path = path, Folder = folder, GroupId = group, SizeLong = info.Length, DateModified = info.LastWriteTimeUtc,
			DateCreated = info.CreationTimeUtc, FrameSize = "1920x1080", Format = "h264", Duration = TimeSpan.FromMinutes(1)
		});
		vm.Duplicates.Add(row);
		return row;
	}

	async Task WithSettings(Func<Task> work) {
		var settings = SettingsFile.Instance;
		string database = settings.CustomDatabaseFolder;
		var selection = settings.AutomationSelection;
		var nfo = settings.NfoFavourites;
		try {
			settings.CustomDatabaseFolder = folder;
			settings.AutomationSelection = new() { AutoAfterScan = true, Profile = SelectionProfile.NewestFirst };
			settings.NfoFavourites = new();
			await work();
		}
		finally { settings.CustomDatabaseFolder = database; settings.AutomationSelection = selection; settings.NfoFavourites = nfo; }
	}

	[Fact]
	public Task StartupRecoveryReservesTheScanUntilAllStateHasLoaded() => HeadlessUi.Run(() => WithSettings(async () => {
		bool scheduled = SettingsFile.Instance.EnableScheduledScan;
		SettingsFile.Instance.EnableScheduledScan = false;
		using var releaseClaim = new ManualResetEventSlim();
		var claimEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		string targetPath = Path.Combine(folder, "startup-target.mp4");
		string keeperPath = Path.Combine(folder, "startup-keeper.mp4");
		File.WriteAllBytes(targetPath, [1, 2, 3]);
		File.WriteAllBytes(keeperPath, [1, 2, 3]);
		var service = new LegacyRemovalService(Path.Combine(folder, "LegacyCleanupHistory.json"));
		var removal = Task.Run(() => service.RemoveAsync(FileIdentity.Capture(targetPath)!, FileIdentity.Capture(keeperPath)!, claim => {
			claimEntered.TrySetResult(true);
			if (!releaseClaim.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release the private claim.");
			File.Delete(claim);
		}));
		Task? startup = null;
		try {
			await claimEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			var vm = new MainWindowVM();
			startup = vm.LoadStartupStateAsync();
			Assert.False(startup.IsCompleted);
			Assert.True(vm.IsStartupLoading);
			Assert.False(await vm.StartScanCommand.CanExecute.FirstAsync());
			// Execute also checks the lifecycle guard, independently of the button binding.
			await vm.StartScanCommand.Execute("ScheduledFullScan");
			Assert.False(vm.HasActiveScanRun || vm.IsScanning);
			releaseClaim.Set();
			await removal;
			await startup;
			Assert.False(vm.IsStartupLoading);
			Assert.True(await vm.StartScanCommand.CanExecute.FirstAsync());
		}
		finally {
			releaseClaim.Set();
			await removal;
			if (startup != null) await startup;
			SettingsFile.Instance.EnableScheduledScan = scheduled;
		}
	}));

	[Fact]
	public Task ScanResultProcessingBlocksImportsAndDatabaseChangesUntilReviewCompletes() => HeadlessUi.Run(() => WithSettings(async () => {
		bool previews = SettingsFile.Instance.GeneratePreviewThumbnails;
		SettingsFile.Instance.GeneratePreviewThumbnails = false;
		SettingsFile.Instance.AutomationSelection.AutoAfterScan = false;
		try {
			var vm = new MainWindowVM();
			vm.Scanner.Settings.CustomDatabaseFolder = folder; // The scanner produced these results in this state.
			var group = Guid.NewGuid();
			for (int i = 0; i < 5000; i++) vm.Scanner.Duplicates.Add(new DuplicateItem {
				Path = Path.Combine(folder, $"missing-{i}.mp4"), Folder = folder, GroupId = group, SizeLong = 1
			});
			var processing = vm.FinishScanResultsAsync();
			Assert.False(processing.IsCompleted);
			Assert.True(vm.IsBusy);
			Assert.False(await vm.ImportScanResultsFromFileCommand.CanExecute.FirstAsync());
			Assert.False(await vm.ClearDatabaseCommand.CanExecute.FirstAsync());
			Assert.False(await vm.CleanDatabaseCommand.CanExecute.FirstAsync());
			Assert.False(await vm.ImportDataBaseFromJsonCommand.CanExecute.FirstAsync());
			Assert.False(await vm.RelocateDatabaseFilesCommand.CanExecute.FirstAsync());
			await vm.ImportScanResultsIncludingThumbnails(Path.Combine(folder, "missing-backup.zip"));
			await processing;
			Assert.False(vm.IsBusy);
			Assert.Equal(5000, vm.Duplicates.Count);
			Assert.Equal(5000, vm.Duplicates.Select(row => row.ItemInfo.Path).Distinct().Count());
		}
		finally { SettingsFile.Instance.GeneratePreviewThumbnails = previews; }
	}));

	[Fact]
	public Task SettingsDialogLoadsWithNativeControlsAndAnIsolatedDraft() => HeadlessUi.Run(() => {
		var dialog = new AutomationSettingsView();
		dialog.Show(); HeadlessUi.Pump();
		try {
			Assert.NotNull(dialog.FindControl<ComboBox>("ProfileCombo"));
			Assert.NotNull(dialog.FindControl<ItemsControl>("FolderRulesList"));
			Assert.NotNull(dialog.FindControl<CheckBox>("EnableNfoCheck"));
			Assert.NotNull(dialog.FindControl<Button>("SaveAutomationSettingsButton"));
			Assert.NotSame(SettingsFile.Instance.AutomationSelection, dialog.ViewModel.Selection);
			AccessibleNameTests.AssertEverythingIsNamed(dialog);
			string? screenshot = Environment.GetEnvironmentVariable("VDF_AUTOMATION_UI_SCREENSHOT");
			if (!string.IsNullOrWhiteSpace(screenshot)) { using var frame = dialog.CaptureRenderedFrame(); frame?.Save(screenshot); }
			dialog.ViewModel.SavedProfileName = "Test keeper profile";
			dialog.ViewModel.SaveNamedProfileCommand.Execute().Subscribe();
			Assert.Contains(dialog.ViewModel.SavedProfiles, p => p.Name == "Test keeper profile");
			Assert.DoesNotContain(SettingsFile.Instance.SavedSelectionProfiles, p => p.Name == "Test keeper profile");
		}
		finally { dialog.Close(); HeadlessUi.Pump(); }
	});

	[Fact]
	public Task ScanHookRestoresFavouritesEvenWhenAutomaticSelectionIsOff() => HeadlessUi.Run(() => WithSettings(async () => {
		SettingsFile.Instance.AutomationSelection.AutoAfterScan = false;
		SettingsFile.Instance.NfoFavourites = new NfoOptions { Enabled = true };
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var a = Add(vm, group, "a.mp4", 2020); var b = Add(vm, group, "b.mp4", 2025);
		b.Checked = true;
		File.WriteAllText(Path.ChangeExtension(b.ItemInfo.Path, ".nfo"), "<movie><favorite>true</favorite></movie>");
		await vm.ApplyAutomaticSelectionAsync();
		Assert.True(b.IsNfoFavourite && b.IsProtected);
		Assert.False(b.Checked || a.Checked);
		b.Checked = true;
		Assert.False(b.Checked);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task ComparerKeeperIsSavedWhenAlreadyUncheckedAndSurvivesANewerCopyOnRescan() => HeadlessUi.Run(() => WithSettings(async () => {
		foreach (bool keepLeft in new[] { true, false }) {
			var vm = new MainWindowVM(); var group = Guid.NewGuid();
			var left = Add(vm, group, $"comparer-{keepLeft}-left.mp4", 2020);
			var right = Add(vm, group, $"comparer-{keepLeft}-right.mp4", 2025);
			var comparer = new ThumbnailComparerVM([new(left), new(right)]);
			comparer.AssignDefaultSelections();
			await (keepLeft ? comparer.KeepLeftCommand : comparer.KeepRightCommand).Execute();
			var keeper = keepLeft ? left : right;
			var removed = keepLeft ? right : left;
			Assert.Equal(ReviewChoice.Keep, keeper.ManualReviewChoice);
			Assert.Equal(ReviewChoice.Remove, removed.ManualReviewChoice);
			Assert.Equal(1, vm.DuplicatesCheckedCounter);
			await vm.PrepareAutomationRescanAsync();
			var store = new ReviewStore(vm.AutomationReviewFile); await store.LoadAsync();
			Assert.Equal(ReviewChoice.Keep, store.Find(keeper.ItemInfo.Path)!.Choice);
			var next = new MainWindowVM(); var nextGroup = Guid.NewGuid();
			var restoredLeft = Add(next, nextGroup, Path.GetFileName(left.ItemInfo.Path), 2020);
			var restoredRight = Add(next, nextGroup, Path.GetFileName(right.ItemInfo.Path), 2025);
			Add(next, nextGroup, $"comparer-{keepLeft}-newest.mp4", 2030);
			await next.ApplyAutomaticSelectionAsync();
			Assert.False((keepLeft ? restoredLeft : restoredRight).Checked);
			Assert.Equal(ReviewChoice.Keep, (keepLeft ? restoredLeft : restoredRight).ManualReviewChoice);
			await next.PrepareAutomationRescanAsync();
		}
	}));

	[Fact]
	public Task ManualKeeperSurvivesReplanningAndChangedScanGroupIds() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var a = Add(vm, group, "old.mp4", 2020); var b = Add(vm, group, "new.mp4", 2025);
		await vm.ApplyAutomaticSelectionAsync();
		Assert.True(a.Checked); Assert.False(b.Checked);
		a.Checked = false;
		await vm.ApplyAutomaticSelectionAsync();
		Assert.Equal(ReviewChoice.Keep, a.ManualReviewChoice);
		Assert.False(a.Checked); Assert.True(b.Checked);
		await vm.PrepareAutomationRescanAsync();
		var next = new MainWindowVM(); var newGroup = Guid.NewGuid();
		var restoredA = Add(next, newGroup, "old.mp4", 2020); var restoredB = Add(next, newGroup, "new.mp4", 2025);
		await next.ApplyAutomaticSelectionAsync();
		Assert.Equal(ReviewChoice.Keep, restoredA.ManualReviewChoice);
		Assert.False(restoredA.Checked); Assert.True(restoredB.Checked);
		await next.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task DeliberateCheckboxChangeOverridesManualKeeperWhileAutomaticSelectionPreservesIt() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var old = Add(vm, group, "override-old.mp4", 2020); var newest = Add(vm, group, "override-new.mp4", 2025);
		await vm.ApplyAutomaticSelectionAsync();
		old.Checked = false;
		Assert.Equal(ReviewChoice.Keep, old.ManualReviewChoice);
		await vm.ApplyAutomaticSelectionAsync();
		Assert.False(old.Checked); Assert.True(newest.Checked);
		old.Checked = true;
		Assert.Equal(ReviewChoice.Remove, old.ManualReviewChoice);
		await vm.ApplyAutomaticSelectionAsync();
		Assert.True(old.Checked); Assert.False(newest.Checked);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task IgnoredGroupIncludesNewMembersOnRescanAndAutomaticScopeIsAllGroups() => HeadlessUi.Run(() => WithSettings(async () => {
		SettingsFile.Instance.AutomationSelection.HighlightedGroupsOnly = true;
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var a = Add(vm, group, "ignored-a.mp4", 2020); Add(vm, group, "ignored-b.mp4", 2025);
		await vm.IgnoreAutomationGroupCommand.Execute(a);
		await vm.PrepareAutomationRescanAsync();
		var next = new MainWindowVM(); var newGroup = Guid.NewGuid();
		var old = Add(next, newGroup, "ignored-a.mp4", 2020); var fresh = Add(next, newGroup, "new-member.mp4", 2026);
		var other = Guid.NewGuid(); var otherOld = Add(next, other, "other-old.mp4", 2019); var otherNew = Add(next, other, "other-new.mp4", 2024);
		await next.ApplyAutomaticSelectionAsync();
		Assert.False(old.Checked || fresh.Checked);
		Assert.True(fresh.IsAutomationGroupIgnored);
		Assert.Equal(ReviewChoice.Ignore, fresh.ManualReviewChoice);
		fresh.Checked = true;
		Assert.False(fresh.Checked);
		Assert.True(otherOld.Checked); Assert.False(otherNew.Checked);
		await next.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task ChangedFileInvalidatesReviewAndCannotAcquireReplacementIdentity() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var row = Add(vm, group, "replacement.mp4", 2020); Add(vm, group, "survivor.mp4", 2025);
		await vm.ProtectAutomationItemCommand.Execute(row);
		File.Delete(row.ItemInfo.Path);
		File.WriteAllBytes(row.ItemInfo.Path, [5, 6, 7, 8]);
		await vm.InitialiseAutomationReviewAsync();
		Assert.True(row.ProtectionUncertain && row.IsProtected);
		Assert.Null(row.AutomationIdentity);
		Assert.False(row.Checked);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task FreshScanSnapshotRejectsReplacementBeforeDelayedIdentityCapture() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var row = Add(vm, group, "fresh-replacement.mp4", 2020); Add(vm, group, "fresh-survivor.mp4", 2025);
		DateTime modified = row.ItemInfo.DateModified;
		File.Delete(row.ItemInfo.Path); File.WriteAllBytes(row.ItemInfo.Path, [1, 2, 3, 4]); File.SetLastWriteTimeUtc(row.ItemInfo.Path, modified);
		row.ItemInfo.DateCreated = new FileInfo(row.ItemInfo.Path).CreationTimeUtc;
		row.Checked = true;
		await vm.SaveAutomationReviewRecordsAsync();
		Assert.Null(row.AutomationIdentity);
		await vm.InitialiseAutomationReviewAsync();
		Assert.True(row.ProtectionUncertain && row.AutomationSnapshotInvalid);
		Assert.False(row.Checked);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task LockedFilePreservesItsProtectionAcrossSavingAndReplanning() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var row = Add(vm, group, "locked.mp4", 2020); Add(vm, group, "lock-survivor.mp4", 2025);
		await vm.ProtectAutomationItemCommand.Execute(row);
		var identity = row.AutomationIdentity;
		Assert.NotNull(identity);
		using (var held = new FileStream(row.ItemInfo.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
			Assert.True(File.Exists(row.ItemInfo.Path));
			Assert.Null(FileIdentity.Capture(row.ItemInfo.Path));
			await vm.SaveAutomationReviewRecordsAsync();
			await vm.InitialiseAutomationReviewAsync();
			Assert.True(row.ManuallyProtected && row.IsProtected && row.ProtectionUncertain);
			Assert.False(row.AutomationSnapshotInvalid);
			Assert.Equal(identity, row.AutomationIdentity);
			var store = new ReviewStore(vm.AutomationReviewFile); await store.LoadAsync();
			Assert.True(store.Records.Single(r => r.Identity.Path == row.ItemInfo.Path).ManuallyProtected);
		}
		await vm.InitialiseAutomationReviewAsync();
		Assert.True(row.ManuallyProtected && row.IsProtected);
		Assert.False(row.ProtectionUncertain);
		Assert.True((await new CleanupProtectionGuard(folder).InspectAsync(identity!)).IsProtected);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task UnavailableFileRetainsItsStoredManualProtection() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var row = Add(vm, group, "offline.mp4", 2020); Add(vm, group, "mounted.mp4", 2025);
		await vm.ProtectAutomationItemCommand.Execute(row);
		string held = row.ItemInfo.Path + ".unavailable";
		File.Move(row.ItemInfo.Path, held);
		try {
			await vm.InitialiseAutomationReviewAsync();
			Assert.True(row.ManuallyProtected && row.IsProtected);
			Assert.NotNull(row.AutomationIdentity);
			await vm.PrepareAutomationRescanAsync();
			var store = new ReviewStore(vm.AutomationReviewFile); await store.LoadAsync();
			Assert.True(store.Records.Single(r => r.Identity.Path == row.ItemInfo.Path).ManuallyProtected);
		}
		finally { File.Move(held, row.ItemInfo.Path); }
	}));

	[Fact]
	public Task LegacyBackupChecksAreDiscardedEvenWhenReplacementCopiesDates() => HeadlessUi.Run(() => WithSettings(async () => {
		SettingsFile.Instance.AutomationSelection.AutoAfterScan = false;
		string path = Path.Combine(folder, "legacy.mp4");
		File.WriteAllBytes(path, [1, 2, 3, 4]);
		DateTime modified = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(path, modified);
		var item = new DuplicateItemVM(new DuplicateItem { Path = path, Folder = folder, GroupId = Guid.NewGuid(), SizeLong = 4, DateModified = modified });
		item.Checked = true;
		string backup = JsonSerializer.Serialize(new List<DuplicateItemVM> { item }, GuiJsonFieldsContext.Default.ListDuplicateItemVM);
		File.Delete(path); File.WriteAllBytes(path, [9, 8, 7, 6]); File.SetLastWriteTimeUtc(path, modified);
		var restored = JsonSerializer.Deserialize(backup, GuiJsonFieldsContext.Default.ListDuplicateItemVM)!;
		Assert.False(restored.Single().Checked);
		var vm = new MainWindowVM(); vm.AddDuplicatesInBulk(restored);
		await vm.InitialiseAutomationReviewAsync();
		Assert.False(restored.Single().Checked);
		Assert.Null(restored.Single().AutomationIdentity);
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task QuarantinePreviewShowsManualByteDifferenceAndDoesNotMoveFiles() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		File.WriteAllBytes(Path.Combine(folder, "target.mp4"), [9, 9, 9, 9]);
		var target = Add(vm, group, "target.mp4", 2020); var keeper = Add(vm, group, "keeper.mp4", 2025);
		target.Checked = true;
		var preview = await vm.CreateQuarantinePreviewAsync();
		var entry = Assert.Single(preview.Items);
		Assert.True(entry.CanQuarantine); Assert.False(entry.ByteIdentical);
		Assert.Equal(keeper.ItemInfo.Path, entry.KeeperPath);
		Assert.True(File.Exists(target.ItemInfo.Path));
		var dialog = new QuarantinePreviewView(preview); dialog.Show(); HeadlessUi.Pump();
		Assert.NotNull(dialog.FindControl<Button>("ConfirmQuarantineButton"));
		AccessibleNameTests.AssertEverythingIsNamed(dialog);
		dialog.Close();
		await vm.PrepareAutomationRescanAsync();
	}));

	[Fact]
	public Task QuarantineCommandMovesAfterConfirmationAndHistoryRestoresTheOriginal() => HeadlessUi.Run(() => WithSettings(async () => {
		var vm = new MainWindowVM(); var group = Guid.NewGuid();
		var target = Add(vm, group, "recover-target.mp4", 2020); var keeper = Add(vm, group, "recover-keeper.mp4", 2025);
		target.Checked = true;
		var preview = await vm.CreateQuarantinePreviewAsync();
		Assert.True(Assert.Single(preview.Items).CanQuarantine);
		await preview.ExecuteCommand.Execute();
		Assert.False(File.Exists(target.ItemInfo.Path)); Assert.True(File.Exists(keeper.ItemInfo.Path));
		Assert.DoesNotContain(target, vm.Duplicates);
		var service = new QuarantineService(vm.QuarantineHistoryFile);
		var record = Assert.Single(await service.HistoryAsync());
		Assert.Equal(QuarantineState.Quarantined, record.State);
		var settings = new AutomationSettingsVM(vm);
		await settings.ReloadHistoryAsync();
		await settings.RestoreQuarantineCommand.Execute(settings.QuarantineHistory.Single());
		Assert.True(File.Exists(target.ItemInfo.Path));
		Assert.Equal(QuarantineState.Restored, Assert.Single(await service.HistoryAsync()).State);
		await vm.PrepareAutomationRescanAsync();
	}));
}
