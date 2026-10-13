using System.Linq;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using System.Threading;

namespace VDF.GUI.ViewModels;

public partial class MainWindowVM {
    sealed record ApprovedRemoval(DuplicateItemVM Item, FileIdentity Target, FileIdentity Keeper);
    readonly SemaphoreSlim cleanupOperationGate = new(1, 1);
    string LegacyCleanupJournal => Path.Combine(CoreUtils.ResolveDatabaseFolder(Data.SettingsFile.Instance.CustomDatabaseFolder), "LegacyCleanupHistory.json");

    public async Task RecoverCleanupStateAsync() {
        await new VDF.Core.Cleanup.LegacyRemovalService(LegacyCleanupJournal).RecoverAsync();
        await new VDF.Core.Cleanup.QuarantineService(QuarantineHistoryFile).HistoryAsync();
    }

    async Task<Dictionary<DuplicateItemVM, ApprovedRemoval>> PrepareCleanupGuardAsync(IReadOnlyList<DuplicateItemVM> requested) {
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
