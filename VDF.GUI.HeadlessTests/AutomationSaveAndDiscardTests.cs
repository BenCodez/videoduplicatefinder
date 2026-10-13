using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.HeadlessTests;

public sealed class AutomationSaveAndDiscardTests : IDisposable {
	readonly string folder = Directory.CreateTempSubdirectory("vdf-automation-transactions-").FullName;
	public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

	async Task WithSettings(Func<Task> work) {
		HeadlessUi.Shell();
		var settings = SettingsFile.Instance;
		string database = settings.CustomDatabaseFolder;
		var selection = settings.AutomationSelection;
		var profiles = settings.SavedSelectionProfiles;
		var nfo = settings.NfoFavourites;
		string quarantine = settings.QuarantineFolder;
		try {
			settings.CustomDatabaseFolder = folder;
			settings.AutomationSelection = new() { Profile = SelectionProfile.OldestFirst };
			settings.SavedSelectionProfiles = [new() { Name = "Previous profile", Options = new() { Profile = SelectionProfile.OldestFirst } }];
			settings.NfoFavourites = new() { Enabled = true };
			settings.QuarantineFolder = Path.Combine(folder, "previous-quarantine");
			await work();
		}
		finally {
			SettingsFile.SetSettingsPath(null);
			settings.CustomDatabaseFolder = database;
			settings.AutomationSelection = selection;
			settings.SavedSelectionProfiles = profiles;
			settings.NfoFavourites = nfo;
			settings.QuarantineFolder = quarantine;
		}
	}

	DuplicateItemVM Add(MainWindowVM vm, string name) {
		string path = Path.Combine(folder, name);
		File.WriteAllBytes(path, [1, 2, 3, 4]);
		var info = new FileInfo(path);
		var row = new DuplicateItemVM(new DuplicateItem {
			Path = path, Folder = folder, GroupId = Guid.NewGuid(), SizeLong = info.Length,
			DateCreated = info.CreationTimeUtc, DateModified = info.LastWriteTimeUtc
		});
		vm.Duplicates.Add(row);
		return row;
	}

	static async Task<MessageBoxView> WaitForMessageAsync() {
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline) {
			var dialog = ApplicationHelpers.MainWindow.OwnedWindows.OfType<MessageBoxView>().FirstOrDefault(w => w.IsVisible);
			if (dialog != null) return dialog;
			HeadlessUi.Pump();
			await Task.Delay(10);
		}
		throw new TimeoutException("The command did not display its expected dialog.");
	}

	[Fact]
	public Task FailedReviewRefreshRollsBackActiveAndPersistedSettings() => HeadlessUi.Run(() => WithSettings(async () => {
		string path = Path.Combine(folder, "Settings.json");
		SettingsFile.SetSettingsPath(path);
		var settings = SettingsFile.Instance;
		var oldSelection = settings.AutomationSelection;
		var oldProfiles = settings.SavedSelectionProfiles;
		var oldNfo = settings.NfoFavourites;
		string oldQuarantine = settings.QuarantineFolder;
		Directory.CreateDirectory(Path.Combine(folder, "ReviewedFiles.json"));
		var draft = new AutomationSettingsVM(new MainWindowVM());
		draft.Selection.Profile = SelectionProfile.NewestFirst;
		draft.Nfo.Enabled = false;
		draft.SavedProfiles.Clear();
		draft.QuarantineFolder = Path.Combine(folder, "draft-quarantine");

		Assert.False(await draft.SaveAsync());

		Assert.Same(oldSelection, settings.AutomationSelection);
		Assert.Same(oldProfiles, settings.SavedSelectionProfiles);
		Assert.Same(oldNfo, settings.NfoFavourites);
		Assert.Equal(oldQuarantine, settings.QuarantineFolder);
		var persisted = JsonSerializer.Deserialize(File.ReadAllText(path), GuiJsonContext.Default.SettingsFile)!;
		Assert.Equal(SelectionProfile.OldestFirst, persisted.AutomationSelection.Profile);
		Assert.True(persisted.NfoFavourites.Enabled);
		Assert.Equal("Previous profile", persisted.SavedSelectionProfiles.Single().Name);
		Assert.Equal(oldQuarantine, persisted.QuarantineFolder);
	}));

	[Fact]
	public Task NewScanFlushesImmediateCheckboxDecisionBeforeClearingResults() => HeadlessUi.Run(() => WithSettings(async () => {
		HeadlessUi.Shell();
		var vm = new MainWindowVM();
		var row = Add(vm, "review-now.mp4");
		row.Checked = true;
		Task command = vm.NewScanCommand.Execute().ToTask();
		var dialog = await WaitForMessageAsync();
		Assert.True(vm.IsBusy);
		Assert.False(File.Exists(vm.AutomationReviewFile));
		((MessageBoxVM)dialog.DataContext!).YesCommand.Execute().Subscribe();
		await command.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Empty(vm.Duplicates);
		Assert.True(vm.IsSetupState);
		Assert.False(vm.IsBusy);
		var store = new ReviewStore(vm.AutomationReviewFile);
		await store.LoadAsync();
		Assert.Equal(ReviewChoice.Remove, store.Find(row.ItemInfo.Path)!.Choice);
	}));

	[Fact]
	public Task NewScanRetainsResultsWhenReviewFlushFails() => HeadlessUi.Run(() => WithSettings(async () => {
		HeadlessUi.Shell();
		var vm = new MainWindowVM();
		var row = Add(vm, "review-retained.mp4");
		Directory.CreateDirectory(vm.AutomationReviewFile);
		row.Checked = true;
		Task command = vm.NewScanCommand.Execute().ToTask();
		var prompt = await WaitForMessageAsync();
		((MessageBoxVM)prompt.DataContext!).YesCommand.Execute().Subscribe();
		var failure = await WaitForMessageAsync();
		Assert.Contains("current results were retained", ((MessageBoxVM)failure.DataContext!).Message);
		((MessageBoxVM)failure.DataContext!).OKCommand.Execute().Subscribe();
		await command.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Same(row, Assert.Single(vm.Duplicates));
		Assert.True(row.Checked);
		Assert.False(vm.IsBusy);
	}));
}
