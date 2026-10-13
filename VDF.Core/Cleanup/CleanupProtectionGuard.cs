// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;

namespace VDF.Core.Cleanup;

public sealed record CleanupProtectionCheck(bool IsProtected, string Reason);

/// <summary>Live protection checks shared by GUI, CLI, and optional Web cleanup.</summary>
public sealed class CleanupProtectionGuard {
	readonly string _stateFolder;
	readonly NfoFavouriteService _nfo = new();
	readonly SemaphoreSlim _reviewGate = new(1, 1);
	FileIdentity? _reviewIdentity;
	IReadOnlyDictionary<string, ReviewRecord> _reviews = new Dictionary<string, ReviewRecord>();
	internal Func<string?, CancellationToken, Task>? BeforeLiveCheckAsync { get; set; }
	public CleanupProtectionGuard(string stateFolder) {
		_stateFolder = SafePaths.Normalize(stateFolder, rejectTraversal: true);
		SafePaths.RequireDirectory(_stateFolder);
	}
	public void ClearCache() => _nfo.ClearCache();
	public Task RecoverAsync() => new LegacyRemovalService(Path.Combine(_stateFolder, "LegacyCleanupHistory.json")).RecoverAsync();

	public Task<CleanupProtectionCheck> InspectAsync(FileIdentity target, CancellationToken ct = default) =>
		InspectCoreAsync(target, null, ct);

	/// <summary>A hardlink changes keeper metadata; deliberate retention cannot cross that unjournaled change.</summary>
	public async Task ValidateHardLinkKeeperAsync(FileIdentity keeper, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(keeper);
		if (!keeper.IsWellFormed()) throw new IOException("A valid keeper identity is required.");
		var reviews = await GetReviewsAsync(ct).ConfigureAwait(false);
		if (reviews.TryGetValue(keeper.Path, out var review) && review.Identity.Matches(keeper) &&
			(review.ManuallyProtected || review.Choice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup))
			throw new IOException("Hardlink replacement would change a deliberately protected keeper's identity. Use quarantine or choose a keeper without a deliberate protection decision.");
	}

	async Task<CleanupProtectionCheck> InspectCoreAsync(FileIdentity target, string? claimPath, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(target);
		if (!target.IsWellFormed()) throw new IOException("A valid reviewed media identity is required.");
		CleanupProtectionPolicy policy = await CleanupProtectionPolicyStore.LoadAsync(_stateFolder, ct).ConfigureAwait(false);
		var reviews = await GetReviewsAsync(ct).ConfigureAwait(false);
		if (reviews.TryGetValue(target.Path, out var review) && review.Identity.Matches(target) &&
			(review.ManuallyProtected || review.Choice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup))
			return new(true, "A validated manual protection or review decision requires retaining this file.");
		var folder = new FolderRuleMatcher(policy.FolderRules, policy.OverlapPolicy).Match(target.Path);
		if (folder.Protected || folder.Uncertain) return new(true, folder.Reason);
		_nfo.RefreshDirectoryCache();
		NfoResult result = await Task.Run(() => claimPath is null ? _nfo.Inspect(target.Path, policy.NfoOptions) :
			_nfo.InspectClaimedVideo(target.Path, claimPath, policy.NfoOptions), ct).ConfigureAwait(false);
		return new(result.IsFavourite || result.IsUncertain, result.Reason);
	}

	public async Task ValidateGroupAsync(IEnumerable<FileIdentity> members, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(members);
		await ValidateGroupCoreAsync(members.ToArray(), null, null, ct).ConfigureAwait(false);
	}

	async Task ValidateGroupCoreAsync(IReadOnlyList<FileIdentity>? members, FileIdentity? target, string? claimPath, CancellationToken ct) {
		if (members is null) return;
		var reviews = await GetReviewsAsync(ct).ConfigureAwait(false);
		foreach (FileIdentity member in members) {
			ct.ThrowIfCancellationRequested();
			if (member is null || !member.IsWellFormed()) throw new IOException("A duplicate group contains an invalid file identity.");
			if (!reviews.TryGetValue(member.Path, out var record) || !record.Identity.Matches(member) ||
				record.Choice is not (ReviewChoice.Ignore or ReviewChoice.RejectGroup)) continue;
			bool current = claimPath is not null && target is not null && target.Matches(member) ||
				await Task.Run(() => member.Matches(IdentityReader.Capture(member.Path, true, ct)), ct).ConfigureAwait(false);
			if (current) throw new IOException("A validated group review decision requires retaining every member of this duplicate group.");
		}
	}

	public async Task ValidateRemovalAsync(FileIdentity target, FileIdentity keeper, bool requireByteIdentical = false, CancellationToken ct = default) {
		RequireIndependentPair(target, keeper, requireByteIdentical);
		await Task.Run(() => {
			if (!target.Matches(IdentityReader.Capture(target.Path, true, ct)) || !keeper.Matches(IdentityReader.Capture(keeper.Path, true, ct)))
				throw new IOException("The target or remaining keeper is unavailable or changed since review.");
		}, ct).ConfigureAwait(false);
		var result = await InspectAsync(target, ct).ConfigureAwait(false);
		if (result.IsProtected) throw new IOException(result.Reason);
	}

	public Task RemoveAsync(FileIdentity target, FileIdentity keeper, Action<string> removeClaim,
		IReadOnlyList<FileIdentity>? groupMembers = null, bool requireByteIdentical = false, CancellationToken ct = default) {
		RequireIndependentPair(target, keeper, requireByteIdentical);
		FileIdentity[]? group = groupMembers?.ToArray();
		var removal = new LegacyRemovalService(Path.Combine(_stateFolder, "LegacyCleanupHistory.json")) {
			ValidateProtectionAsync = (claim, token) => RequireUnprotectedAsync(target, group, claim, token)
		};
		return removal.RemoveAsync(target, keeper, removeClaim, ct: ct);
	}

	public Task<QuarantineRecord> QuarantineAsync(FileIdentity target, FileIdentity keeper, string destination,
		IReadOnlyList<FileIdentity>? groupMembers = null, CancellationToken ct = default) =>
		QuarantineCoreAsync(target, keeper, destination, groupMembers, false, ct);
	public Task<QuarantineRecord> QuarantineExactAsync(FileIdentity target, FileIdentity keeper, string destination,
		IReadOnlyList<FileIdentity>? groupMembers = null, CancellationToken ct = default) =>
		QuarantineCoreAsync(target, keeper, destination, groupMembers, true, ct);

	Task<QuarantineRecord> QuarantineCoreAsync(FileIdentity target, FileIdentity keeper, string destination,
		IReadOnlyList<FileIdentity>? groupMembers, bool identical, CancellationToken ct) {
		RequireIndependentPair(target, keeper, identical);
		FileIdentity[]? group = groupMembers?.ToArray();
		var service = new QuarantineService(Path.Combine(_stateFolder, "QuarantineHistory.json")) {
			ValidateProtectionAsync = (claim, token) => RequireUnprotectedAsync(target, group, claim, token)
		};
		return identical ? service.QuarantineExactAsync(target, keeper, destination, ct: ct) :
			service.QuarantineAsync(target, keeper, destination, ct: ct);
	}

	async Task RequireUnprotectedAsync(FileIdentity target, IReadOnlyList<FileIdentity>? group, string? claim, CancellationToken ct) {
		if (BeforeLiveCheckAsync is not null) await BeforeLiveCheckAsync(claim, ct).ConfigureAwait(false);
		await ValidateGroupCoreAsync(group, target, claim, ct).ConfigureAwait(false);
		var protection = await InspectCoreAsync(target, claim, ct).ConfigureAwait(false);
		if (protection.IsProtected) throw new IOException(protection.Reason);
	}

	async Task<IReadOnlyDictionary<string, ReviewRecord>> GetReviewsAsync(CancellationToken ct) {
		await _reviewGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			string path = Path.Combine(_stateFolder, "ReviewedFiles.json");
			SafePaths.RequireRegularFile(path);
			if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("The review journal exceeds its size limit.");
			FileIdentity current = await FileIdentity.CaptureAsync(path, ct).ConfigureAwait(false)
				?? throw new IOException("The current review journal cannot be validated.");
			if (_reviewIdentity is not null && _reviewIdentity.Matches(current)) return _reviews;
			var store = new ReviewStore(path);
			await store.LoadForCleanupAsync().ConfigureAwait(false);
			_reviews = store.Records.ToDictionary(record => record.Identity.Path, record => record, VDF.Core.Utils.PathComparer.ForCurrentPlatform);
			_reviewIdentity = current;
			return _reviews;
		}
		finally { _reviewGate.Release(); }
	}

	static void RequireIndependentPair(FileIdentity target, FileIdentity keeper, bool identical) {
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(keeper);
		if (!target.IsWellFormed() || !keeper.IsWellFormed() || SafePaths.Same(target.Path, keeper.Path) || target.StableFileId == keeper.StableFileId)
			throw new IOException("Cleanup requires reviewed, independent target and keeper identities.");
		if (identical && !target.HasSameBytes(keeper))
			throw new IOException("Automatic CLI cleanup requires byte-identical files; perceptual matches require manual GUI review.");
	}
}
