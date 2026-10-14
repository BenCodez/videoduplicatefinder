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
	readonly HashSet<string> _observedReviewJournals = new(PathComparer.ForCurrentPlatform);

	// Tests exercise trash/link failure without touching the user's recycle bin.
	internal Func<string, string, bool> TrashMover { get; set; } = MoveClaimToTrash;
	internal Action<string, string, bool> LinkCreator { get; set; } = CreateLink;
	internal Action<string, string> FileMover { get; set; } = File.Move;

	async Task<IDisposable> AcquireMoveRetentionLeaseAsync() {
		string folder = _resultStateFolder ?? throw new IOException("These results have no verified file snapshots.");
		SafePaths.RequireDirectory(folder);
		// Establish an empty first generation only for a genuinely new optional
		// Web state. Initialization preserves a concurrently committed generation.
		if (!ObserveReviewJournal(folder))
			await new ReviewStore(Path.Combine(folder, "ReviewedFiles.json")).InitializeAsync();
		_observedReviewJournals.Add(Path.GetFullPath(folder));
		return JournalWriteLease.Acquire(Path.Combine(folder, "ReviewedFiles.json"));
	}

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
			ObserveReviewJournal(_resultStateFolder);
		}
	}

	void ClearCleanupSnapshots() {
		_resultSnapshots.Clear();
		_resultStateFolder = null;
	}

	bool ObserveReviewJournal(string stateFolder) {
		string fullFolder = Path.GetFullPath(stateFolder);
		string path = Path.Combine(fullFolder, "ReviewedFiles.json");
		try {
			SafePaths.RequireDirectory(fullFolder);
			string policyPath = Path.Combine(fullFolder, CleanupProtectionPolicyStore.FileName);
			if (SafePaths.Exists(path) || SafePaths.Exists(path + ".bak") || SafePaths.Exists(path + ".lock") ||
				SafePaths.Exists(policyPath) || SafePaths.Exists(policyPath + ".bak"))
				_observedReviewJournals.Add(fullFolder);
		}
		catch {
			// Inaccessible state must never count as an empty first generation.
			_observedReviewJournals.Add(fullFolder);
		}
		return _observedReviewJournals.Contains(fullFolder);
	}

	async Task ValidateMoveRetentionAsync(FileIdentity identity) {
		string folder = _resultStateFolder ?? throw new IOException("These results have no verified file snapshots.");
		SafePaths.RequireDirectory(folder);
		// A new optional Web state need not have a review journal. Persistent policy
		// or lease files also prove initialization after restart; require its primary.
		if (!ObserveReviewJournal(folder)) return;
		// Renaming changes every hardlink alias's native stamp. Without a durable
		// restoration proof we must retain deliberate decisions on any such alias.
		await new CleanupProtectionGuard(folder).ValidateIdentityChangingMoveAsync(identity);
	}

	async Task<bool> LoadDatabaseWithResultsAuthorityAsync(string? folder) {
		try {
			bool loaded = await ScanEngine.LoadDatabase(folder);
			if (!loaded) InvalidateFailedResultsDatabaseLoad();
			return loaded;
		}
		catch {
			InvalidateFailedResultsDatabaseLoad();
			throw;
		}
	}

	void InvalidateFailedResultsDatabaseLoad() {
		// LoadDatabase changes the global folder before importing its wrapper.
		// Keep other-folder results usable, but revoke authority for failed results loads.
		if (_resultStateFolder is not null && PathComparer.ForCurrentPlatform.Equals(
			Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()), Path.GetFullPath(_resultStateFolder)))
			ClearCleanupSnapshots();
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
					Path.GetFullPath(_resultStateFolder)) && !await LoadDatabaseWithResultsAuthorityAsync(_resultStateFolder)) {
					// A failed load can set the global folder while retaining another
					// database wrapper. Retries must require a fresh scan authority.
					ClearCleanupSnapshots();
					throw new IOException("The results database could not be loaded; files were retained.");
				}
				await guard.RecoverAsync();
				var requestedPaths = requested.Select(item => item.Path).ToHashSet(PathComparer.ForCurrentPlatform);
				var groups = _engine.Duplicates.GroupBy(item => item.GroupId)
					.ToDictionary(group => group.Key, group => group.ToList());
				var sw = System.Diagnostics.Stopwatch.StartNew();
				foreach (var item in requested) {
					Dictionary<DuplicateItem, FileIdentity>? keeperAliases = null;
					bool hardLinkAttempted = false;
					try {
						if (!_engine.Duplicates.Contains(item))
							throw new IOException("This file is no longer part of the current results.");
						var target = CaptureReviewedIdentity(item)
							?? throw new IOException("The file is unavailable or has changed since scanning. Rescan before removing it.");
						var protection = await guard.InspectAsync(target);
						if (protection.IsProtected) throw new IOException(protection.Reason);
						if (!groups.TryGetValue(item.GroupId, out var group))
							throw new IOException("The duplicate group is no longer available.");
						FileIdentity? keeper = null;
						foreach (var candidate in group.Where(row => !requestedPaths.Contains(row.Path))
							.OrderByDescending(row => row.Similarity)) {
							var identity = CaptureReviewedIdentity(candidate);
							if (identity is null || identity.StableFileId == target.StableFileId ||
								PathComparer.ForCurrentPlatform.Equals(identity.Path, target.Path)) continue;
							keeper = identity;
							break;
						}
						if (keeper is null)
							throw new IOException("This group has no unselected, independent remaining file with a current scan identity.");
						if (createLinks && hardLinks) await guard.ValidateHardLinkKeeperAsync(keeper);
						// The group list predates this batch: successful removals no longer
						// have snapshots, but every remaining member must still be verified.
						var groupMembers = group.Where(row => _engine.Duplicates.Contains(row))
							.Select(row => CaptureReviewedIdentity(row)
								?? throw new IOException("A group member is unavailable or has changed since scanning. Rescan before removing files."))
							.ToList();
						if (createLinks && hardLinks) keeperAliases = CaptureKeeperAliases(keeper);
						await guard.RemoveAsync(target, keeper, claim => {
							// Recheck at the mutation boundary in case another frontend
							// protected the keeper while its identities were validated.
							if (createLinks && hardLinks) guard.ValidateHardLinkKeeperAsync(keeper).GetAwaiter().GetResult();
							if (createLinks) {
								hardLinkAttempted = hardLinks;
								CreateVerifiedReplacementLink(claim, target.Path, keeper.Path, hardLinks);
							}
							else if (permanent) File.Delete(claim);
							else if (!TrashMover(claim, target.Path))
								throw new IOException("Moving to trash failed; the original file was retained.");
						}, groupMembers: groupMembers);
						_engine.Duplicates.Remove(item);
						_resultSnapshots.Remove(item);
						ScanEngine.RemoveFromDatabase(new FileEntry { Path = item.Path });
						result.FreedBytes += Math.Max(0, target.Length);
						result.Done++;
					}
					catch (Exception ex) {
						result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
						result.Failed++;
					}
					finally {
						// A link attempt can change every keeper alias's stamp even if
						// a later callback or journal step fails. Refresh only rows that
						// were fully verified as that native snapshot before the attempt.
						if (hardLinkAttempted) RefreshKeeperAliasesAfterLink(keeperAliases!);
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

	Dictionary<DuplicateItem, FileIdentity> CaptureKeeperAliases(FileIdentity keeper) {
		var aliases = new Dictionary<DuplicateItem, FileIdentity>();
		foreach (var (row, snapshot) in _resultSnapshots.ToArray()) {
			if (!_engine.Duplicates.Contains(row) || snapshot.StableFileId != keeper.StableFileId ||
				snapshot.Length != keeper.Length || snapshot.LastWriteUtc != keeper.LastWriteUtc ||
				snapshot.CreationUtc != keeper.CreationUtc || snapshot.ChangeStamp != keeper.ChangeStamp) continue;
			var previous = CaptureReviewedIdentity(row);
			if (previous is not null && keeper.SameFileSnapshot(previous)) aliases.Add(row, previous);
		}
		return aliases;
	}

	void RefreshKeeperAliasesAfterLink(IReadOnlyDictionary<DuplicateItem, FileIdentity> aliases) {
		foreach (var (row, previous) in aliases) {
			if (!_engine.Duplicates.Contains(row) || !PathComparer.ForCurrentPlatform.Equals(row.Path, previous.Path) ||
				!_resultSnapshots.TryGetValue(row, out var original) || !original.Matches(previous)) continue;
			var current = IdentityReader.Capture(previous.Path, true, CancellationToken.None);
			var snapshot = FileIdentity.CaptureSnapshot(previous.Path);
			if (current is not null && snapshot is not null && snapshot.Matches(current) &&
				previous.MatchesRelocated(current) && MatchesScannedMetadata(row, current.Length, current.LastWriteUtc))
				_resultSnapshots[row] = snapshot;
		}
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
