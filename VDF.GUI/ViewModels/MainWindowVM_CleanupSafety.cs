using System.Linq;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core.Cleanup;

namespace VDF.GUI.ViewModels;

public partial class MainWindowVM {
    internal sealed record ApprovedRemoval(DuplicateItemVM Item, FileIdentity Target, FileIdentity Keeper);
    readonly SemaphoreSlim cleanupOperationGate = new(1, 1);
    string LegacyCleanupJournal => Path.Combine(CoreUtils.ResolveDatabaseFolder(Data.SettingsFile.Instance.CustomDatabaseFolder), "LegacyCleanupHistory.json");

    public async Task RecoverCleanupStateAsync() {
        await new VDF.Core.Cleanup.LegacyRemovalService(LegacyCleanupJournal).RecoverAsync();
        await new VDF.Core.Cleanup.QuarantineService(QuarantineHistoryFile).HistoryAsync();
    }

    internal async Task<Dictionary<DuplicateItemVM, ApprovedRemoval>> PrepareCleanupGuardAsync(IReadOnlyList<DuplicateItemVM> requested) {
        var candidates = await BuildAutomationCandidatesAsync();
        var matcher = new FolderRuleMatcher(Data.SettingsFile.Instance.AutomationSelection.FolderRules,
            Data.SettingsFile.Instance.AutomationSelection.OverlapPolicy);
        var requestedPaths = requested.Select(r => r.ItemInfo.Path).ToHashSet(AutomationPathComparer);
        var keepers = Duplicates.Where(d => !requestedPaths.Contains(d.ItemInfo.Path) && !d.Checked &&
            candidates.TryGetValue(d.ItemInfo.Path, out var c) && c.IsAvailable)
            .GroupBy(d => d.ItemInfo.GroupId).ToDictionary(g => g.Key, g => g.ToList());
        var input = requested.Select(row => (Row: row, Candidate: candidates.GetValueOrDefault(row.ItemInfo.Path),
            Folder: matcher.Match(row.ItemInfo.Path), Identity: row.AutomationIdentity)).ToList();
        return await Task.Run(() => {
            var approved = new Dictionary<DuplicateItemVM, ApprovedRemoval>();
            var cachedKeepers = new Dictionary<Guid, FileIdentity>();
            foreach (var entry in input) {
                var c = entry.Candidate;
                if (c is null || !c.IsAvailable || c.ManuallyProtected || c.NfoProtected || c.ProtectionUncertain ||
                    c.ManualChoice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup || entry.Folder.Protected || entry.Folder.Uncertain) continue;
                var target = entry.Identity ?? FileIdentity.Capture(entry.Row.ItemInfo.Path);
                if (!MatchesDisplayedScan(entry.Row, target) || !target!.MatchesCurrent(forceHash: true)) continue;
                if (!cachedKeepers.TryGetValue(entry.Row.ItemInfo.GroupId, out var keeper)) {
                    if (!keepers.TryGetValue(entry.Row.ItemInfo.GroupId, out var groupKeepers)) continue;
                    foreach (var row in groupKeepers) {
                        var identity = row.AutomationIdentity ?? FileIdentity.Capture(row.ItemInfo.Path);
                        if (MatchesDisplayedScan(row, identity) && identity!.MatchesCurrent(forceHash: true) && identity.StableFileId != target.StableFileId) {
                            cachedKeepers[entry.Row.ItemInfo.GroupId] = keeper = identity;
                            break;
                        }
                    }
                }
                if (keeper is null || keeper.StableFileId == target.StableFileId) continue;
                approved.Add(entry.Row, new(entry.Row, target, keeper));
            }
            return approved;
        });
    }

    internal async Task ReplaceApprovedWithLinkAsync(CleanupProtectionGuard removal, ApprovedRemoval action,
        Dictionary<DuplicateItemVM, ApprovedRemoval> approved, IReadOnlyList<FileIdentity> group, bool hardLink) {
        if (hardLink) await automationReviewSaveGate.WaitAsync();
        bool hardLinkAttempted = false;
        try {
			if (hardLink) await removal.ValidateHardLinkKeeperAsync(action.Keeper);
            if (hardLink) await Dispatcher.UIThread.InvokeAsync(async () => {
                var store = await GetAutomationReviewStoreAsync();
                static bool DeliberateProtection(bool protect, ReviewChoice choice) => protect ||
                    choice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup;
                bool persisted = store.Records.Any(record => action.Keeper.SameFileSnapshot(record.Identity) &&
                    DeliberateProtection(record.ManuallyProtected, record.Choice));
                bool pending = Duplicates.Any(row =>
                    (row.AutomationIdentity is not null ? action.Keeper.SameFileSnapshot(row.AutomationIdentity) :
                        row.AutomationFileSnapshot is { } snapshot && snapshot.StableFileId == action.Keeper.StableFileId &&
                        snapshot.Length == action.Keeper.Length && snapshot.LastWriteUtc == action.Keeper.LastWriteUtc &&
                        snapshot.CreationUtc == action.Keeper.CreationUtc && snapshot.ChangeStamp == action.Keeper.ChangeStamp) &&
                    DeliberateProtection(row.ManuallyProtected, row.ManualReviewChoice));
                if (persisted || pending)
                    throw new IOException("Hardlink replacement would change a deliberately protected keeper's identity. Use quarantine or choose a keeper without a deliberate protection decision.");
            });
            await removal.RemoveAsync(action.Target, action.Keeper, claim => {
                if (hardLink) removal.ValidateHardLinkKeeperAsync(action.Keeper).GetAwaiter().GetResult();
                hardLinkAttempted = hardLink;
                CreateVerifiedReplacementLink(claim, action.Target.Path, action.Keeper.Path, hardLink);
            }, groupMembers: group);
        }
        finally {
            try {
                // Linking can change the keeper's stamp even if a subsequent
                // removal/journal step fails. Serialize the mutation and refresh
                // with autosaves so none can discard its prior manual protection.
                if (hardLinkAttempted) {
                    try {
                        if (!await Dispatcher.UIThread.InvokeAsync(() => RefreshCleanupKeeperAfterHardLinkCoreAsync(action.Keeper, approved)))
                            Logger.Instance.Warn("The hardlink keeper changed during refresh; remaining cleanup requires a rescan.");
                    }
                    catch (Exception ex) {
                        Logger.Instance.Warn($"The hardlink keeper review could not be refreshed; remaining cleanup requires a rescan: {ex.Message}");
                    }
                }
            }
            finally { if (hardLink) automationReviewSaveGate.Release(); }
        }
    }

    internal Task<bool> RefreshCleanupKeeperAfterHardLinkAsync(FileIdentity previous,
        Dictionary<DuplicateItemVM, ApprovedRemoval> approved) => Dispatcher.UIThread.InvokeAsync(async () => {
        await automationReviewSaveGate.WaitAsync();
        try { return await RefreshCleanupKeeperAfterHardLinkCoreAsync(previous, approved); }
        finally { automationReviewSaveGate.Release(); }
    });

    async Task<bool> RefreshCleanupKeeperAfterHardLinkCoreAsync(FileIdentity previous,
        Dictionary<DuplicateItemVM, ApprovedRemoval> approved) {
            var verified = await Task.Run<(FileIdentity Identity, FileSnapshot Snapshot, DateTime Created)?>(() => {
                var current = IdentityReader.Capture(previous.Path, true, CancellationToken.None);
                var snapshot = FileIdentity.CaptureSnapshot(previous.Path);
                if (current is null || snapshot is null || !snapshot.Matches(current) ||
                    current.StableFileId != previous.StableFileId || current.Length != previous.Length ||
                    current.LastWriteUtc != previous.LastWriteUtc || current.Sha256 != previous.Sha256) return null;
                return (current, snapshot, new FileInfo(previous.Path).CreationTimeUtc);
            });
            if (verified is not { } refreshed) return false;
            var store = await GetAutomationReviewStoreAsync();
            var records = store.Records.Select(record => previous.Matches(record.Identity) ? new ReviewRecord {
                Identity = refreshed.Identity, Choice = record.Choice, ManuallyProtected = record.ManuallyProtected,
                AutomaticallySelected = record.AutomaticallySelected, IsKeeper = record.IsKeeper,
                Profile = record.Profile, Reason = record.Reason, UpdatedUtc = DateTime.UtcNow
            } : record).ToList();
            // Carry a matching manual review across this verified intentional
            // link-count change before allowing another action to use the keeper.
            await store.SaveAsync(records);
            if (!refreshed.Snapshot.MatchesCurrent()) return false;
            foreach (var row in Duplicates.Where(row => AutomationPathComparer.Equals(row.ItemInfo.Path, previous.Path))) {
                if (row.AutomationIdentity is not null && !previous.Matches(row.AutomationIdentity)) continue;
                row.AutomationIdentity = refreshed.Identity;
                row.AutomationFileSnapshot = refreshed.Snapshot;
                row.ItemInfo.DateCreated = refreshed.Created;
                row.RaisePropertyChanged(nameof(DuplicateItemVM.ShownDate));
            }
            foreach (var entry in approved.ToList())
                if (previous.Matches(entry.Value.Keeper)) approved[entry.Key] = entry.Value with { Keeper = refreshed.Identity };
            return true;
    }

    static bool MatchesDisplayedScan(DuplicateItemVM row, FileIdentity? identity) => identity is not null &&
        (row.AutomationFileSnapshot is null || row.AutomationFileSnapshot.Matches(identity)) &&
        row.ItemInfo.SizeLong == identity.Length && row.ItemInfo.DateModified != default &&
        SelectionPlanner.DateOf(new(row.ItemInfo), SelectionDateSource.Modification) == identity.LastWriteUtc;

    bool IsLiveCleanupProtected(DuplicateItemVM row) {
        var nfo = automationNfo.Inspect(row.ItemInfo.Path, Data.SettingsFile.Instance.NfoFavourites);
        var options = Data.SettingsFile.Instance.AutomationSelection;
        var folder = new FolderRuleMatcher(options.FolderRules, options.OverlapPolicy).Match(row.ItemInfo.Path);
        return row.ManuallyProtected || row.ManualReviewChoice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup ||
            nfo.IsFavourite || nfo.IsUncertain || folder.Protected || folder.Uncertain;
    }

    static bool MoveCleanupClaimToTrash(string path, string originalPath) {
        if (!OperatingSystem.IsWindows()) return FileUtils.MoveToTrash(path, originalPath);
        var operation = new FileUtils.SHFILEOPSTRUCT {
            wFunc = FileUtils.FileOperationType.FO_DELETE, pFrom = path + "\0\0",
            fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO | FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
                FileUtils.FileOperationFlags.FOF_NOERRORUI | FileUtils.FileOperationFlags.FOF_SILENT
        };
        return FileUtils.SHFileOperation(ref operation) == 0 && !File.Exists(path);
    }

    static void CreateVerifiedReplacementLink(string claim, string original, string keeper, bool hardLink) {
        bool created = false;
        try {
            if (hardLink) HardLinkUtils.CreateHardLink(original, keeper);
            else File.CreateSymbolicLink(original, keeper);
            created = true;
            File.Delete(claim);
        }
        catch {
            // The claimed source still exists until the new link has been created.
            // Remove only the link this callback created so the source can be restored.
            if (created && File.Exists(claim)) {
                bool ownLink = hardLink ? HardLinkUtils.AreSameFile(original, keeper)
                    : new FileInfo(original).LinkTarget == keeper;
                if (ownLink) File.Delete(original);
            }
            throw;
        }
    }
}
