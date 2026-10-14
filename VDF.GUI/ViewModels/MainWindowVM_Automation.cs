using System.Linq;
using System.Reactive;
using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core.Cleanup;
using VDF.Core.FFTools;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels;

// Immutable copy of the exact journal generation observed by a displayed row.
internal sealed record AutomationReviewBaseline(FileIdentity Identity, ReviewChoice Choice, bool Protected,
	bool Automatic, bool Keeper, string Profile, string Reason, DateTime UpdatedUtc) {
	internal static AutomationReviewBaseline? Of(ReviewRecord? record) => record is null ? null : new(
		record.Identity, record.Choice, record.ManuallyProtected, record.AutomaticallySelected,
		record.IsKeeper, record.Profile, record.Reason, record.UpdatedUtc);
	internal ReviewRecord ToRecord() => new() {
		Identity = Identity, Choice = Choice, ManuallyProtected = Protected, AutomaticallySelected = Automatic,
		IsKeeper = Keeper, Profile = Profile, Reason = Reason, UpdatedUtc = UpdatedUtc
	};
}

public partial class MainWindowVM {
	static readonly StringComparer AutomationPathComparer = PathComparer.ForCurrentPlatform;
	readonly SemaphoreSlim automationWorkGate = new(1, 1);
	readonly SemaphoreSlim automationReviewSaveGate = new(1, 1);
	readonly NfoFavouriteService automationNfo = new();
	readonly Dictionary<string, (long Length, DateTime Modified, DateTime Created, DateTime? Embedded)> embeddedDateCache = new(AutomationPathComparer);
	ReviewStore? automationReviewStore;
	string? automationReviewStorePath;
	CancellationTokenSource? reviewSaveDebounce;
	Task reviewSaveTask = Task.CompletedTask;
	int automaticReviewMutation;
	public string AutomationReviewFile => Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "ReviewedFiles.json");
	public string QuarantineHistoryFile => Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "QuarantineHistory.json");
	public string AutomationSelectionPlanFile => Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "SelectionPlan.json");
	public string AutomationStatus {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	} = string.Empty;
	public bool IsAutomationWorking {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	}
	public bool IsStartupLoading {
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	}

	/// <summary>Reserve startup before recovery yields, through database, results, and review loading.</summary>
	public async Task LoadStartupStateAsync() {
		if (IsStartupLoading) return;
		IsStartupLoading = true;
		IsBusy = true;
		IsBusyOverlayText = "Recovering saved cleanup state...";
		try {
			await RecoverCleanupStateAsync();
			await LoadDatabaseAsync();
			await RestoreBackupScanResultsAsync();
			await InitialiseAutomationReviewAsync();
		}
		finally {
			IsBusy = false;
			IsStartupLoading = false;
		}
		InitializeScheduledScanning();
	}

	async Task<ReviewStore> GetAutomationReviewStoreAsync() {
		await SettingsFile.ExportCleanupProtectionPolicyAsync();
		string path = AutomationReviewFile;
		if (automationReviewStore == null || !AutomationPathComparer.Equals(path, automationReviewStorePath)) {
			automationReviewStorePath = path;
			automationReviewStore = new ReviewStore(path);
		}
		var store = automationReviewStore;
		// Never repair a missing or corrupt current ledger from cached decisions
		// before a cleanup or automatic review save can use those decisions.
		await store.LoadForCleanupAsync();
		return store;
	}

	sealed record AutomationRowSnapshot(DuplicateItemVM Row, int Version, bool Restored, bool Invalid, FileIdentity? Identity, FileSnapshot? FileSnapshot);
	sealed record AutomationInspection(AutomationRowSnapshot Snapshot, bool Available, bool Stale, bool MetadataChanged, AutomationReviewBaseline? ObservedReview, ReviewRecord? Review, NfoResult Nfo, DateTime? EmbeddedDate);

	internal static bool AutomationScanMetadataMatches(VDF.Core.ViewModels.DuplicateItem item) {
		try {
			var file = new FileInfo(item.Path);
			return file.Exists && file.Length == item.SizeLong && item.DateModified != default &&
				file.LastWriteTimeUtc == SelectionPlanner.DateOf(new(item), SelectionDateSource.Modification) &&
				(item.IsImage || item.DateCreated == default || file.CreationTimeUtc == SelectionPlanner.DateOf(new(item), SelectionDateSource.Creation));
		}
		catch { return false; }
	}

	/// <summary>Loads validated review decisions and current NFO/folder protections. File and NFO work runs off the UI thread.</summary>
	public async Task<IReadOnlyDictionary<string, SelectionCandidate>> BuildAutomationCandidatesAsync() {
		var rows = Duplicates.Select(row => new AutomationRowSnapshot(row, row.AutomationReviewVersion,
			row.AutomationReviewRestored, row.AutomationSnapshotInvalid, row.AutomationIdentity, row.AutomationFileSnapshot)).ToList();
		var options = AutomationSettingsVM.CloneSelectionOptions(SettingsFile.Instance.AutomationSelection ?? new());
		var nfoOptions = AutomationSettingsVM.CloneNfoOptions(SettingsFile.Instance.NfoFavourites ?? new());
		var store = await GetAutomationReviewStoreAsync();
		// Observe one immutable journal generation before inspection awaits. A
		// debounced save may reload this store while filesystem work is pending.
		var observed = store.Records.Select(record => AutomationReviewBaseline.Of(record)!)
			.ToDictionary(record => record.Identity.Path, AutomationPathComparer);
		var savedPaths = observed.Keys.ToHashSet(AutomationPathComparer);
		var inspections = await Task.Run(() => {
			automationNfo.ClearCache();
			return rows.Select(snapshot => {
			string path = snapshot.Row.ItemInfo.Path;
			bool available = File.Exists(path);
			var currentSnapshot = available ? FileIdentity.CaptureSnapshot(path) : null;
			// A failed read (for example an exclusive lock) does not prove replacement.
			// Keep its review, but withhold selection/cleanup until validation succeeds.
			bool stale = snapshot.Invalid || (currentSnapshot != null &&
				(!AutomationScanMetadataMatches(snapshot.Row.ItemInfo) ||
				(snapshot.Identity != null && !currentSnapshot.Matches(snapshot.Identity)) ||
				(snapshot.FileSnapshot != null && currentSnapshot != snapshot.FileSnapshot)));
			bool changed = stale || (available && currentSnapshot == null);
			var baseline = observed.GetValueOrDefault(path);
			ReviewRecord? review = !snapshot.Restored && baseline?.Identity.MatchesCurrent() == true ? baseline.ToRecord() : null;
			var nfo = automationNfo.Inspect(path, nfoOptions);
			DateTime? embedded = available && options.DateSource == SelectionDateSource.Embedded ? ReadEmbeddedDate(path) : null;
			return new AutomationInspection(snapshot, available && !changed, stale, changed, baseline, review, nfo, embedded);
			}).ToList();
		});

		if (BeforeAutomationInspectionAppliedAsync is not null) await BeforeAutomationInspectionAppliedAsync();
		var candidates = new Dictionary<string, SelectionCandidate>(AutomationPathComparer);
		var liveRows = Duplicates.ToHashSet();
		automaticReviewMutation++;
		try {
			foreach (var inspection in inspections) {
				var row = inspection.Snapshot.Row;
				if (!liveRows.Contains(row)) continue;
				if (row.AutomationReviewVersion == inspection.Snapshot.Version) {
					if (inspection.Stale) {
						row.ManuallyProtected = false;
						row.ManualReviewChoice = ReviewChoice.None;
						row.AutomaticallySelected = false;
						row.IsAutomationKeeper = false;
						row.SelectionProfileApplied = string.Empty;
						row.AutomationSnapshotInvalid = true;
						row.AutomationIdentity = null;
						row.Checked = false;
						row.SelectionReason = "File identity changed; previous review discarded.";
					}
					if (!inspection.Stale && !row.AutomationReviewRestored && inspection.Available)
						row.ObservedReview = inspection.ObservedReview;
					if (!inspection.Stale && !row.AutomationReviewRestored && inspection.Review is { } review) {
						row.ObservedReview = AutomationReviewBaseline.Of(review);
						row.AutomationIdentity = review.Identity;
						row.ManuallyProtected = review.ManuallyProtected;
						row.ManualReviewChoice = review.Choice;
						row.AutomaticallySelected = review.AutomaticallySelected;
						row.SelectionProfileApplied = review.Profile;
						row.SelectionReason = review.Reason;
						row.IsAutomationKeeper = review.IsKeeper;
						row.Checked = review.Choice == ReviewChoice.Remove || (review.Choice == ReviewChoice.None && review.AutomaticallySelected);
					}
				}
				if (inspection.Available || inspection.Stale || !savedPaths.Contains(row.ItemInfo.Path)) row.AutomationReviewRestored = true;
				row.IsNfoFavourite = inspection.Nfo.IsFavourite;
				row.ProtectionUncertain = inspection.Nfo.IsUncertain || inspection.MetadataChanged;
				row.ProtectionReason = inspection.MetadataChanged ? "File cannot be validated against this scan. Retry when available or rescan before selection or cleanup." : inspection.Nfo.Reason;
				candidates[row.ItemInfo.Path] = new SelectionCandidate(row.ItemInfo) {
					IsAvailable = inspection.Available,
					ManuallyProtected = row.ManuallyProtected,
					ManualChoice = row.ManualReviewChoice,
					NfoProtected = inspection.Nfo.IsFavourite,
					ProtectionUncertain = row.ProtectionUncertain,
					ProtectionReason = row.ProtectionReason,
					EmbeddedDateUtc = inspection.EmbeddedDate
				};
			}
			var ignoredGroups = liveRows.Where(r => r.ManualReviewChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup)
				.GroupBy(r => r.ItemInfo.GroupId).ToDictionary(g => g.Key, g => g.Any(r => r.ManualReviewChoice == ReviewChoice.RejectGroup) ? ReviewChoice.RejectGroup : ReviewChoice.Ignore);
			foreach (var row in liveRows) {
				row.IsAutomationGroupIgnored = ignoredGroups.TryGetValue(row.ItemInfo.GroupId, out var groupChoice);
				if (!row.IsAutomationGroupIgnored) continue;
				if (row.ManualReviewChoice == ReviewChoice.None) {
					row.ManualReviewChoice = groupChoice;
					row.AutomaticallySelected = false;
					row.IsAutomationKeeper = false;
					row.SelectionReason = "Inherited the existing review decision for this group.";
				}
				if (candidates.TryGetValue(row.ItemInfo.Path, out var candidate)) candidate.ManualChoice = groupChoice;
				row.Checked = false;
				row.NotifyAutomationStateChanged();
			}
		}
		finally { automaticReviewMutation--; }
		var plan = await Task.Run(() => SelectionPlanner.BuildPlan(candidates.Values, options));
		liveRows = Duplicates.ToHashSet();
		automaticReviewMutation++;
		try {
			var decisions = plan.Decisions.ToDictionary(d => d.Path, AutomationPathComparer);
			foreach (var inspection in inspections) {
				var row = inspection.Snapshot.Row;
				if (!liveRows.Contains(row) || !decisions.TryGetValue(row.ItemInfo.Path, out var decision)) continue;
				candidates[row.ItemInfo.Path].ManualChoice = row.IsAutomationGroupIgnored ? ReviewChoice.Ignore : row.ManualReviewChoice;
				candidates[row.ItemInfo.Path].ManuallyProtected = row.ManuallyProtected;
				row.IsProtected = decision.IsProtected || row.ManuallyProtected || row.IsNfoFavourite || row.ProtectionUncertain;
				row.RequiresAutomationReview = decision.RequiresReview;
				if (row.IsProtected || row.IsAutomationGroupIgnored || row.ManualReviewChoice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup) row.Checked = false;
				if (decision.IsProtected) row.ProtectionReason = decision.Reason;
				if (row.ManualReviewChoice == ReviewChoice.Keep) row.IsAutomationKeeper = true;
				row.NotifyAutomationStateChanged();
			}
		}
		finally { automaticReviewMutation--; }
		return candidates;
	}

	DateTime? ReadEmbeddedDate(string path) {
		try {
			var info = new FileInfo(path);
			lock (embeddedDateCache) {
				if (embeddedDateCache.TryGetValue(path, out var cached) && cached.Length == info.Length &&
					cached.Modified == info.LastWriteTimeUtc && cached.Created == info.CreationTimeUtc) return cached.Embedded;
			}
			DateTime? date = FFProbeEngine.GetCreationTime(path);
			lock (embeddedDateCache) embeddedDateCache[path] = (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, date);
			return date;
		}
		catch { return null; }
	}

	public Task<FileIdentity?> CaptureAutomationIdentityAsync(string path) => FileIdentity.CaptureAsync(path);

	/// <summary>Called after a scan even when automatic selection is disabled, so protections and previous manual reviews remain effective.</summary>
	public async Task ApplyAutomaticSelectionAsync() {
		await RunAutomationSelectionAsync(SettingsFile.Instance.AutomationSelection?.AutoAfterScan == true, useHighlightedScope: false);
	}

	public async Task InitialiseAutomationReviewAsync() => await RunAutomationSelectionAsync(false);

	async Task RunAutomationSelectionAsync(bool applySelection, SelectionProfile? profile = null, SelectionDateSource? dateSource = null, bool useHighlightedScope = true) {
		await automationWorkGate.WaitAsync();
		IsAutomationWorking = true;
		try {
			SelectionPlan? planToSave = null;
			var candidates = await BuildAutomationCandidatesAsync();
			if (applySelection) {
				var options = AutomationSettingsVM.CloneSelectionOptions(SettingsFile.Instance.AutomationSelection ?? new());
				if (profile.HasValue) options.Profile = profile.Value;
				if (dateSource.HasValue) options.DateSource = dateSource.Value;
				HashSet<Guid>? selectedGroups = useHighlightedScope && options.HighlightedGroupsOnly
					? GetSelectedDuplicates().Select(d => d.ItemInfo.GroupId).ToHashSet() : null;
				var scoped = candidates.Values.Where(c => selectedGroups == null || selectedGroups.Contains(c.Item.GroupId)).ToList();
				var versions = Duplicates.ToDictionary(d => d.ItemInfo.Path, d => d.AutomationReviewVersion, AutomationPathComparer);
				var plan = await Task.Run(() => SelectionPlanner.BuildPlan(scoped, options));
				var changedGroups = Duplicates.Where(d => versions.TryGetValue(d.ItemInfo.Path, out int version) && version != d.AutomationReviewVersion)
					.Select(d => d.ItemInfo.GroupId).ToHashSet();
				if (changedGroups.Count > 0) plan.Decisions.RemoveAll(d => changedGroups.Contains(d.GroupId));
				plan.EstimatedSavings = plan.Decisions.Where(d => d.RemoveCandidate).Sum(d => Math.Max(0, candidates[d.Path].Item.SizeLong));
				ApplyAutomationPlan(plan);
				planToSave = plan;
				AutomationStatus = $"{plan.Decisions.Count(d => d.RemoveCandidate)} removal candidates; estimated savings {plan.EstimatedSavings.BytesToString()}. Review before cleanup.";
			}
			await SaveAutomationReviewRecordsAsync();
			if (planToSave != null) await SelectionPlanStore.SaveAsync(AutomationSelectionPlanFile, planToSave);
		}
		catch (Exception ex) {
			AutomationStatus = $"Selection/review could not be completed: {ex.Message}";
			Logger.Instance.Warn(AutomationStatus);
			throw;
		}
		finally { IsAutomationWorking = false; automationWorkGate.Release(); }
	}

	internal void ApplyAutomationPlan(SelectionPlan plan) {
		var rowsByPath = Duplicates.ToDictionary(d => d.ItemInfo.Path, AutomationPathComparer);
		automaticReviewMutation++;
		try {
			using var undo = BeginSelectionUndoBatch();
			foreach (var decision in plan.Decisions) {
				if (!rowsByPath.TryGetValue(decision.Path, out var row)) continue;
				row.IsProtected = decision.IsProtected;
				row.IsAutomationKeeper = decision.IsKeeper;
				row.RequiresAutomationReview = decision.RequiresReview;
				row.SelectionReason = decision.Reason;
				row.SelectionProfileApplied = plan.Profile.ToString();
				row.AutomaticallySelected = decision.RemoveCandidate && row.ManualReviewChoice == ReviewChoice.None;
				row.Checked = decision.RemoveCandidate;
				row.NotifyAutomationStateChanged();
			}
		}
		finally { automaticReviewMutation--; }
		RefreshResultsView();
	}

	/// <summary>Called by the existing Checked-property handler. Automatic/restoration mutations are suppressed.</summary>
	internal void TrackReviewChange(DuplicateItemVM item) {
		if (automaticReviewMutation > 0) return;
		item.AutomationReviewVersion++;
		item.AutomationReviewRestored = true;
		item.ManualReviewChoice = item.Checked ? ReviewChoice.Remove : ReviewChoice.Keep;
		item.AutomaticallySelected = false;
		item.IsAutomationKeeper = !item.Checked;
		item.SelectionReason = item.Checked ? "Selected for removal by you." : "Kept by you.";
		item.NotifyAutomationStateChanged();
		ScheduleAutomationReviewSave();
	}

	void ScheduleAutomationReviewSave() {
		reviewSaveDebounce?.Cancel();
		reviewSaveDebounce?.Dispose();
		reviewSaveDebounce = new CancellationTokenSource();
		reviewSaveTask = SaveAutomationReviewAfterDelayAsync(reviewSaveDebounce.Token);
	}

	async Task SaveAutomationReviewAfterDelayAsync(CancellationToken cancellationToken) {
		try {
			await Task.Delay(400, cancellationToken);
			await SaveAutomationReviewRecordsAsync();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { AutomationStatus = $"Review decisions could not be saved: {ex.Message}"; Logger.Instance.Warn(AutomationStatus); }
	}

	/// <summary>Flush before results are cleared or the application exits. Errors propagate so the caller can preserve the current results.</summary>
	public async Task PrepareAutomationRescanAsync() {
		reviewSaveDebounce?.Cancel();
		await reviewSaveTask;
		await automationWorkGate.WaitAsync();
		try {
			// No decisions can be lost from an empty/unreviewed result set. In
			// particular, unreadable startup state must not prevent closing to recover.
			if (Duplicates.Any(d => d.ManuallyProtected || d.ManualReviewChoice != ReviewChoice.None ||
				d.AutomaticallySelected || d.IsAutomationKeeper || d.AutomationIdentity != null))
				await SaveAutomationReviewRecordsAsync();
		}
		finally { automationWorkGate.Release(); }
	}

	// Apply a foreign decision without turning checkbox notifications into local edits.
	void ReconcileAutomationReview(DuplicateItemVM row, ReviewRecord? record, FileIdentity identity) {
		row.AutomationReviewVersion++;
		row.ObservedReview = AutomationReviewBaseline.Of(record);
		if (record is not null && !identity.Matches(record.Identity)) {
			row.AutomationSnapshotInvalid = true;
			row.ProtectionUncertain = true;
			row.IsProtected = true;
			row.SelectionReason = "The current review belongs to a different file snapshot. Rescan before changing it.";
			row.Checked = false;
			row.NotifyAutomationStateChanged();
			return;
		}
		row.ManuallyProtected = record?.ManuallyProtected ?? false;
		row.ManualReviewChoice = record?.Choice ?? ReviewChoice.None;
		row.AutomaticallySelected = record?.AutomaticallySelected ?? false;
		row.IsAutomationKeeper = record?.IsKeeper ?? false;
		row.SelectionProfileApplied = record?.Profile ?? string.Empty;
		row.SelectionReason = record?.Reason ?? string.Empty;
		row.IsAutomationGroupIgnored = row.ManualReviewChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup;
		var options = SettingsFile.Instance.AutomationSelection;
		var folder = new FolderRuleMatcher(options.FolderRules, options.OverlapPolicy).Match(row.ItemInfo.Path);
		row.IsProtected = row.ManuallyProtected || row.IsNfoFavourite || row.ProtectionUncertain || folder.Protected || folder.Uncertain;
		row.Checked = row.ManualReviewChoice == ReviewChoice.Remove ||
			(row.ManualReviewChoice == ReviewChoice.None && row.AutomaticallySelected);
		row.NotifyAutomationStateChanged();
	}

	void ObserveAutomationReviewWithPendingChoice(DuplicateItemVM row, ReviewRecord? saved, int protectionVersion, FileIdentity identity) {
		row.AutomationReviewVersion++;
		row.ObservedReview = AutomationReviewBaseline.Of(saved);
		if (saved is not null && !identity.Matches(saved.Identity)) {
			row.AutomationSnapshotInvalid = true;
			row.ProtectionUncertain = true;
			row.IsProtected = true;
			row.Checked = false;
			row.NotifyAutomationStateChanged();
			return;
		}
		// A checkbox choice does not express an intent to change protection.
		// Rebase that untouched field onto the current journal generation.
		if (row.AutomationProtectionVersion == protectionVersion)
			row.ManuallyProtected = saved?.ManuallyProtected ?? false;
		if (row.ManuallyProtected) {
			row.IsProtected = true;
			row.Checked = false;
		}
		row.NotifyAutomationStateChanged();
	}

	internal Func<Task>? BeforeAutomationReviewCommitAsync { get; set; }
	internal Func<Task>? BeforeAutomationInspectionAppliedAsync { get; set; }
	internal Func<Task>? BeforeAutomationReviewReconciliationAsync { get; set; }

	public async Task SaveAutomationReviewRecordsAsync() {
		await automationReviewSaveGate.WaitAsync();
		try {
			var store = await GetAutomationReviewStoreAsync();
			var snapshot = Duplicates.Where(d => d.ManuallyProtected || d.ManualReviewChoice != ReviewChoice.None ||
				d.AutomaticallySelected || d.IsAutomationKeeper || d.AutomationIdentity != null)
				.Select(d => (Row: d, Path: d.ItemInfo.Path, Baseline: d.ObservedReview,
					Identity: d.AutomationIdentity, Snapshot: d.AutomationFileSnapshot, Invalid: d.AutomationSnapshotInvalid)).ToList();
			var captured = await Task.Run(() => snapshot.Select(state => {
				var identity = state.Invalid || !AutomationScanMetadataMatches(state.Row.ItemInfo) ? null :
					state.Identity != null && state.Identity.MatchesCurrent() ? state.Identity : FileIdentity.Capture(state.Path);
				if (identity != null && state.Snapshot != null && !state.Snapshot.Matches(identity)) identity = null;
				return (State: state, Identity: identity);
			}).ToList());
			var updates = captured.Where(entry => entry.Identity != null &&
				(entry.State.Identity == null || entry.State.Identity == entry.Identity) &&
				Duplicates.Contains(entry.State.Row) && AutomationPathComparer.Equals(entry.State.Row.ItemInfo.Path, entry.State.Path))
				.Select(entry => (Row: entry.State.Row, Baseline: entry.State.Baseline,
					Version: entry.State.Row.AutomationReviewVersion, ProtectionVersion: entry.State.Row.AutomationProtectionVersion, Record: new ReviewRecord {
						Identity = entry.Identity!, Choice = entry.State.Row.ManualReviewChoice,
						ManuallyProtected = entry.State.Row.ManuallyProtected, AutomaticallySelected = entry.State.Row.AutomaticallySelected,
						IsKeeper = entry.State.Row.IsAutomationKeeper, Profile = entry.State.Row.SelectionProfileApplied,
						Reason = entry.State.Row.SelectionReason, UpdatedUtc = DateTime.UtcNow
					})).ToList();
			if (BeforeAutomationReviewCommitAsync is not null) await BeforeAutomationReviewCommitAsync();
			var conflicts = new HashSet<DuplicateItemVM>();
			var committed = await store.UpdateWithSnapshotAsync(current => {
				var merged = current.ToDictionary(record => record.Identity.Path, AutomationPathComparer);
				foreach (var update in updates) {
					merged.TryGetValue(update.Record.Identity.Path, out var existing);
					if (AutomationReviewBaseline.Of(existing) != update.Baseline) {
						conflicts.Add(update.Row);
						continue;
					}
					merged[update.Record.Identity.Path] = update.Record;
				}
				return merged.Values;
			});
			if (BeforeAutomationReviewReconciliationAsync is not null) await BeforeAutomationReviewReconciliationAsync();
			var committedByPath = committed.ToDictionary(record => record.Identity.Path, AutomationPathComparer);
			automaticReviewMutation++;
			try {
				foreach (var update in updates) {
					var row = update.Row;
					if (!Duplicates.Contains(row) || !AutomationPathComparer.Equals(row.ItemInfo.Path, update.Record.Identity.Path)) continue;
					var saved = committedByPath.GetValueOrDefault(row.ItemInfo.Path);
					row.ObservedReview = AutomationReviewBaseline.Of(saved);
					row.AutomationIdentity = update.Record.Identity;
					// New local choices made during the transaction remain pending for
					// the next save against this newly observed generation.
					if (conflicts.Contains(row) && row.AutomationReviewVersion == update.Version)
						ReconcileAutomationReview(row, saved, update.Record.Identity);
					else if (conflicts.Contains(row))
						ObserveAutomationReviewWithPendingChoice(row, saved, update.ProtectionVersion, update.Record.Identity);
				}
			}
			finally { automaticReviewMutation--; }
			if (conflicts.Count > 0) AutomationStatus = "Newer saved review decisions were loaded. Reapply a conflicting choice if still intended.";
		}
		finally { automationReviewSaveGate.Release(); }
	}

	async Task AutomationUiCommandAsync(Func<Task> work) {
		if (IsBusy || IsScanning || IsAutomationWorking) { AutomationStatus = "Wait for the current scan or operation to finish."; return; }
		try { await work(); }
		catch (Exception ex) { await MessageBoxService.Show($"Selection or review failed: {ex.Message}"); }
	}
	public ReactiveCommand<Unit, Unit> ApplyAutomationSelectionCommand => ReactiveCommand.CreateFromTask(() => AutomationUiCommandAsync(() => RunAutomationSelectionAsync(true)));
	public ReactiveCommand<Unit, Unit> KeepNewestAutomationCommand => ReactiveCommand.CreateFromTask(() => AutomationUiCommandAsync(() => RunAutomationSelectionAsync(true, SelectionProfile.NewestFirst)));
	public ReactiveCommand<Unit, Unit> KeepOldestAutomationCommand => ReactiveCommand.CreateFromTask(() => AutomationUiCommandAsync(() => RunAutomationSelectionAsync(true, SelectionProfile.OldestFirst)));
	internal Task ApplyLegacyDateSelectionAsync(bool keepNewest) => AutomationUiCommandAsync(() => RunAutomationSelectionAsync(true,
		keepNewest ? SelectionProfile.NewestFirst : SelectionProfile.OldestFirst,
		SettingsFile.Instance.ResultsShowDateModified ? SelectionDateSource.Modification : SelectionDateSource.Creation));

	internal IDisposable BeginAutomaticReviewMutation() {
		var previousChecks = Duplicates.ToDictionary(d => d, d => d.Checked);
		automaticReviewMutation++;
		return System.Reactive.Disposables.Disposable.Create(() => {
			foreach (var row in Duplicates) {
				if (!previousChecks.TryGetValue(row, out bool oldChecked)) continue;
				if (row.ManualReviewChoice == ReviewChoice.Keep) row.Checked = false;
				else if (row.ManualReviewChoice == ReviewChoice.Remove) row.Checked = oldChecked;
			}
			automaticReviewMutation--;
			bool changed = false;
			foreach (var row in Duplicates) {
				if (!previousChecks.TryGetValue(row, out bool oldChecked) || oldChecked == row.Checked || row.ManualReviewChoice != ReviewChoice.None) continue;
				row.AutomaticallySelected = row.Checked;
				row.IsAutomationKeeper = !row.Checked;
				row.SelectionProfileApplied = "Legacy selection";
				row.SelectionReason = "Selected by the existing automatic selection settings.";
				row.NotifyAutomationStateChanged();
				changed = true;
			}
			if (changed) ScheduleAutomationReviewSave();
		});
	}
	public ReactiveCommand<Unit, Unit> OpenAutomationSettingsCommand => ReactiveCommand.Create(() => new AutomationSettingsView(this).Show(ApplicationHelpers.MainWindow));
	public ReactiveCommand<DuplicateItemVM, Unit> KeepAutomationItemCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(async () => {
		if (item == null) return;
		item.AutomationReviewVersion++;
		item.AutomationReviewRestored = true;
		item.ManualReviewChoice = ReviewChoice.Keep;
		item.AutomaticallySelected = false;
		item.IsAutomationKeeper = true;
		item.SelectionReason = "Kept by you.";
		using (BeginAutomaticReviewMutation()) item.Checked = false;
		item.NotifyAutomationStateChanged();
		await SaveAutomationReviewRecordsAsync();
	}));
	public ReactiveCommand<DuplicateItemVM, Unit> ProtectAutomationItemCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(async () => {
		if (item == null) return;
		item.AutomationReviewVersion++;
		item.AutomationProtectionVersion++;
		item.ManuallyProtected = true;
		item.IsProtected = true;
		item.AutomationReviewRestored = true;
		automaticReviewMutation++;
		try { item.Checked = false; }
		finally { automaticReviewMutation--; }
		item.SelectionReason = "Protected by you.";
		item.NotifyAutomationStateChanged();
		await SaveAutomationReviewRecordsAsync();
	}));
	public ReactiveCommand<DuplicateItemVM, Unit> UnprotectAutomationItemCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(async () => {
		if (item == null) return;
		item.AutomationReviewVersion++;
		item.AutomationProtectionVersion++;
		item.ManuallyProtected = false;
		item.NotifyAutomationStateChanged();
		await RunAutomationSelectionAsync(false);
	}));
	public ReactiveCommand<DuplicateItemVM, Unit> IgnoreAutomationGroupCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(() => SetAutomationGroupReviewAsync(item, ReviewChoice.Ignore)));
	public ReactiveCommand<DuplicateItemVM, Unit> RejectAutomationGroupCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(() => SetAutomationGroupReviewAsync(item, ReviewChoice.RejectGroup)));
	public ReactiveCommand<DuplicateItemVM, Unit> ResetAutomationGroupReviewCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(item => AutomationUiCommandAsync(() => SetAutomationGroupReviewAsync(item, ReviewChoice.None)));

	async Task SetAutomationGroupReviewAsync(DuplicateItemVM item, ReviewChoice choice) {
		if (item == null) return;
		automaticReviewMutation++;
		try {
			foreach (var row in Duplicates.Where(d => d.ItemInfo.GroupId == item.ItemInfo.GroupId)) {
				row.AutomationReviewVersion++;
				row.AutomationReviewRestored = true;
				row.ManualReviewChoice = choice;
				row.IsAutomationGroupIgnored = choice is ReviewChoice.Ignore or ReviewChoice.RejectGroup;
				row.AutomaticallySelected = false;
				row.IsAutomationKeeper = false;
				row.Checked = false;
				row.SelectionReason = choice == ReviewChoice.None ? "Review choice reset by you." : "Group " + (choice == ReviewChoice.Ignore ? "ignored" : "rejected") + " by you.";
				row.NotifyAutomationStateChanged();
			}
		}
		finally { automaticReviewMutation--; }
		await SaveAutomationReviewRecordsAsync();
	}
}
