// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.CLI.Actions;

/// <summary>Exported result paths are candidates, never deletion authority.</summary>
internal static class GuardedDeletion {
	internal static async Task<int> ExecuteAsync(IReadOnlyList<DuplicateItem> marked,
		IReadOnlyList<DuplicateItem> allItems, string stateFolder, bool permanent, bool dryRun, CancellationToken ct) {
		var guard = new CleanupProtectionGuard(stateFolder);
		await CleanupProtectionPolicyStore.LoadAsync(stateFolder, ct);
		guard.ClearCache();
		var selectedPaths = marked.Select(item => Path.GetFullPath(item.Path)).ToHashSet(PathComparer.ForCurrentPlatform);
		var groups = allItems.GroupBy(item => item.GroupId).ToDictionary(group => group.Key, group => group.ToList());
		var identities = new Dictionary<string, FileIdentity?>(PathComparer.ForCurrentPlatform);
		async Task<FileIdentity?> Capture(DuplicateItem item) {
			string path = Path.GetFullPath(item.Path);
			if (identities.TryGetValue(path, out var cached)) return cached;
			var current = await FileIdentity.CaptureAsync(path, ct);
			if (current is not null && (item.SizeLong != current.Length || item.DateModified == default ||
				SelectionPlanner.DateOf(new(item), SelectionDateSource.Modification) != current.LastWriteUtc)) current = null;
			identities[path] = current;
			return current;
		}

		int failed = 0;
		foreach (DuplicateItem item in marked) {
			ct.ThrowIfCancellationRequested();
			try {
				FileIdentity target = await Capture(item) ?? throw new IOException("Target changed since these results; rescan before cleanup.");
				if (!groups.TryGetValue(item.GroupId, out var group)) throw new IOException("The complete duplicate group is missing.");
				var members = new List<FileIdentity>();
				FileIdentity? keeper = null;
				foreach (DuplicateItem member in group.OrderBy(member => member.Path, PathComparer.ForCurrentPlatform)) {
					var identity = await Capture(member);
					if (identity is null) continue;
					members.Add(identity);
					if (keeper is null && !selectedPaths.Contains(identity.Path) && identity.StableFileId != target.StableFileId &&
						identity.Length == target.Length && identity.Sha256 == target.Sha256) keeper = identity;
				}
				if (keeper is null) throw new IOException("No verified byte-identical, unselected keeper remains. Use the GUI for manual review of perceptual matches.");
				await guard.ValidateGroupAsync(members, ct);
				var protection = await guard.InspectAsync(target, ct);
				if (protection.IsProtected) throw new IOException(protection.Reason);
				if (dryRun) {
					await guard.ValidateRemovalAsync(target, keeper, requireByteIdentical: true, ct: ct);
					Console.WriteLine($"[dry-run] would delete: {item.Path}");
				}
				else {
					await guard.RemoveAsync(target, keeper, claim => {
						if (permanent) File.Delete(claim);
						else MoveToTrash(claim, target.Path);
					}, members, requireByteIdentical: true, ct: ct);
					Console.Error.WriteLine($"Deleted: {item.Path}");
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				Console.Error.WriteLine($"Cleanup refused or failed for '{item.Path}': {ex.Message}");
				failed++;
			}
		}
		return failed;
	}

	internal static void MoveToTrash(string claim, string originalPath) {
		if (OperatingSystem.IsWindows()) {
			Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(claim,
				Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
				Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
			if (File.Exists(claim)) throw new IOException("The recycle bin did not accept the private claim.");
		}
		else if (!FileUtils.MoveToTrash(claim, originalPath))
			throw new IOException("Trash is unavailable; the original file will be retained. There is no permanent-delete fallback.");
	}
}
