using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Avalonia.Platform.Storage;
using ReactiveUI;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels;

/// <summary>An isolated settings draft; Cancel leaves the current configuration intact.</summary>
public sealed class AutomationSettingsVM : ReactiveObject {
	readonly MainWindowVM? host;
	readonly NfoFavouriteService nfo = new();
	public AutomationSettingsVM(MainWindowVM? host = null) {
		this.host = host;
		Selection = CloneSelectionOptions(SettingsFile.Instance.AutomationSelection ?? new());
		Nfo = CloneNfoOptions(SettingsFile.Instance.NfoFavourites ?? new());
		FolderRules = new(Selection.FolderRules);
		Criteria = new(Selection.Criteria);
		SavedProfiles = new((SettingsFile.Instance.SavedSelectionProfiles ?? new()).Select(p => new NamedSelectionProfile { Name = p.Name, Options = CloneSelectionOptions(p.Options ?? new()) }));
		PreferredCodecsText = string.Join(", ", Selection.PreferredCodecs);
		PreferredAudioLanguagesText = string.Join(", ", Selection.PreferredAudioLanguages);
		PreferredContainersText = string.Join(", ", Selection.PreferredContainers);
		BooleanElementsText = string.Join(Environment.NewLine, Nfo.BooleanElements);
		TagValuesText = string.Join(Environment.NewLine, Nfo.TagValues);
		TagElementsText = string.Join(Environment.NewLine, Nfo.TagElements);
		QuarantineFolder = SettingsFile.Instance.QuarantineFolder;
	}

	public SelectionOptions Selection {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	}
	public NfoOptions Nfo { get; }
	public ObservableCollection<FolderPriorityRule> FolderRules { get; }
	public ObservableCollection<SelectionCriterion> Criteria { get; }
	public ObservableCollection<NamedSelectionProfile> SavedProfiles { get; }
	public NamedSelectionProfile? SelectedSavedProfile { get; set; }
	public string SavedProfileName { get; set; } = string.Empty;
	public ObservableCollection<QuarantineRecord> QuarantineHistory { get; } = new();
	public IEnumerable<SelectionProfile> Profiles => Enum.GetValues<SelectionProfile>();
	public IEnumerable<SelectionMode> Modes => Enum.GetValues<SelectionMode>();
	public IEnumerable<SelectionDateSource> DateSources => Enum.GetValues<SelectionDateSource>();
	public IEnumerable<FolderOverlapPolicy> OverlapPolicies => Enum.GetValues<FolderOverlapPolicy>();
	public IEnumerable<string> CriterionNames => new[] { "Resolution", "VideoBitrate", "AudioBitrate", "AudioChannels", "Codec", "Hdr", "AudioLanguage", "Subtitles", "Container", "Date", "ModificationDate", "CreationDate", "Duration", "Size", "Metadata" };
	public string PreferredCodecsText { get; set; }
	public string PreferredAudioLanguagesText { get; set; }
	public string PreferredContainersText { get; set; }
	public string BooleanElementsText { get; set; }
	public string TagValuesText { get; set; }
	public string TagElementsText { get; set; }
	public string QuarantineFolder { get; set; }
	public string NfoTestPath { get; set; } = string.Empty;
	public string NfoTestResult {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	} = string.Empty;
	public string Status {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	} = string.Empty;
	public bool IsWorking {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	}
	public ReactiveCommand<Unit, Unit> AddFolderRuleCommand => ReactiveCommand.Create(() => FolderRules.Add(new FolderPriorityRule()));
	public ReactiveCommand<Unit, Unit> SaveNamedProfileCommand => ReactiveCommand.Create(() => {
		string name = SavedProfileName.Trim();
		if (name.Length == 0) { Status = "Enter a name for this selection profile."; return; }
		var existing = SavedProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		if (existing != null) SavedProfiles.Remove(existing);
		var profile = new NamedSelectionProfile { Name = name, Options = BuildSelectionOptions() };
		SavedProfiles.Add(profile);
		SelectedSavedProfile = profile;
		this.RaisePropertyChanged(nameof(SelectedSavedProfile));
		Status = "Profile added to this draft. Save settings to persist it.";
	});
	public ReactiveCommand<Unit, Unit> DeleteNamedProfileCommand => ReactiveCommand.Create(() => {
		if (SelectedSavedProfile != null) SavedProfiles.Remove(SelectedSavedProfile);
		SelectedSavedProfile = null;
		this.RaisePropertyChanged(nameof(SelectedSavedProfile));
	});
	public ReactiveCommand<Unit, Unit> LoadNamedProfileCommand => ReactiveCommand.Create(() => {
		if (SelectedSavedProfile == null) return;
		Selection = CloneSelectionOptions(SelectedSavedProfile.Options);
		FolderRules.Clear(); foreach (var rule in Selection.FolderRules) FolderRules.Add(rule);
		Criteria.Clear(); foreach (var criterion in Selection.Criteria) Criteria.Add(criterion);
		PreferredCodecsText = string.Join(", ", Selection.PreferredCodecs);
		PreferredAudioLanguagesText = string.Join(", ", Selection.PreferredAudioLanguages);
		PreferredContainersText = string.Join(", ", Selection.PreferredContainers);
		foreach (var name in new[] { nameof(PreferredCodecsText), nameof(PreferredAudioLanguagesText), nameof(PreferredContainersText) }) this.RaisePropertyChanged(name);
	});
	public ReactiveCommand<FolderPriorityRule, Unit> RemoveFolderRuleCommand => ReactiveCommand.Create<FolderPriorityRule>(rule => FolderRules.Remove(rule));
	public ReactiveCommand<Unit, Unit> AddCriterionCommand => ReactiveCommand.Create(() => Criteria.Add(new SelectionCriterion()));
	public ReactiveCommand<SelectionCriterion, Unit> RemoveCriterionCommand => ReactiveCommand.Create<SelectionCriterion>(criterion => Criteria.Remove(criterion));
	public ReactiveCommand<SelectionCriterion, Unit> MoveCriterionUpCommand => ReactiveCommand.Create<SelectionCriterion>(criterion => {
		int index = Criteria.IndexOf(criterion);
		if (index > 0) Criteria.Move(index, index - 1);
	});
	public ReactiveCommand<SelectionCriterion, Unit> MoveCriterionDownCommand => ReactiveCommand.Create<SelectionCriterion>(criterion => {
		int index = Criteria.IndexOf(criterion);
		if (index >= 0 && index < Criteria.Count - 1) Criteria.Move(index, index + 1);
	});
	public ReactiveCommand<FolderPriorityRule, Unit> BrowseFolderRuleCommand => ReactiveCommand.CreateFromTask<FolderPriorityRule>(async rule => {
		var paths = await Utils.PickerDialogUtils.OpenDialogPicker(new FolderPickerOpenOptions { Title = "Choose priority folder", AllowMultiple = false });
		if (paths?.Count > 0) {
			int index = FolderRules.IndexOf(rule);
			var replacement = new FolderPriorityRule { Directory = paths[0], Priority = rule.Priority, Enabled = rule.Enabled,
				Recursive = rule.Recursive, ProtectAll = rule.ProtectAll, Pattern = rule.Pattern };
			if (index >= 0) FolderRules[index] = replacement;
		}
	});
	public ReactiveCommand<Unit, Unit> TestNfoCommand => ReactiveCommand.CreateFromTask(async () => {
		if (string.IsNullOrWhiteSpace(NfoTestPath)) { NfoTestResult = "Enter a media file path."; return; }
		IsWorking = true;
		try {
			var options = BuildNfoOptions();
			options.Enabled = true;
			string path = NfoTestPath;
			var result = await Task.Run(() => { nfo.ClearCache(); return nfo.Inspect(path, options); });
			NfoTestResult = (result.IsUncertain ? "Needs review: " : result.IsFavourite ? "Favourite: " : "No favourite marker: ") + result.Reason +
				(result.Sources.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, result.Sources));
		}
		catch (Exception ex) { NfoTestResult = ex.Message; }
		finally { IsWorking = false; }
	});
	public ReactiveCommand<Unit, Unit> ReloadHistoryCommand => ReactiveCommand.CreateFromTask(ReloadHistoryAsync);
	public ReactiveCommand<QuarantineRecord, Unit> RestoreQuarantineCommand => ReactiveCommand.CreateFromTask<QuarantineRecord>(async record => {
		if (record == null) return;
		IsWorking = true;
		try {
			await QuarantineService().RestoreAsync(record.OperationId);
			Status = $"Restored: {record.OriginalPath}. Rescan to include it in the results.";
			await ReloadHistoryAsync();
			if (host != null) await host.InitialiseAutomationReviewAsync();
		}
		catch (Exception ex) { Status = $"Restore failed: {ex.Message}"; }
		finally { IsWorking = false; }
	});

	QuarantineService QuarantineService() => new CleanupProtectionGuard(Path.GetDirectoryName(host?.QuarantineHistoryFile ??
		Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "QuarantineHistory.json"))!).CreateQuarantineService();
	public async Task ReloadHistoryAsync() {
		try {
			var records = await QuarantineService().HistoryAsync();
			QuarantineHistory.Clear();
			foreach (var record in records.OrderByDescending(r => r.CreatedUtc)) QuarantineHistory.Add(record);
		}
		catch (Exception ex) { Status = $"History could not be read: {ex.Message}"; }
	}

	internal SelectionOptions BuildSelectionOptions() {
		var options = CloneSelectionOptions(Selection);
		options.FolderRules = FolderRules.ToList();
		options.Criteria = Criteria.ToList();
		options.PreferredCodecs = ParseList(PreferredCodecsText);
		options.PreferredAudioLanguages = ParseList(PreferredAudioLanguagesText);
		options.PreferredContainers = ParseList(PreferredContainersText);
		return CloneSelectionOptions(options);
	}
	internal NfoOptions BuildNfoOptions() {
		var options = CloneNfoOptions(Nfo);
		options.BooleanElements = ParseList(BooleanElementsText);
		options.TagValues = ParseList(TagValuesText);
		options.TagElements = ParseList(TagElementsText);
		return options;
	}
	internal static List<string> ParseList(string? text) => (text ?? string.Empty).Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
		.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

	public async Task<bool> SaveAsync() {
		if (host != null && (host.IsBusy || host.IsScanning || host.IsAutomationWorking || host.IsStartupLoading || host.HasActiveScanRun)) { Status = "Wait for the current scan or operation to finish before changing these settings."; return false; }
		if (FolderRules.Any(r => r.Enabled && (string.IsNullOrWhiteSpace(r.Directory) || !Path.IsPathFullyQualified(r.Directory)))) {
			Status = "Every enabled folder rule needs an absolute folder path.";
			return false;
		}
		if (!double.IsFinite(Selection.FolderWeight) || Selection.FolderWeight < 0 || Criteria.Any(c => !double.IsFinite(c.Weight) || c.Weight < 0)) {
			Status = "Weights must be finite numbers of zero or more.";
			return false;
		}
		if (!string.IsNullOrWhiteSpace(QuarantineFolder) && !Path.IsPathFullyQualified(QuarantineFolder)) {
			Status = "The quarantine folder needs an absolute path.";
			return false;
		}
		IsWorking = true;
		if (host != null) { host.IsBusy = true; host.IsBusyOverlayText = "Saving selection and protection settings…"; }
		var settings = SettingsFile.Instance;
		var previousSelection = settings.AutomationSelection;
		var previousProfiles = settings.SavedSelectionProfiles;
		var previousNfo = settings.NfoFavourites;
		string previousQuarantine = settings.QuarantineFolder;
		bool saveAttempted = false;
		bool reviewRefreshStarted = false;
		try {
			// Finish building the draft before exposing any of it to the running app.
			var selection = BuildSelectionOptions();
			var profiles = SavedProfiles.Select(p => new NamedSelectionProfile { Name = p.Name, Options = CloneSelectionOptions(p.Options) }).ToList();
			var nfoOptions = BuildNfoOptions();
			string quarantine = QuarantineFolder.Trim();
			settings.AutomationSelection = selection;
			settings.SavedSelectionProfiles = profiles;
			settings.NfoFavourites = nfoOptions;
			settings.QuarantineFolder = quarantine;
			saveAttempted = true;
			await Task.Run(() => SettingsFile.SaveSettings());
			if (host != null) { reviewRefreshStarted = true; await host.InitialiseAutomationReviewAsync(); }
			Status = "Settings saved.";
			return true;
		}
		catch (Exception ex) {
			settings.AutomationSelection = previousSelection;
			settings.SavedSelectionProfiles = previousProfiles;
			settings.NfoFavourites = previousNfo;
			settings.QuarantineFolder = previousQuarantine;
			Status = $"Settings could not be saved: {ex.Message}. Previous settings remain active.";
			if (saveAttempted) {
				try { await Task.Run(() => SettingsFile.SaveSettings()); }
				catch (Exception rollbackError) { Status += $" The previous settings file could not be restored: {rollbackError.Message}"; }
			}
			if (reviewRefreshStarted && host != null) {
				try { await host.BuildAutomationCandidatesAsync(); }
				catch (Exception refreshError) { Status += $" Existing review protection could not be refreshed: {refreshError.Message}"; }
			}
			return false;
		}
		finally { if (host != null) host.IsBusy = false; IsWorking = false; }
	}

	internal static SelectionOptions CloneSelectionOptions(SelectionOptions options) => new() {
		AutoAfterScan = options.AutoAfterScan, HighlightedGroupsOnly = options.HighlightedGroupsOnly,
		Profile = options.Profile, Mode = options.Mode, DateSource = options.DateSource, OverlapPolicy = options.OverlapPolicy,
		FolderWeight = options.FolderWeight,
		FolderRules = (options.FolderRules ?? new()).Select(r => new FolderPriorityRule {
			Directory = r.Directory, Priority = r.Priority, Enabled = r.Enabled, Recursive = r.Recursive, ProtectAll = r.ProtectAll, Pattern = r.Pattern
		}).ToList(),
		Criteria = (options.Criteria ?? new()).Select(c => new SelectionCriterion { Name = c.Name, Weight = c.Weight, PreferLower = c.PreferLower }).ToList(),
		PreferredCodecs = new(options.PreferredCodecs ?? new()), PreferredAudioLanguages = new(options.PreferredAudioLanguages ?? new()),
		PreferredContainers = new(options.PreferredContainers ?? new())
	};
	internal static NfoOptions CloneNfoOptions(NfoOptions options) => new() {
		Enabled = options.Enabled, InheritSeason = options.InheritSeason, InheritSeries = options.InheritSeries,
		BooleanElements = new(options.BooleanElements ?? new()), TagValues = new(options.TagValues ?? new()), TagElements = new(options.TagElements ?? new())
	};
}
