// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Cleanup;

/// <summary>
/// Journals legacy trash/permanent removal and invokes the selected removal
/// mechanism only on a verified private claim. Recovery never replays removal.
/// </summary>
public sealed class LegacyRemovalService {
	const int MaximumRecords = 10_000;
	static readonly ConcurrentDictionary<string, SemaphoreSlim> JournalGates = new(PathComparer.ForCurrentPlatform);
	readonly string _journalPath;
	readonly SemaphoreSlim _journalGate;
	internal Func<string?, CancellationToken, Task>? ValidateProtectionAsync { get; set; }
	internal Func<FileIdentity, FileIdentity, FileIdentity, CancellationToken, Task>? RefreshRestoredProtectionAsync { get; set; }

	public LegacyRemovalService(string journalPath) {
		_journalPath = SafePaths.Normalize(journalPath, rejectTraversal: true);
		_journalGate = JournalGates.GetOrAdd(_journalPath, _ => new SemaphoreSlim(1, 1));
	}

	/// <summary>Restores interrupted claims without overwriting existing originals.</summary>
	public async Task RecoverAsync() {
		await CleanupOperationGate.Instance.WaitAsync().ConfigureAwait(false);
		try {
			await _journalGate.WaitAsync().ConfigureAwait(false);
			try {
				using var lease = AcquireJournalLease();
				LegacyRemovalEnvelope journal = await LoadAsync(CancellationToken.None).ConfigureAwait(false);
				var failures = new List<Exception>();
				bool changed = false;
				foreach (LegacyRemovalRecord record in journal.Records.Where(record => record.RecoveryRequired)) {
					changed = true;
					try { await ReconcileAsync(journal, record, "Interrupted removal.").ConfigureAwait(false); }
					catch (Exception ex) {
						RequireManualRecovery(record);
						failures.Add(ex);
					}
				}
				if (changed) await SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
				if (failures.Count > 0)
					throw new IOException("Some removal claims require manual recovery; no deletion was replayed.",
						new AggregateException(failures));
			}
			finally { _journalGate.Release(); }
		}
		finally { CleanupOperationGate.Instance.Release(); }
	}

	public async Task RemoveAsync(FileIdentity target, FileIdentity keeper, Action<string> removeClaim,
		bool protectedFile = false, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(keeper);
		ArgumentNullException.ThrowIfNull(removeClaim);
		if (protectedFile) throw new InvalidOperationException("Protected media cannot be removed.");
		ct.ThrowIfCancellationRequested();
		await CleanupOperationGate.Instance.WaitAsync(ct).ConfigureAwait(false);
		try {
			await _journalGate.WaitAsync(ct).ConfigureAwait(false);
			try {
				using var lease = AcquireJournalLease();
				LegacyRemovalEnvelope journal = await LoadAsync(ct).ConfigureAwait(false);
				if (journal.Records.Any(record => record.RecoveryRequired &&
					(record.Target.StableFileId == target.StableFileId || record.Target.StableFileId == keeper.StableFileId ||
					SafePaths.Same(record.Target.Path, target.Path) || SafePaths.Same(record.Target.Path, keeper.Path) ||
					SafePaths.Same(record.ClaimPath, target.Path) || SafePaths.Same(record.ClaimPath, keeper.Path))))
					throw new IOException("An earlier removal of this media requires recovery first.");
				await Task.Run(() => ValidateBeforeClaim(target, keeper, ct), ct).ConfigureAwait(false);
				if (ValidateProtectionAsync is not null) await ValidateProtectionAsync(null, ct).ConfigureAwait(false);
				MakeRoom(journal);
				string id = Guid.NewGuid().ToString("N");
				var record = new LegacyRemovalRecord {
					Id = id,
					Target = target,
					Keeper = keeper,
					ClaimPath = ClaimPath(target.Path, id),
					State = LegacyRemovalState.Prepared,
					RecoveryRequired = true,
					Outcome = "Prepared; the original has not yet been claimed."
				};
				journal.Records.Add(record);
				// The durable prepared generation must exist before the source rename.
				await SaveAsync(journal, ct).ConfigureAwait(false);
				bool claimed = false;
				try {
					ct.ThrowIfCancellationRequested();
					SafePaths.RequireRegularFile(target.Path);
					SafePaths.RequireNoLinks(record.ClaimPath, allowMissing: true);
					File.Move(target.Path, record.ClaimPath, overwrite: false);
					claimed = true;
					record.State = LegacyRemovalState.Claimed;
					DurableFileOperations.FlushDirectory(System.IO.Path.GetDirectoryName(target.Path)!);
					record.Outcome = "Claimed; removal has not yet completed.";
					record.UpdatedUtc = DateTime.UtcNow;
					await SaveAsync(journal, ct).ConfigureAwait(false);
					// Hash after the rename and journal flush. A swap at the public source
					// path cannot cause the callback to remove an unrelated replacement.
					FileIdentity? verifiedClaim = null;
					await Task.Run(() => {
						// Verify the potentially large keeper first. A source writer must
						// not get an entire keeper-hash interval after source validation.
						SafePaths.RequireRegularFile(keeper.Path);
						if (!keeper.Matches(IdentityReader.Capture(keeper.Path, true, ct)))
							throw new IOException("The remaining keeper is unavailable or has changed.");
						SafePaths.RequireRegularFile(record.ClaimPath);
						verifiedClaim = IdentityReader.Capture(record.ClaimPath, true, ct);
						if (!target.MatchesRelocated(verifiedClaim))
							throw new IOException("The claimed media no longer matches the reviewed file.");
						// Full hashing the claim can itself take time. Recheck both native
						// identities/change stamps without another long hash before removal.
						if (!IdentityReader.MatchesCurrentSnapshot(keeper))
							throw new IOException("The remaining keeper changed or lacks a reliable current filesystem snapshot.");
						if (!IdentityReader.MatchesCurrentSnapshot(verifiedClaim!))
							throw new IOException("The claimed media changed or lacks a reliable current filesystem snapshot.");
					}, ct).ConfigureAwait(false);
					if (ValidateProtectionAsync is not null) await ValidateProtectionAsync(record.ClaimPath, ct).ConfigureAwait(false);
					if (!IdentityReader.MatchesCurrentSnapshot(keeper) || verifiedClaim is null || !IdentityReader.MatchesCurrentSnapshot(verifiedClaim))
						throw new IOException("The source or remaining keeper changed during live protection checks.");
					ct.ThrowIfCancellationRequested();
					removeClaim(record.ClaimPath);
					if (SafePaths.Exists(record.ClaimPath))
						throw new IOException("The removal mechanism left the private claim in place.");
					record.State = LegacyRemovalState.Completed;
					record.RecoveryRequired = false;
					DurableFileOperations.FlushDirectory(System.IO.Path.GetDirectoryName(target.Path)!);
					record.Outcome = "Removal completed; the private claim is absent.";
					record.UpdatedUtc = DateTime.UtcNow;
					// Removal has already happened. Persist its outcome even when a caller
					// requests cancellation immediately after the synchronous callback.
					await SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception removalError) {
					var errors = new List<Exception> { removalError };
					try { await ReconcileAsync(journal, record, "Removal failed.", allowChangedClaim: claimed).ConfigureAwait(false); }
					catch (Exception recoveryError) {
						RequireManualRecovery(record);
						errors.Add(recoveryError);
					}
					try { await SaveAsync(journal, CancellationToken.None).ConfigureAwait(false); }
					catch (Exception journalError) { errors.Add(journalError); }
					if (errors.Count > 1)
						throw new IOException("Removal failed and recovery is incomplete; the journal and any private claim were preserved.",
							new AggregateException(errors));
					throw;
				}
			}
			finally { _journalGate.Release(); }
		}
		finally { CleanupOperationGate.Instance.Release(); }
	}

	void ValidateBeforeClaim(FileIdentity target, FileIdentity keeper, CancellationToken ct) {
		if (!target.IsWellFormed() || !keeper.IsWellFormed())
			throw new IOException("A valid reviewed identity is required for both media files.");
		SafePaths.Normalize(target.Path, rejectTraversal: true);
		SafePaths.Normalize(keeper.Path, rejectTraversal: true);
		if (SafePaths.Same(target.Path, keeper.Path) || target.StableFileId == keeper.StableFileId)
			throw new IOException("Removal requires a different, independent remaining file.");
		if (ReservedJournalPath(target.Path) || ReservedJournalPath(keeper.Path))
			throw new IOException("The removal journal cannot be used as media.");
		SafePaths.RequireRegularFile(target.Path);
		SafePaths.RequireRegularFile(keeper.Path);
		if (!keeper.Matches(IdentityReader.Capture(keeper.Path, true, ct)))
			throw new IOException("The remaining keeper is unavailable or has changed since review.");
		if (!target.Matches(IdentityReader.Capture(target.Path, true, ct)))
			throw new IOException("The removal target is unavailable or has changed since review.");
		if (!IdentityReader.MatchesCurrentSnapshot(keeper) || !IdentityReader.MatchesCurrentSnapshot(target))
			throw new IOException("The reviewed media changed or lacks reliable current filesystem snapshots.");
	}

	bool ReservedJournalPath(string path) => SafePaths.Same(path, _journalPath) ||
		SafePaths.Same(path, _journalPath + ".bak") || SafePaths.Same(path, _journalPath + ".lock");

	FileStream AcquireJournalLease() {
		string path = _journalPath + ".lock";
		SafePaths.EnsureParentDirectory(path);
		SafePaths.RequireNoLinks(path, allowMissing: true);
		if (SafePaths.Exists(path)) SafePaths.RequireRegularFile(path);
		return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
	}
	static string ClaimPath(string target, string id) => System.IO.Path.Combine(
		System.IO.Path.GetDirectoryName(target)!, $".vdf-{id}.holding");

	async Task ReconcileAsync(LegacyRemovalEnvelope journal, LegacyRemovalRecord record, string prefix, bool allowChangedClaim = false) {
		if (record.ProtectionRefreshClaim is not null && RefreshRestoredProtectionAsync is null)
			throw new IOException("Pending restored-source protection must be refreshed by the guarded recovery service.");
		SafePaths.RequireDirectory(System.IO.Path.GetDirectoryName(record.Target.Path)!);
		bool claimExists = SafePaths.Exists(record.ClaimPath);
		bool originalExists = SafePaths.Exists(record.Target.Path);
		if (claimExists) {
			SafePaths.RequireRegularFile(record.ClaimPath);
			FileIdentity? claim = await Task.Run(() => IdentityReader.Capture(record.ClaimPath, true, CancellationToken.None)).ConfigureAwait(false);
			bool reviewedClaim = record.Target.MatchesRelocated(claim);
			if (claim is null || (!reviewedClaim && !allowChangedClaim))
				throw new IOException("The private claim no longer matches the original; it was preserved for manual recovery.");
			if (originalExists)
				throw new IOException("The original path already exists; neither it nor the private claim was overwritten.");
			if (reviewedClaim && RefreshRestoredProtectionAsync is not null) {
				// Persist the validated post-claim snapshot before restoring. If review
				// refresh fails or the process stops after the rename, recovery can
				// retry against this proof without trusting an arbitrary changed file.
				record.ProtectionRefreshClaim = claim;
				await SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
				if (!IdentityReader.MatchesCurrentSnapshot(claim))
					throw new IOException("The verified source claim changed before restoration.");
			}
			SafePaths.RequireNoLinks(record.Target.Path, allowMissing: true);
			File.Move(record.ClaimPath, record.Target.Path, overwrite: false);
			DurableFileOperations.FlushDirectory(System.IO.Path.GetDirectoryName(record.Target.Path)!);
			FileIdentity? restored = await Task.Run(() => IdentityReader.Capture(record.Target.Path, true, CancellationToken.None)).ConfigureAwait(false);
			if (!claim.MatchesRelocated(restored))
				throw new IOException("The restored source could not be verified; manual recovery is required.");
			if (reviewedClaim && RefreshRestoredProtectionAsync is not null) {
				await RefreshRestoredProtectionAsync(record.Target, claim, restored!, CancellationToken.None).ConfigureAwait(false);
				record.ProtectionRefreshClaim = null;
			}
			record.State = LegacyRemovalState.Failed;
			record.RecoveryRequired = false;
			record.Outcome = prefix + " Verified source claim restored without overwriting another file." +
				(reviewedClaim ? string.Empty : " Its identity changed; the restored file requires manual review.");
		}
		else if (originalExists) {
			FileIdentity? original = await Task.Run(() => IdentityReader.Capture(record.Target.Path, true, CancellationToken.None)).ConfigureAwait(false);
			if (!record.Target.MatchesRelocated(original))
				throw new IOException("The claim is missing and the original path was replaced; the replacement was preserved.");
			if (record.ProtectionRefreshClaim is not null) {
				if (RefreshRestoredProtectionAsync is null || !record.ProtectionRefreshClaim.MatchesRelocated(original))
					throw new IOException("The restored source requires its pending protection refresh before recovery can complete.");
				await RefreshRestoredProtectionAsync(record.Target, record.ProtectionRefreshClaim, original!, CancellationToken.None).ConfigureAwait(false);
				record.ProtectionRefreshClaim = null;
			}
			else if (RefreshRestoredProtectionAsync is not null && !record.Target.Matches(original))
				throw new IOException("The original changed without a durable restoration proof; protection requires manual recovery.");
			record.State = LegacyRemovalState.Failed;
			record.RecoveryRequired = false;
			record.Outcome = prefix + " Claim absent and original verified; removal cancelled.";
		}
		else {
			if (record.ProtectionRefreshClaim is not null)
				throw new IOException("The source and claim are unavailable while protection refresh is pending; manual recovery is required.");
			record.State = LegacyRemovalState.Completed;
			record.RecoveryRequired = false;
			record.Outcome = prefix + " Both original and claim are absent; outcome is uncertain and deletion will not be replayed.";
		}
		record.UpdatedUtc = DateTime.UtcNow;
	}

	internal async Task<bool> HasPendingRecoveryAsync(FileIdentity identity, string? activeClaim, CancellationToken ct) {
		var journal = await DurableJsonFile.ReadPrimaryAsync(_journalPath,
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, ValidateEnvelope, ct).ConfigureAwait(false);
		if (journal is null && SafePaths.Exists(_journalPath + ".bak"))
			throw new IOException("The current removal journal is missing; recovery is required before cleanup.");
		return journal?.Records.Any(record => record.RecoveryRequired && record.Target.StableFileId == identity.StableFileId &&
			(activeClaim is null || !SafePaths.Same(activeClaim, record.ClaimPath))) == true;
	}

	static void RequireManualRecovery(LegacyRemovalRecord record) {
		record.State = LegacyRemovalState.Failed;
		record.RecoveryRequired = true;
		record.Outcome = "Recovery could not safely restore the source; any claim and existing original were preserved for manual review.";
		record.UpdatedUtc = DateTime.UtcNow;
	}

	async Task<LegacyRemovalEnvelope> LoadAsync(CancellationToken ct) {
		LegacyRemovalEnvelope journal = await DurableJsonFile.ReadAsync(_journalPath,
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, ValidateEnvelope, ct).ConfigureAwait(false) ?? new();
		if (journal.Records.Any(record => ReservedJournalPath(record.Target.Path) ||
			ReservedJournalPath(record.Keeper.Path) || ReservedJournalPath(record.ClaimPath)))
			throw new InvalidDataException("The removal journal overlaps media or a private claim; it was preserved.");
		// Version 2 makes pending protection refresh mandatory for recovery. Older
		// binaries reject it instead of silently ignoring the durable claim proof.
		journal.Version = LegacyRemovalEnvelope.CurrentVersion;
		return journal;
	}

	Task SaveAsync(LegacyRemovalEnvelope journal, CancellationToken ct) =>
		DurableJsonFile.WriteAsync(_journalPath, journal, LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope,
			ValidateEnvelope, ct);

	static void MakeRoom(LegacyRemovalEnvelope journal) {
		if (journal.Records.Count < MaximumRecords) return;
		int oldest = journal.Records.FindIndex(record => !record.RecoveryRequired);
		if (oldest < 0) throw new IOException("The removal journal has too many unresolved claims; recovery is required.");
		journal.Records.RemoveAt(oldest);
	}

	internal static bool ValidateEnvelope(LegacyRemovalEnvelope journal) {
		if (journal.Version is not (1 or LegacyRemovalEnvelope.CurrentVersion))
			throw new NotSupportedException("The removal journal schema version is unsupported; it was preserved.");
		if (journal.Records is null || journal.Records.Count > MaximumRecords) return false;
		if (journal.Version == 1 && journal.Records.Any(record => record?.ProtectionRefreshClaim is not null)) return false;
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var claims = new HashSet<string>(PathComparer.ForCurrentPlatform);
		foreach (LegacyRemovalRecord record in journal.Records) {
			if (record is null || !Guid.TryParseExact(record.Id, "N", out _) || !ids.Add(record.Id) ||
				record.Target is null || record.Keeper is null || record.ClaimPath is null ||
				record.Outcome is null || record.Outcome.Length > 1024 ||
				!Enum.IsDefined(record.State) || record.CreatedUtc.Kind != DateTimeKind.Utc || record.UpdatedUtc.Kind != DateTimeKind.Utc ||
				(record.State == LegacyRemovalState.Completed && record.RecoveryRequired) ||
				(record.State is LegacyRemovalState.Prepared or LegacyRemovalState.Claimed && !record.RecoveryRequired))
				return false;
			try {
				if (!record.Target.IsWellFormed() || !record.Keeper.IsWellFormed() ||
					SafePaths.Same(record.Target.Path, record.Keeper.Path) || record.Target.StableFileId == record.Keeper.StableFileId ||
					!SafePaths.Same(SafePaths.Normalize(record.ClaimPath, rejectTraversal: true), record.ClaimPath) ||
					!SafePaths.Same(record.ClaimPath, ClaimPath(record.Target.Path, record.Id)) ||
					(record.ProtectionRefreshClaim is not null && (!record.RecoveryRequired ||
						!record.ProtectionRefreshClaim.IsWellFormed() || !SafePaths.Same(record.ProtectionRefreshClaim.Path, record.ClaimPath) ||
						!record.Target.MatchesRelocated(record.ProtectionRefreshClaim))) ||
					!claims.Add(record.ClaimPath)) return false;
			}
			catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
		}
		return true;
	}
}

internal enum LegacyRemovalState { Prepared, Claimed, Completed, Failed }

internal sealed class LegacyRemovalRecord {
	[JsonRequired]
	public string Id { get; set; } = string.Empty;
	[JsonRequired]
	public FileIdentity Target { get; set; } = new();
	[JsonRequired]
	public FileIdentity Keeper { get; set; } = new();
	[JsonRequired]
	public string ClaimPath { get; set; } = string.Empty;
	[JsonRequired]
	public LegacyRemovalState State { get; set; }
	[JsonRequired]
	public bool RecoveryRequired { get; set; }
	public FileIdentity? ProtectionRefreshClaim { get; set; }
	[JsonRequired]
	public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
	[JsonRequired]
	public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
	[JsonRequired]
	public string Outcome { get; set; } = string.Empty;
}

internal sealed class LegacyRemovalEnvelope {
	internal const int CurrentVersion = 2;
	[JsonRequired]
	public int Version { get; set; } = CurrentVersion;
	[JsonRequired]
	public List<LegacyRemovalRecord> Records { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LegacyRemovalEnvelope))]
internal partial class LegacyRemovalJsonContext : JsonSerializerContext { }
