using System.Linq;
using System.Reactive;
using ReactiveUI;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels;

public partial class MainWindowVM {
	public ReactiveCommand<Unit, Unit> PreviewQuarantineCommand => ReactiveCommand.CreateFromTask(async () => {
		if (IsScanning || IsBusy || IsAutomationWorking) return;
		try {
			var preview = await CreateQuarantinePreviewAsync();
			new QuarantinePreviewView(preview).Show(ApplicationHelpers.MainWindow);
		}
		catch (Exception ex) { await MessageBoxService.Show($"Quarantine preview failed: {ex.Message}"); }
	});

	internal async Task<QuarantinePreviewVM> CreateQuarantinePreviewAsync() {
		await automationWorkGate.WaitAsync();
		IsAutomationWorking = true;
		try {
			var candidates = await BuildAutomationCandidatesAsync();
			var checkedRows = CheckedItemsToDelete;
			var groups = Duplicates.GroupBy(d => d.ItemInfo.GroupId).ToDictionary(g => g.Key, g => g.ToList());
			var draft = checkedRows.Select(row => {
				var group = groups[row.ItemInfo.GroupId];
				var keeper = group.Where(d => !d.Checked && candidates.TryGetValue(d.ItemInfo.Path, out var c) && c.IsAvailable)
					.OrderByDescending(d => d.IsAutomationKeeper).ThenBy(d => d.ItemInfo.Path, AutomationPathComparer).FirstOrDefault();
				string reason = group.Any(d => d.ManualReviewChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup) ? "This group is ignored or rejected." :
					!row.CanMarkForRemoval ? row.AutomationTooltip : keeper == null ? "No available unchecked keeper remains in this group." : row.SelectionReason;
				return (Row: row, Keeper: keeper, Reason: reason, CanMove: row.CanMarkForRemoval && keeper != null &&
					!group.Any(d => d.ManualReviewChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup));
			}).ToList();
			var items = await Task.Run(() => draft.Select(entry => {
				FileIdentity? target = entry.CanMove && !entry.Row.AutomationSnapshotInvalid && AutomationScanMetadataMatches(entry.Row.ItemInfo)
					? entry.Row.AutomationIdentity ?? FileIdentity.Capture(entry.Row.ItemInfo.Path) : null;
				if (target != null && !target.MatchesCurrent()) target = null;
				FileIdentity? keeper = target != null && entry.Keeper != null && !entry.Keeper.AutomationSnapshotInvalid && AutomationScanMetadataMatches(entry.Keeper.ItemInfo)
					? entry.Keeper.AutomationIdentity ?? FileIdentity.Capture(entry.Keeper.ItemInfo.Path) : null;
				if (keeper != null && !keeper.MatchesCurrent()) keeper = null;
				return new QuarantinePreviewItem {
					Path = entry.Row.ItemInfo.Path, KeeperPath = entry.Keeper?.ItemInfo.Path ?? string.Empty,
					Reason = entry.CanMove && (target == null || keeper == null) ? "The target or keeper changed or is unavailable. Rescan." : entry.Reason,
					SizeBytes = Math.Max(0, entry.Row.ItemInfo.SizeLong), CanQuarantine = target != null && keeper != null,
					ByteIdentical = target != null && keeper != null && target.Sha256 == keeper.Sha256,
					TargetIdentity = target, KeeperIdentity = keeper
				};
			}).ToList());
			string destination = string.IsNullOrWhiteSpace(SettingsFile.Instance.QuarantineFolder)
				? Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "quarantine") : SettingsFile.Instance.QuarantineFolder;
			var service = new CleanupProtectionGuard(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder)).CreateQuarantineService();
			var validatedItems = new List<QuarantinePreviewItem>(items.Count);
			foreach (var item in items) {
				if (!item.CanQuarantine || item.TargetIdentity == null || item.KeeperIdentity == null) { validatedItems.Add(item); continue; }
				try {
					var validation = await service.PreviewAsync(item.TargetIdentity, item.KeeperIdentity, destination);
					validatedItems.Add(new QuarantinePreviewItem {
						Path = item.Path, KeeperPath = item.KeeperPath, SizeBytes = item.SizeBytes, CanQuarantine = true,
						ByteIdentical = validation.ByteIdentical, TargetIdentity = item.TargetIdentity, KeeperIdentity = item.KeeperIdentity,
						Reason = validation.Reason + (validation.DestinationWillBeCreated ? " The quarantine folder will be created." : string.Empty)
					});
				}
				catch (Exception ex) { validatedItems.Add(new QuarantinePreviewItem { Path = item.Path, KeeperPath = item.KeeperPath, SizeBytes = item.SizeBytes, Reason = ex.Message }); }
			}
			return new QuarantinePreviewVM(this, validatedItems, destination);
		}
		finally { IsAutomationWorking = false; automationWorkGate.Release(); }
	}

	internal async Task<string> ExecuteQuarantinePreviewAsync(IReadOnlyList<QuarantinePreviewItem> preview, string destination) {
		if (IsScanning || IsBusy || IsAutomationWorking) throw new InvalidOperationException("Wait for the current scan or operation to finish.");
		await automationWorkGate.WaitAsync();
		IsAutomationWorking = true;
		IsBusy = true;
		IsBusyOverlayText = "Quarantining reviewed files…";
		var cancellationToken = BeginCancelableBusyOperation();
		int moved = 0, skipped = 0;
		string warning = string.Empty;
		try {
			var candidates = await BuildAutomationCandidatesAsync();
			var rows = Duplicates.ToDictionary(d => d.ItemInfo.Path, AutomationPathComparer);
			var rejectedGroups = Duplicates.Where(d => d.ManualReviewChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup).Select(d => d.ItemInfo.GroupId).ToHashSet();
			var service = new CleanupProtectionGuard(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder));
			foreach (var item in preview.Where(i => i.CanQuarantine)) {
				if (cancellationToken.IsCancellationRequested) { warning = "Cancelled; completed moves are retained."; break; }
				if (item.TargetIdentity == null || item.KeeperIdentity == null || !rows.TryGetValue(item.Path, out var row) || !row.Checked ||
					!row.CanMarkForRemoval || !rows.TryGetValue(item.KeeperPath, out var keeper) || keeper.Checked ||
					rejectedGroups.Contains(row.ItemInfo.GroupId) || row.ItemInfo.GroupId != keeper.ItemInfo.GroupId ||
					!candidates.TryGetValue(item.Path, out var candidate) || !candidate.IsAvailable) { skipped++; continue; }
				try {
					IsBusyOverlayText = $"Quarantining {Path.GetFileName(item.Path)}…";
					bool liveProtected = await Task.Run(() => { automationNfo.ClearCache(); return IsLiveCleanupProtected(row); }, cancellationToken);
					if (liveProtected || !row.Checked || keeper.Checked) { skipped++; continue; }
					if (row.IsProtected || candidate.ManuallyProtected || candidate.NfoProtected || candidate.ProtectionUncertain) { skipped++; continue; }
					var group = CaptureCompleteCleanupGroup(row.ItemInfo.GroupId);
					var result = await service.QuarantineAsync(item.TargetIdentity, item.KeeperIdentity, destination,
						groupMembers: group, ct: cancellationToken);
					if (result.State == QuarantineState.Quarantined) { Duplicates.Remove(row); moved++; }
					else { skipped++; Logger.Instance.Warn($"Quarantine {item.Path}: {result.State} {result.Error}"); }
				}
				catch (OperationCanceledException) { warning = "Cancelled; completed moves are retained. Check history for any interrupted operation."; break; }
				catch (Exception ex) { skipped++; warning = "Some operations failed; check quarantine history. " + ex.Message; Logger.Instance.Warn($"Quarantine {item.Path}: {ex.Message}"); }
			}
			RefreshResultsView();
			RefreshGroupStats();
			try { await SaveAutomationReviewRecordsAsync(); await ExportScanResults(BackupScanResultsFile); }
			catch (Exception ex) { warning += " Current results could not be saved: " + ex.Message; }
			return $"Quarantined {moved} files; skipped {skipped}. Restore is available in quarantine history." +
				(warning.Length == 0 ? string.Empty : Environment.NewLine + warning);
		}
		finally { EndCancelableBusyOperation(); IsBusy = false; IsAutomationWorking = false; automationWorkGate.Release(); }
	}
}
