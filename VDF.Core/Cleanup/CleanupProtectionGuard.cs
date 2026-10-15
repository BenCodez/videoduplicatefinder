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
	HashSet<(string Id, long Length, DateTime Modified, DateTime Created, string Stamp, string Hash)> _retainedFiles = new();
	internal Func<string?, CancellationToken, Task>? BeforeLiveCheckAsync { get; set; }
	internal Func<string?, CancellationToken, Task>? AfterLiveCheckAsync { get; set; }
	public CleanupProtectionGuard(string stateFolder) {
		_stateFolder = SafePaths.Normalize(stateFolder, rejectTraversal: true);
		SafePaths.RequireDirectory(_stateFolder);
	}
	public void ClearCache() => _nfo.ClearCache();
	public async Task RecoverAsync() {
		await CreateRemovalService().RecoverAsync().ConfigureAwait(false);
		var records = await QuarantineHistoryAsync().ConfigureAwait(false);
		if (records.Any(record => record.ProtectionRefreshClaim is not null || record.State == QuarantineState.NeedsAttention))
			throw new IOException("Some quarantine sources require recovery or their pending protection refresh before cleanup.");
	}

	public QuarantineService CreateQuarantineService() => new(Path.Combine(_stateFolder, "QuarantineHistory.json")) {
		RefreshRestoredProtectionAsync = RefreshRestoredProtectionAsync
	};
	public Task<IReadOnlyList<QuarantineRecord>> QuarantineHistoryAsync() => CreateQuarantineService().HistoryAsync();

	LegacyRemovalService CreateRemovalService() => new(Path.Combine(_stateFolder, "LegacyCleanupHistory.json")) {
		RefreshRestoredProtectionAsync = RefreshRestoredProtectionAsync
	};

	public Task<CleanupProtectionCheck> InspectAsync(FileIdentity target, CancellationToken ct = default) =>
		InspectCoreAsync(target, null, ct);

	/// <summary>A hardlink changes keeper metadata; deliberate retention cannot cross that unjournaled change.</summary>
	public Task ValidateHardLinkKeeperAsync(FileIdentity keeper, CancellationToken ct = default) =>
		ValidateNativeIdentityMutationAsync(keeper, "Hardlink replacement", ct);

	internal Task ValidateIdentityChangingMoveAsync(FileIdentity source, CancellationToken ct = default) =>
		ValidateNativeIdentityMutationAsync(source, "Moving", ct);

	async Task ValidateNativeIdentityMutationAsync(FileIdentity keeper, string operation, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(keeper);
		if (!keeper.IsWellFormed()) throw new IOException("A valid keeper identity is required.");
		await GetReviewsAsync(ct).ConfigureAwait(false);
		await RequireNoPendingRecoveryAsync(keeper, null, ct).ConfigureAwait(false);
		// All hardlink aliases share the metadata that linking changes. Index by
		// the verified native/content snapshot so another path's protection counts.
		if (_retainedFiles.Contains(RetentionKey(keeper)))
			throw new IOException($"{operation} would change a deliberately protected file's identity. Change the review explicitly before this operation.");
		// Callers can reach this check after their final media validation. Journal
		// reads above may await; a replacement must not inherit that earlier proof.
		if (!IdentityReader.MatchesCurrentSnapshot(keeper))
			throw new IOException($"{operation} requires the reviewed file's unchanged current native snapshot.");
	}

	static (string, long, DateTime, DateTime, string, string) RetentionKey(FileIdentity identity) =>
		(identity.StableFileId, identity.Length, identity.LastWriteUtc, identity.CreationUtc, identity.ChangeStamp, identity.Sha256);

	async Task<CleanupProtectionCheck> InspectCoreAsync(FileIdentity target, string? claimPath, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(target);
		if (!target.IsWellFormed()) throw new IOException("A valid reviewed media identity is required.");
		CleanupProtectionPolicy policy = await CleanupProtectionPolicyStore.LoadAsync(_stateFolder, ct).ConfigureAwait(false);
		await GetReviewsAsync(ct).ConfigureAwait(false);
		await RequireNoPendingRecoveryAsync(target, claimPath, ct).ConfigureAwait(false);
		FileIdentity? currentClaim = claimPath is null ? null :
			// Removal already force-hashed this claim. Reuse its digest only when
			// the native snapshot is unchanged; absent/unreliable stamps rehash.
			await Task.Run(() => IdentityReader.Capture(claimPath, false, ct), ct).ConfigureAwait(false);
		if (claimPath is not null && !target.MatchesRelocated(currentClaim))
			throw new IOException("The claimed source cannot currently be validated for protection checks.");
		if (_retainedFiles.Contains(RetentionKey(target)) ||
			(currentClaim is not null && _retainedFiles.Contains(RetentionKey(currentClaim))))
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
			await RequireNoPendingRecoveryAsync(member, claimPath, ct).ConfigureAwait(false);
			if (!reviews.TryGetValue(member.Path, out var record) ||
				record.Choice is not (ReviewChoice.Ignore or ReviewChoice.RejectGroup)) continue;
			bool ownClaim = claimPath is not null && target is not null && SafePaths.Same(target.Path, member.Path);
			var captured = await Task.Run(() => IdentityReader.Capture(ownClaim ? claimPath! : member.Path, true, ct), ct).ConfigureAwait(false);
			// A verified rollback can refresh the saved veto while the caller still
			// holds its old group snapshot. Validate that snapshot before treating a
			// mismatched review as irrelevant to a genuinely replaced file.
			if (captured is null || (ownClaim ? !target!.Matches(member) || !target.MatchesRelocated(captured) : !member.Matches(captured)))
				throw new IOException("A reviewed group member is unavailable or changed. Refresh every member's identity before cleanup.");
			// The active source is at its private claim path. Both a preclaim veto
			// and a veto recorded against that exact claimed snapshot must survive;
			// requiring the absent original path would invalidate this live check.
			if (ownClaim ? record.Identity.Matches(member) || record.Identity.SameFileSnapshot(captured) : record.Identity.Matches(captured))
				throw new IOException("A validated group review decision requires retaining every member of this duplicate group.");
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
		var removal = CreateRemovalService();
		removal.AcquireProtectionLeaseAsync = (claim, token) => AcquireMutationLeaseAsync(claim, token);
		removal.ValidateProtectionAsync = (claim, token) => RequireUnprotectedAsync(target, group, claim, token);
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
		var service = CreateQuarantineService();
		service.AcquireProtectionLeaseAsync = (claim, token) => AcquireMutationLeaseAsync(claim, token);
		service.ValidateProtectionAsync = (claim, token) => RequireUnprotectedAsync(target, group, claim, token);
		return identical ? service.QuarantineExactAsync(target, keeper, destination, ct: ct) :
			service.QuarantineAsync(target, keeper, destination, ct: ct);
	}

	async Task RequireNoPendingRecoveryAsync(FileIdentity identity, string? activeClaim, CancellationToken ct) {
		if (await CreateRemovalService().HasPendingRecoveryAsync(identity, activeClaim, ct).ConfigureAwait(false) ||
			await CreateQuarantineService().HasPendingRecoveryAsync(identity, activeClaim, ct).ConfigureAwait(false))
			throw new IOException("A cleanup claim or protection refresh for this file or a hardlink alias requires recovery first.");
	}

	async Task RefreshRestoredProtectionAsync(FileIdentity original, FileIdentity claim, FileIdentity restored, CancellationToken ct) {
		if (!original.MatchesRelocated(claim) || !claim.MatchesRelocated(restored))
			throw new IOException("Only the verified original source claim can refresh deliberate protection after restoration.");
		var store = new ReviewStore(Path.Combine(_stateFolder, "ReviewedFiles.json"));
		await store.UpdateAsync(records => {
			var refreshed = new List<FileIdentity>();
			var result = records.Select(record => {
				if (!(record.ManuallyProtected || record.Choice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup) ||
					!(record.Identity.SameFileSnapshot(original) || record.Identity.SameFileSnapshot(claim))) return record;
				// A record may belong to any hardlink alias. Fully verify its recorded
				// path; a readable unrelated replacement remains an invalid old review.
				FileIdentity current = IdentityReader.Capture(record.Identity.Path, true, ct)
					?? throw new IOException("A retained hardlink alias cannot currently be validated; source protection refresh remains pending.");
				if (current.StableFileId != restored.StableFileId) return record;
				if (!restored.SameFileSnapshot(current))
					throw new IOException("A retained hardlink alias changed during source restoration; protection refresh remains pending.");
				refreshed.Add(current);
				return new ReviewRecord {
					Identity = current, Choice = record.Choice, ManuallyProtected = record.ManuallyProtected,
					AutomaticallySelected = record.AutomaticallySelected, IsKeeper = record.IsKeeper,
					Profile = record.Profile, Reason = record.Reason, UpdatedUtc = record.UpdatedUtc
				};
			}).ToArray();
			if (!IdentityReader.MatchesCurrentSnapshot(restored) || refreshed.Any(identity => !IdentityReader.MatchesCurrentSnapshot(identity)))
				throw new IOException("The restored source or a retained hardlink alias changed before protection refresh.");
			return result;
		}, ct).ConfigureAwait(false);
	}

	async Task<IDisposable> AcquireMutationLeaseAsync(string claim, CancellationToken ct) {
		if (BeforeLiveCheckAsync is not null) await BeforeLiveCheckAsync(claim, ct).ConfigureAwait(false);
		return await CleanupProtectionPolicyStore.AcquireMutationLeaseAsync(_stateFolder, ct).ConfigureAwait(false);
	}

	async Task RequireUnprotectedAsync(FileIdentity target, IReadOnlyList<FileIdentity>? group, string? claim, CancellationToken ct) {
		if (claim is null && BeforeLiveCheckAsync is not null) await BeforeLiveCheckAsync(claim, ct).ConfigureAwait(false);
		await ValidateGroupCoreAsync(group, target, claim, ct).ConfigureAwait(false);
		var protection = await InspectCoreAsync(target, claim, ct).ConfigureAwait(false);
		if (protection.IsProtected) throw new IOException(protection.Reason);
		if (AfterLiveCheckAsync is not null) await AfterLiveCheckAsync(claim, ct).ConfigureAwait(false);
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
			_retainedFiles = store.Records.Where(record => record.ManuallyProtected ||
				record.Choice is ReviewChoice.Keep or ReviewChoice.Ignore or ReviewChoice.RejectGroup)
				.Select(record => RetentionKey(record.Identity)).ToHashSet();
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
