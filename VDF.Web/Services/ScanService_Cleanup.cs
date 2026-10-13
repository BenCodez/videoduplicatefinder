// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services;

public sealed partial class ScanService {
	Dictionary<DuplicateItem, FileSnapshot> _resultSnapshots = new();
	string? _resultStateFolder;

	// Tests exercise trash/link failure without touching the user's recycle bin.
	internal Func<string, string, bool> TrashMover { get; set; } = MoveClaimToTrash;
	internal Action<string, string, bool> LinkCreator { get; set; } = CreateLink;

	/// <summary>Anchor the files before completed results become available for cleanup.</summary>
	internal void CaptureCleanupSnapshots(string? actualStateFolder = null) {
		var snapshots = new Dictionary<DuplicateItem, FileSnapshot>();
		foreach (var item in _engine.Duplicates) {
			var snapshot = FileIdentity.CaptureSnapshot(item.Path);
			if (snapshot is not null && MatchesScannedMetadata(item, snapshot.Length, snapshot.LastWriteUtc))
				snapshots.Add(item, snapshot);
		}
		lock (_operationSync) {
			_resultSnapshots = snapshots;
			_resultStateFolder = actualStateFolder ?? CoreUtils.ResolveDatabaseFolder(Settings.CustomDatabaseFolder);
		}
	}

	void ClearCleanupSnapshots() {
		_resultSnapshots.Clear();
		_resultStateFolder = null;
	}

	static bool MatchesScannedMetadata(DuplicateItem item, long length, DateTime modifiedUtc) =>
		item.SizeLong == length &&
		SelectionPlanner.DateOf(new SelectionCandidate(item), SelectionDateSource.Modification) == modifiedUtc;

	FileIdentity? CaptureReviewedIdentity(DuplicateItem item) {
		if (!_resultSnapshots.TryGetValue(item, out var snapshot) || !snapshot.MatchesCurrent()) return null;
		var identity = FileIdentity.Capture(item.Path);
		return identity is not null && snapshot.Matches(identity) &&
			MatchesScannedMetadata(item, identity.Length, identity.LastWriteUtc) ? identity : null;
	}

	async Task<FileOpResult> RemoveOrReplaceItemsAsync(IEnumerable<DuplicateItem> items,
		bool permanent, bool createLinks, bool hardLinks) {
		var requested = items.Distinct().ToList();
		var result = new FileOpResult();
		string verb = createLinks ? (hardLinks ? "Creating hardlinks" : "Creating symlinks")
			: permanent ? "Deleting" : "Moving to trash";
		if (!TryBeginFileOp(verb, requested.Count, result)) return result;
		try {
			await Task.Run(async () => {
				if (_resultStateFolder is null) {
					result.Failed = requested.Count;
					result.Errors.Add("These results have no verified file snapshots. Rescan before removing files.");
					return;
				}
				var guard = new CleanupProtectionGuard(_resultStateFolder);
				guard.ClearCache();
				// Maintenance or edited settings can change the process-wide database.
				// Cleanup records must update the database that produced these results.
				if (!PathComparer.ForCurrentPlatform.Equals(Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()),
					Path.GetFullPath(_resultStateFolder)) && !await ScanEngine.LoadDatabase(_resultStateFolder))
					throw new IOException("The results database could not be loaded; files were retained.");
				await new LegacyRemovalService(Path.Combine(_resultStateFolder, "LegacyCleanupHistory.json")).RecoverAsync();
				var requestedPaths = requested.Select(item => item.Path).ToHashSet(PathComparer.ForCurrentPlatform);
				var groups = _engine.Duplicates.GroupBy(item => item.GroupId)
					.ToDictionary(group => group.Key, group => group.ToList());
				var sw = System.Diagnostics.Stopwatch.StartNew();
				foreach (var item in requested) {
					try {
						if (!_engine.Duplicates.Contains(item))
							throw new IOException("This file is no longer part of the current results.");
						var target = CaptureReviewedIdentity(item)
							?? throw new IOException("The file is unavailable or has changed since scanning. Rescan before removing it.");
						var protection = await guard.InspectAsync(target);
						if (protection.IsProtected) throw new IOException(protection.Reason);
						if (!groups.TryGetValue(item.GroupId, out var group))
							throw new IOException("The duplicate group is no longer available.");
						DuplicateItem? keeperRow = null;
						FileIdentity? keeper = null;
						foreach (var candidate in group.Where(row => !requestedPaths.Contains(row.Path))
							.OrderByDescending(row => row.Similarity)) {
							var identity = CaptureReviewedIdentity(candidate);
							if (identity is null || identity.StableFileId == target.StableFileId ||
								PathComparer.ForCurrentPlatform.Equals(identity.Path, target.Path)) continue;
							keeperRow = candidate;
							keeper = identity;
							break;
						}
						if (keeper is null)
							throw new IOException("This group has no unselected, independent remaining file with a current scan identity.");
						if (createLinks && hardLinks) await guard.ValidateHardLinkKeeperAsync(keeper);
						var groupMembers = group.Select(CaptureReviewedIdentity).OfType<FileIdentity>().ToList();
						await guard.RemoveAsync(target, keeper, claim => {
							// Recheck at the mutation boundary in case another frontend
							// protected the keeper while its identities were validated.
							if (createLinks && hardLinks) guard.ValidateHardLinkKeeperAsync(keeper).GetAwaiter().GetResult();
							if (createLinks) CreateVerifiedReplacementLink(claim, target.Path, keeper.Path, hardLinks);
							else if (permanent) File.Delete(claim);
							else if (!TrashMover(claim, target.Path))
								throw new IOException("Moving to trash failed; the original file was retained.");
						}, groupMembers: groupMembers);
						_engine.Duplicates.Remove(item);
						_resultSnapshots.Remove(item);
						ScanEngine.RemoveFromDatabase(new FileEntry { Path = item.Path });
						result.FreedBytes += Math.Max(0, target.Length);
						result.Done++;
						// Creating a hardlink changes the keeper's link count/change stamp.
						// Refresh only this verified, intentional change for the next item.
						if (createLinks && hardLinks) RefreshKeeperAfterLink(keeperRow!, keeper);
					}
					catch (Exception ex) {
						result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
						result.Failed++;
					}
					finally {
						FileOpCurrent++;
						if (sw.ElapsedMilliseconds >= 100) { sw.Restart(); Notify(); }
					}
				}
				if (result.Done > 0) {
					try { ScanEngine.SaveDatabase(); }
					catch (Exception ex) { result.Errors.Add($"Files were removed, but saving the scan database failed: {ex.Message}"); }
				}
				DropSingletonGroups();
			});
		}
		catch (Exception ex) {
			result.Failed = requested.Count - result.Done;
			result.Errors.Add($"Cleanup could not complete; remaining files were retained: {ex.Message}");
		}
		finally { EndFileOp(); }
		return result;
	}

	void RefreshKeeperAfterLink(DuplicateItem row, FileIdentity previous) {
		var current = FileIdentity.Capture(previous.Path);
		var snapshot = FileIdentity.CaptureSnapshot(previous.Path);
		if (current is not null && snapshot is not null && snapshot.Matches(current) &&
			current.StableFileId == previous.StableFileId && current.Sha256 == previous.Sha256 &&
			current.Length == previous.Length && current.LastWriteUtc == previous.LastWriteUtc)
			_resultSnapshots[row] = snapshot;
	}

	static bool MoveClaimToTrash(string path, string originalPath) {
		if (!OperatingSystem.IsWindows()) return FileUtils.MoveToTrash(path, originalPath);
		var operation = new FileUtils.SHFILEOPSTRUCT {
			wFunc = FileUtils.FileOperationType.FO_DELETE,
			pFrom = path + "\0\0",
			fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO | FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
				FileUtils.FileOperationFlags.FOF_NOERRORUI | FileUtils.FileOperationFlags.FOF_SILENT
		};
		return FileUtils.SHFileOperation(ref operation) == 0 && !File.Exists(path);
	}

	static void CreateLink(string original, string keeper, bool hardLink) {
		if (hardLink) HardLinkUtils.CreateHardLink(original, keeper);
		else File.CreateSymbolicLink(original, keeper);
	}

	void CreateVerifiedReplacementLink(string claim, string original, string keeper, bool hardLink) {
		bool created = false;
		try {
			LinkCreator(original, keeper, hardLink);
			created = true;
			if (!IsOwnLink(original, keeper, hardLink))
				throw new IOException("The replacement link could not be verified; the original claim was retained.");
			File.Delete(claim);
		}
		catch {
			if (created && File.Exists(claim) && IsOwnLink(original, keeper, hardLink)) File.Delete(original);
			throw;
		}
	}

	static bool IsOwnLink(string original, string keeper, bool hardLink) =>
		hardLink ? HardLinkUtils.AreSameFile(original, keeper) : new FileInfo(original).LinkTarget == keeper;
}
