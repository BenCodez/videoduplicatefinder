using System.Buffers;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using VDF.Core.Review;

namespace VDF.Core.Cleanup;

/// <summary>
/// Optional, explicit quarantine. A flushed and SHA256-verified copy is committed
/// before the source is claimed under a private name and revalidated. Recovery
/// restores interrupted source claims and never resumes deletion automatically.
/// </summary>
public sealed class QuarantineService {
    private readonly string _historyPath;
    internal Func<QuarantineCheckpoint, CancellationToken, Task>? CheckpointAsync { get; set; }
	internal Func<string?, CancellationToken, Task>? ValidateProtectionAsync { get; set; }
	internal Func<FileIdentity, FileIdentity, FileIdentity, CancellationToken, Task>? RefreshRestoredProtectionAsync { get; set; }

    public QuarantineService(string historyPath) => _historyPath = SafePaths.Normalize(historyPath);

    public Task<QuarantinePreview> PreviewAsync(FileIdentity target, FileIdentity keeper, string destination,
        bool protectedFile = false, CancellationToken ct = default) => Task.Run(() =>
            ValidateRequest(target, keeper, destination, protectedFile, false, false, ct), ct);

    public Task<QuarantineRecord> QuarantineAsync(FileIdentity target, FileIdentity keeper, string destination,
        bool protectedFile = false, CancellationToken ct = default) =>
        QuarantineInternalAsync(target, keeper, destination, protectedFile, false, ct);

    /// <summary>Automatic callers must use this entry point; perceptual identity is insufficient.</summary>
    public Task<QuarantineRecord> QuarantineExactAsync(FileIdentity target, FileIdentity keeper, string destination,
        bool protectedFile = false, CancellationToken ct = default) =>
        QuarantineInternalAsync(target, keeper, destination, protectedFile, true, ct);

    public async Task<IReadOnlyList<QuarantineRecord>> HistoryAsync() {
        await CleanupOperationGate.Instance.WaitAsync().ConfigureAwait(false);
        try {
            using var lease = AcquireHistoryLease();
            var records = await LoadAsync().ConfigureAwait(false);
            if (await RecoverPendingAsync(records).ConfigureAwait(false))
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
            return records.Select(Clone).ToList().AsReadOnly();
        }
        finally { CleanupOperationGate.Instance.Release(); }
    }

    private async Task<QuarantineRecord> QuarantineInternalAsync(FileIdentity target, FileIdentity keeper,
        string destination, bool protectedFile, bool requireIdentical, CancellationToken ct) {
        await CleanupOperationGate.Instance.WaitAsync(ct).ConfigureAwait(false);
        try {
            // Validate overlap before opening the journal/lock, which must never
            // coincide with a media file supplied by the caller.
            var preview = await Task.Run(() => ValidateRequest(target, keeper, destination, protectedFile,
                requireIdentical, true, ct), ct).ConfigureAwait(false);
			if (ValidateProtectionAsync is not null) await ValidateProtectionAsync(null, ct).ConfigureAwait(false);
            using var lease = AcquireHistoryLease();
            var records = await LoadAsync().ConfigureAwait(false);
            if (await RecoverPendingAsync(records).ConfigureAwait(false))
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
            if (records.Any(r => r.ProtectionRefreshClaim is not null &&
                (r.Target.StableFileId == target.StableFileId || r.Target.StableFileId == keeper.StableFileId)))
                throw new IOException("An earlier quarantine of this media requires its protection refresh before cleanup.");
            var record = CreateRecord(preview);
            records.Add(record);
            // No media mutation occurs before the prepared intent is durable.
            await SaveAsync(records, ct).ConfigureAwait(false);
            bool claimed = false;
            bool deleted = false;
            try {
                await Checkpoint(QuarantineCheckpoint.Prepared, ct).ConfigureAwait(false);
                await CopyVerifiedAsync(record.OriginalPath, record.StagingPath, record.Target, ct).ConfigureAwait(false);
                await Checkpoint(QuarantineCheckpoint.CopyFlushed, ct).ConfigureAwait(false);
                await ValidateLivePairAsync(record.Target, record.Keeper, ct).ConfigureAwait(false);
                SafePaths.RequireDirectory(record.DestinationDirectory);
                SafePaths.RequireRegularFile(record.StagingPath);
                SafePaths.RequireNoLinks(record.QuarantinePath, true);
                DurableFileOperations.Move(record.StagingPath, record.QuarantinePath);
                record.QuarantineIdentity = await FreshAsync(record.QuarantinePath, ct).ConfigureAwait(false);
                if (record.QuarantineIdentity is null || !record.Target.HasSameBytes(record.QuarantineIdentity))
                    throw new IOException("The committed quarantine copy could not be verified.");
                SetState(record, QuarantineState.CopyReady);
                await SaveAsync(records, ct).ConfigureAwait(false);
                await Checkpoint(QuarantineCheckpoint.CopyCommitted, ct).ConfigureAwait(false);

                await ValidateLivePairAsync(record.Target, record.Keeper, ct).ConfigureAwait(false);
                SafePaths.RequireRegularFile(record.OriginalPath);
                SafePaths.RequireNoLinks(record.SourceStagingPath, true);
                ct.ThrowIfCancellationRequested();
                // The prepared ledger knows this exact holding name even if a
                // crash happens immediately after the rename.
                File.Move(record.OriginalPath, record.SourceStagingPath, false);
                claimed = true;
                DurableFileOperations.FlushDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                var actualClaim = await FreshAsync(record.SourceStagingPath, ct).ConfigureAwait(false);
                if (!record.Target.MatchesRelocated(actualClaim))
                    throw new IOException("The claimed source is a replacement; it will not be deleted.");
                SetState(record, QuarantineState.SourceClaimed);
                await SaveAsync(records, ct).ConfigureAwait(false);
                await Checkpoint(QuarantineCheckpoint.SourceClaimed, ct).ConfigureAwait(false);
                await Checkpoint(QuarantineCheckpoint.BeforeSourceDelete, ct).ConfigureAwait(false);
                await RequireKeeperAsync(record.Keeper, ct).ConfigureAwait(false);
                var verifiedClaim = await FreshAsync(record.SourceStagingPath, ct).ConfigureAwait(false);
                if (!record.Target.MatchesRelocated(verifiedClaim))
                    throw new IOException("The source claim changed; deletion was cancelled.");
				if (ValidateProtectionAsync is not null) await ValidateProtectionAsync(record.SourceStagingPath, ct).ConfigureAwait(false);
                if (!IdentityReader.MatchesCurrentSnapshot(record.Keeper))
                    throw new IOException("The remaining keeper changed or lacks a reliable current filesystem snapshot.");
                if (!IdentityReader.MatchesCurrentSnapshot(verifiedClaim!))
                    throw new IOException("The source claim changed or lacks a reliable current filesystem snapshot.");
                SafePaths.RequireRegularFile(record.SourceStagingPath);
                ct.ThrowIfCancellationRequested();
                File.Delete(record.SourceStagingPath);
                deleted = true;
                DurableFileOperations.FlushDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                // Cancellation after this commit point must not report a rollback.
                SetState(record, QuarantineState.Quarantined);
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
                return Clone(record);
            }
            catch (Exception ex) {
                bool restoredClaim = !claimed || deleted || await TryRestoreClaimAsync(records, record, allowReplacement: true).ConfigureAwait(false);
                record.Error = ex.Message;
                SetState(record, deleted || !restoredClaim ? QuarantineState.NeedsAttention :
                    ex is OperationCanceledException ? QuarantineState.Cancelled : QuarantineState.Failed);
                record.Reason += deleted ? " Source removal completed; the verified quarantine copy was retained." :
                    restoredClaim ? " The original was retained or restored; any copy or partial staging file was retained for recovery." :
                    " Source restoration or its protection refresh requires recovery; all remaining files were retained.";
                try { await SaveAsync(records, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException) {
                    // The last durable prepared/claim record is enough for a later
                    // recovery. Do not delete data to compensate for journal failure.
                }
                throw;
            }
        }
        finally { CleanupOperationGate.Instance.Release(); }
    }

    public async Task RestoreAsync(string operationId, CancellationToken ct = default) {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("A valid operation ID is required.", nameof(operationId));
        await CleanupOperationGate.Instance.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var lease = AcquireHistoryLease();
            var records = await LoadAsync().ConfigureAwait(false);
            if (await RecoverPendingAsync(records).ConfigureAwait(false))
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
            var record = records.SingleOrDefault(r => r.OperationId == operationId)
                ?? throw new KeyNotFoundException("The quarantine operation was not found.");
            if (record.State == QuarantineState.Restored && record.RestoredIdentity?.MatchesCurrent() == true) return;
            SafePaths.RequireDirectory(Path.GetDirectoryName(record.OriginalPath)!);
            SafePaths.RequireNoLinks(record.OriginalPath, true);
            if (SafePaths.Exists(record.OriginalPath)) throw new IOException("The original path is occupied; restore never overwrites it.");
            var copy = await FreshAsync(record.QuarantinePath, ct).ConfigureAwait(false);
            if (copy is null || record.QuarantineIdentity is null || !record.QuarantineIdentity.Matches(copy))
                throw new IOException("The quarantine copy is unavailable or has been replaced.");
            if (SafePaths.Exists(record.RestoringPath))
                throw new IOException("An interrupted restore staging file exists; it was preserved.");
            SetState(record, QuarantineState.Restoring);
            await SaveAsync(records, ct).ConfigureAwait(false);
            bool committed = false;
            try {
                await CopyVerifiedAsync(record.QuarantinePath, record.RestoringPath, copy, ct).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(record.RestoringPath, record.Target.LastWriteUtc);
                await Checkpoint(QuarantineCheckpoint.RestoreCopied, ct).ConfigureAwait(false);
                if (!copy.Matches(await FreshAsync(record.QuarantinePath, ct).ConfigureAwait(false)))
                    throw new IOException("The quarantine copy changed during restore.");
                SafePaths.RequireDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                SafePaths.RequireNoLinks(record.OriginalPath, true);
                SafePaths.RequireRegularFile(record.RestoringPath);
                ct.ThrowIfCancellationRequested();
                // Set the local commit flag immediately after the rename so a
                // directory flush failure cannot be reported as a rollback.
                File.Move(record.RestoringPath, record.OriginalPath, false);
                committed = true;
                DurableFileOperations.FlushDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                record.RestoredIdentity = await FreshAsync(record.OriginalPath, CancellationToken.None).ConfigureAwait(false);
                if (record.RestoredIdentity is null || !record.Target.HasSameBytes(record.RestoredIdentity))
                    throw new IOException("The restored file could not be verified; the quarantine copy was retained.");
                SetState(record, QuarantineState.Restored);
                record.Error = string.Empty;
                record.Reason += " Restored to the original path; the quarantine recovery copy was retained.";
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
                await Checkpoint(QuarantineCheckpoint.RestoreCommitted, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) {
                record.Error = ex.Message;
                SetState(record, committed ? QuarantineState.NeedsAttention : QuarantineState.Quarantined);
                try { await SaveAsync(records, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { CleanupOperationGate.Instance.Release(); }
    }

    private QuarantinePreview ValidateRequest(FileIdentity target, FileIdentity keeper, string destination,
        bool protectedFile, bool requireIdentical, bool forceHash, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keeper);
        if (protectedFile) throw new InvalidOperationException("Protected files cannot be quarantined.");
        if (!target.IsWellFormed() || !keeper.IsWellFormed()) throw new IOException("A validated file identity is required.");
        string targetPath = SafePaths.Normalize(target.Path, true);
        string keeperPath = SafePaths.Normalize(keeper.Path, true);
        if (!Path.IsPathFullyQualified(destination)) throw new IOException("An absolute quarantine destination is required.");
        string directory = SafePaths.Normalize(destination, true);
        ValidateLocations(targetPath, keeperPath, directory);
        SafePaths.RequireNoLinks(directory, true);
        bool createDirectory = !SafePaths.Exists(directory);
        if (createDirectory) SafePaths.RequireDirectory(Path.GetDirectoryName(directory)!);
        else SafePaths.RequireDirectory(directory);
        var currentKeeper = IdentityReader.Capture(keeperPath, forceHash, ct);
        var currentTarget = IdentityReader.Capture(targetPath, forceHash, ct);
        if (!target.Matches(currentTarget)) throw new IOException("The source is unavailable, changed, or replaced.");
        if (!keeper.Matches(currentKeeper)) throw new IOException("The keeper is unavailable, changed, or replaced.");
        if (!IdentityReader.MatchesCurrentSnapshot(keeper) || !IdentityReader.MatchesCurrentSnapshot(target))
            throw new IOException("The reviewed media changed or lacks reliable current filesystem snapshots.");
        if (target.StableFileId == keeper.StableFileId)
            throw new IOException("The keeper must be an independent file, not the source or a hard link to it.");
        bool exact = target.HasSameBytes(keeper);
        if (requireIdentical && !exact) throw new InvalidOperationException("Automatic quarantine requires byte-identical SHA256-verified copies.");
        var sidecars = CollectSidecars(targetPath);
        // Create only the final leaf, after validating both media identities.
        // Never create missing ancestors that could conceal an offline mount.
        if (forceHash) {
            ct.ThrowIfCancellationRequested();
            if (createDirectory) {
                SafePaths.RequireDirectory(Path.GetDirectoryName(directory)!);
                SafePaths.RequireNoLinks(directory, true);
                Directory.CreateDirectory(directory);
            }
            SafePaths.RequireDirectory(directory);
            // A prior failed parent flush can leave an existing leaf whose entry
            // is still not durable. Retry every ancestor before removing media.
            for (string? parent = Path.GetDirectoryName(directory); parent is not null;
                parent = Path.GetDirectoryName(parent))
                DurableFileOperations.FlushDirectory(parent);
        }
        return new() {
            Target = target, Keeper = keeper, DestinationDirectory = directory, ByteIdentical = exact,
            DestinationWillBeCreated = createDirectory,
            SidecarsLeftInPlace = sidecars.AsReadOnly(),
            Reason = (exact ? "Verified byte-identical duplicate with an independent live keeper." :
                "Explicit manual quarantine of different bytes; perceptual matching alone does not authorize automatic cleanup.") +
                " Associated NFOs, subtitles, and artwork remain in place; shared sidecars are never moved." +
                (createDirectory ? " The destination is a new directory beneath its validated existing parent." : string.Empty)
        };
    }

    private void ValidateLocations(string target, string keeper, string directory) {
        if (SafePaths.Same(target, keeper) || SafePaths.Within(target, directory) || SafePaths.Within(keeper, directory) ||
            SafePaths.Within(directory, target) || SafePaths.Within(directory, keeper))
            throw new IOException("The quarantine destination must not overlap the source or keeper.");
        foreach (string journal in new[] { _historyPath, _historyPath + ".bak", _historyPath + ".lock" })
            if (SafePaths.Same(journal, target) || SafePaths.Same(journal, keeper))
                throw new IOException("The history location must not overlap a media file.");
    }

    private static QuarantineRecord CreateRecord(QuarantinePreview preview) {
        string id = Guid.NewGuid().ToString("N");
        string sourceDirectory = Path.GetDirectoryName(preview.Target.Path)!;
        return new() {
            OperationId = id, Target = preview.Target, Keeper = preview.Keeper, OriginalPath = preview.Target.Path,
            DestinationDirectory = preview.DestinationDirectory,
            QuarantinePath = Path.Combine(preview.DestinationDirectory, QuarantineFileName(id, preview.Target.Path)),
            StagingPath = Path.Combine(preview.DestinationDirectory, ".vdf-" + id + ".copying"),
            SourceStagingPath = Path.Combine(sourceDirectory, ".vdf-" + id + ".holding"),
            RestoringPath = Path.Combine(sourceDirectory, ".vdf-" + id + ".restoring"),
            State = QuarantineState.Prepared, ByteIdentical = preview.ByteIdentical,
            Reason = preview.Reason, SidecarsLeftInPlace = preview.SidecarsLeftInPlace.ToList()
        };
    }

    private static string QuarantineFileName(string operationId, string originalPath) {
        // Full original basenames can already reach the filesystem component limit.
        // History preserves that name; the stored copy uses a bounded unique name.
        string extension = Path.GetExtension(originalPath);
        return operationId + (extension.Length <= 16 ? extension : ".media");
    }

    private FileStream AcquireHistoryLease() {
        SafePaths.EnsureParentDirectory(_historyPath);
        string leasePath = _historyPath + ".lock";
        SafePaths.RequireNoLinks(leasePath, true);
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { SafePaths.RequireRegularFile(leasePath); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private async Task<List<QuarantineRecord>> LoadAsync() =>
        (await DurableJsonFile.ReadAsync(_historyPath, CleanupJsonContext.Default.QuarantineEnvelope,
            ValidateEnvelope).ConfigureAwait(false))?.Operations ?? new();

    private Task SaveAsync(List<QuarantineRecord> records, CancellationToken ct) =>
        DurableJsonFile.WriteAsync(_historyPath, new QuarantineEnvelope { Operations = records },
            CleanupJsonContext.Default.QuarantineEnvelope, ValidateEnvelope, ct);

    internal async Task<bool> HasPendingRecoveryAsync(FileIdentity identity, string? activeClaim, CancellationToken ct) {
        var journal = await DurableJsonFile.ReadPrimaryAsync(_historyPath,
            CleanupJsonContext.Default.QuarantineEnvelope, ValidateEnvelope, ct).ConfigureAwait(false);
        if (journal is null && SafePaths.Exists(_historyPath + ".bak"))
            throw new IOException("The current quarantine journal is missing; recovery is required before cleanup.");
        return journal?.Operations.Any(record =>
            (record.ProtectionRefreshClaim is not null || record.State is QuarantineState.Prepared or QuarantineState.CopyReady or
                QuarantineState.SourceClaimed or QuarantineState.NeedsAttention) &&
            record.Target.StableFileId == identity.StableFileId &&
            (activeClaim is null || !SafePaths.Same(activeClaim, record.SourceStagingPath))) == true;
    }

    private bool ValidateEnvelope(QuarantineEnvelope envelope) {
        if (envelope.Version is not (1 or QuarantineEnvelope.CurrentVersion))
            throw new NotSupportedException("The cleanup history schema is unsupported; it was preserved.");
        if (envelope.Operations is null) return false;
        if (envelope.Version == 1 && envelope.Operations.Any(record => record?.ProtectionRefreshClaim is not null)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try {
            return envelope.Operations.All(record => record is not null && ids.Add(record.OperationId) &&
                ValidateRecord(record));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    private bool ValidateRecord(QuarantineRecord record) {
        if (!Guid.TryParseExact(record.OperationId, "N", out _) || record.Target is null || record.Keeper is null ||
            !record.Target.IsWellFormed() || !record.Keeper.IsWellFormed() || !Enum.IsDefined(record.State) ||
            record.Reason is null || record.Error is null || record.SidecarsLeftInPlace is null ||
            record.CreatedUtc.Kind != DateTimeKind.Utc || record.UpdatedUtc.Kind != DateTimeKind.Utc) return false;
        string directory = SafePaths.Normalize(record.DestinationDirectory, true);
        ValidateLocations(record.Target.Path, record.Keeper.Path, directory);
        string sourceDirectory = Path.GetDirectoryName(record.Target.Path)!;
        return SafePaths.Same(record.OriginalPath, record.Target.Path) &&
            (SafePaths.Same(record.QuarantinePath, Path.Combine(directory, QuarantineFileName(record.OperationId, record.Target.Path))) ||
                // Existing histories used an operation prefix plus the full basename.
                SafePaths.Same(record.QuarantinePath, Path.Combine(directory, record.OperationId + "_" + Path.GetFileName(record.Target.Path)))) &&
            SafePaths.Same(record.StagingPath, Path.Combine(directory, ".vdf-" + record.OperationId + ".copying")) &&
            SafePaths.Same(record.SourceStagingPath, Path.Combine(sourceDirectory, ".vdf-" + record.OperationId + ".holding")) &&
            SafePaths.Same(record.RestoringPath, Path.Combine(sourceDirectory, ".vdf-" + record.OperationId + ".restoring")) &&
            (record.QuarantineIdentity is null || record.QuarantineIdentity.IsWellFormed() &&
                SafePaths.Same(record.QuarantineIdentity.Path, record.QuarantinePath) && record.Target.HasSameBytes(record.QuarantineIdentity)) &&
            (record.RestoredIdentity is null || record.RestoredIdentity.IsWellFormed() &&
                SafePaths.Same(record.RestoredIdentity.Path, record.OriginalPath) && record.Target.HasSameBytes(record.RestoredIdentity)) &&
            (record.ProtectionRefreshClaim is null || record.ProtectionRefreshClaim.IsWellFormed() &&
                SafePaths.Same(record.ProtectionRefreshClaim.Path, record.SourceStagingPath) &&
                record.Target.MatchesRelocated(record.ProtectionRefreshClaim));
    }

    private async Task<bool> RecoverPendingAsync(List<QuarantineRecord> records) {
        bool changed = false;
        foreach (var record in records.Where(r => r.ProtectionRefreshClaim is not null || r.State is QuarantineState.Prepared or QuarantineState.CopyReady or
            QuarantineState.SourceClaimed or QuarantineState.Restoring)) {
            changed = true;
            try {
                SafePaths.RequireDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                SafePaths.RequireNoLinks(record.OriginalPath, true);
                if (record.State == QuarantineState.Restoring) {
                    var restored = await FreshAsync(record.OriginalPath, CancellationToken.None).ConfigureAwait(false);
                    if (restored is not null && record.Target.HasSameBytes(restored)) {
                        record.RestoredIdentity = restored;
                        SetState(record, QuarantineState.Restored);
                        record.Reason += " Recovered a completed restore; the quarantine recovery copy was retained.";
                    }
                    else {
                        SetState(record, restored is null && !SafePaths.Exists(record.OriginalPath)
                            ? QuarantineState.Quarantined : QuarantineState.NeedsAttention);
                        record.Reason += " Interrupted restore was stopped; existing files and restore staging were retained.";
                    }
                    continue;
                }
                if (SafePaths.Exists(record.SourceStagingPath)) {
                    if (!await TryRestoreClaimAsync(records, record, false).ConfigureAwait(false)) {
                        SetState(record, QuarantineState.NeedsAttention);
                        record.Reason += " Interrupted source claim could not be safely restored; all copies were retained.";
                        continue;
                    }
                    SetState(record, QuarantineState.Recovered);
                    record.Reason += " Interrupted source claim restored to its original path; no deletion was resumed.";
                    continue;
                }
                var original = await FreshAsync(record.OriginalPath, CancellationToken.None).ConfigureAwait(false);
                if (record.ProtectionRefreshClaim is not null) {
                    if (RefreshRestoredProtectionAsync is null || !record.Target.MatchesRelocated(original) ||
                        !record.ProtectionRefreshClaim.MatchesRelocated(original))
                        throw new IOException("The restored source cannot complete its pending protection refresh.");
                    await RefreshRestoredProtectionAsync(record.Target, record.ProtectionRefreshClaim, original!, CancellationToken.None).ConfigureAwait(false);
                    record.ProtectionRefreshClaim = null;
                    SetState(record, QuarantineState.Recovered);
                    record.Reason += " Restored source protection refreshed; no deletion was resumed.";
                    continue;
                }
                var copy = await FreshAsync(record.QuarantinePath, CancellationToken.None).ConfigureAwait(false);
                bool verifiedCopy = copy is not null && record.Target.HasSameBytes(copy) &&
                    (record.QuarantineIdentity is null || record.QuarantineIdentity.Matches(copy));
                if (verifiedCopy) record.QuarantineIdentity = copy;
                if (original is not null && record.Target.Matches(original)) {
                    SetState(record, QuarantineState.Recovered);
                    record.Reason += " Interrupted quarantine stopped with the original intact; staging and recovery copies were retained.";
                }
                else if (!SafePaths.Exists(record.OriginalPath) && verifiedCopy) {
                    SetState(record, QuarantineState.Quarantined);
                    record.Reason += " Recovered a committed quarantine copy with the original path absent; no deletion was resumed.";
                }
                else {
                    SetState(record, QuarantineState.NeedsAttention);
                    record.Reason += " Interrupted quarantine has an unavailable or replaced original; all remaining files were retained.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException) {
                SetState(record, QuarantineState.NeedsAttention);
                record.Error = ex.Message;
                record.Reason += " Recovery requires an available, safe original directory; no file was deleted.";
            }
        }
        return changed;
    }

    private async Task<bool> TryRestoreClaimAsync(List<QuarantineRecord> records, QuarantineRecord record, bool allowReplacement) {
        try {
            SafePaths.RequireDirectory(Path.GetDirectoryName(record.OriginalPath)!);
            SafePaths.RequireNoLinks(record.OriginalPath, true);
            SafePaths.RequireRegularFile(record.SourceStagingPath);
            if (SafePaths.Exists(record.OriginalPath)) return false;
            var claim = await FreshAsync(record.SourceStagingPath, CancellationToken.None).ConfigureAwait(false);
            bool reviewedClaim = record.Target.MatchesRelocated(claim);
            if (claim is null || !allowReplacement && !reviewedClaim) return false;
            if (reviewedClaim && RefreshRestoredProtectionAsync is not null) {
                record.ProtectionRefreshClaim = claim;
                await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
                if (!IdentityReader.MatchesCurrentSnapshot(claim)) return false;
            }
            File.Move(record.SourceStagingPath, record.OriginalPath, false);
            DurableFileOperations.FlushDirectory(Path.GetDirectoryName(record.OriginalPath)!);
            var restored = await FreshAsync(record.OriginalPath, CancellationToken.None).ConfigureAwait(false);
            if (!claim.MatchesRelocated(restored)) return false;
            if (reviewedClaim && RefreshRestoredProtectionAsync is not null) {
                await RefreshRestoredProtectionAsync(record.Target, claim, restored!, CancellationToken.None).ConfigureAwait(false);
                record.ProtectionRefreshClaim = null;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException) { return false; }
    }

    private static async Task CopyVerifiedAsync(string source, string staging, FileIdentity identity, CancellationToken ct) {
        SafePaths.RequireRegularFile(source);
        SafePaths.RequireDirectory(Path.GetDirectoryName(staging)!);
        SafePaths.RequireNoLinks(staging, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long length = 0;
        try {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0) {
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                length += count;
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(true);
            if (length != identity.Length || Convert.ToHexString(hash.GetHashAndReset()) != identity.Sha256)
                throw new IOException("The staged copy does not match the reviewed source bytes.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task ValidateLivePairAsync(FileIdentity target, FileIdentity keeper, CancellationToken ct) {
        var targetTask = FreshAsync(target.Path, ct);
        var keeperTask = FreshAsync(keeper.Path, ct);
        await Task.WhenAll(targetTask, keeperTask).ConfigureAwait(false);
        if (!target.Matches(targetTask.Result)) throw new IOException("The original is unavailable, changed, or replaced.");
        if (!keeper.Matches(keeperTask.Result)) throw new IOException("The remaining keeper is unavailable, changed, or replaced.");
        if (targetTask.Result!.StableFileId == keeperTask.Result!.StableFileId)
            throw new IOException("An independent keeper is required.");
        if (!IdentityReader.MatchesCurrentSnapshot(keeper) || !IdentityReader.MatchesCurrentSnapshot(target))
            throw new IOException("The reviewed media changed while verifying the remaining copy.");
    }

    private static async Task RequireKeeperAsync(FileIdentity keeper, CancellationToken ct) {
        if (!keeper.Matches(await FreshAsync(keeper.Path, ct).ConfigureAwait(false)))
            throw new IOException("The remaining keeper is unavailable, changed, or replaced.");
    }

    private static Task<FileIdentity?> FreshAsync(string path, CancellationToken ct) =>
        Task.Run(() => IdentityReader.Capture(path, true, ct), ct);

    private async Task Checkpoint(QuarantineCheckpoint checkpoint, CancellationToken ct) {
        if (CheckpointAsync is not null) await CheckpointAsync(checkpoint, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static void SetState(QuarantineRecord record, QuarantineState state) {
        record.State = state;
        record.UpdatedUtc = DateTime.UtcNow;
    }

    private static List<string> CollectSidecars(string target) {
        string directory = Path.GetDirectoryName(target)!;
        SafePaths.RequireDirectory(directory);
        string stem = Path.GetFileNameWithoutExtension(target);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".jpg", ".jpeg", ".png", ".webp"
        };
        return Directory.EnumerateFiles(directory).Where(p => extensions.Contains(Path.GetExtension(p)) &&
            (Path.GetFileNameWithoutExtension(p).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileNameWithoutExtension(p), stem, StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(p).Equals(".nfo", StringComparison.OrdinalIgnoreCase) ||
             new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private static QuarantineRecord Clone(QuarantineRecord record) => new() {
        OperationId = record.OperationId, Target = record.Target, Keeper = record.Keeper,
        OriginalPath = record.OriginalPath, DestinationDirectory = record.DestinationDirectory,
        QuarantinePath = record.QuarantinePath, StagingPath = record.StagingPath,
        SourceStagingPath = record.SourceStagingPath, RestoringPath = record.RestoringPath,
        QuarantineIdentity = record.QuarantineIdentity, RestoredIdentity = record.RestoredIdentity,
        ProtectionRefreshClaim = record.ProtectionRefreshClaim,
        State = record.State, ByteIdentical = record.ByteIdentical, Reason = record.Reason,
        Error = record.Error, SidecarsLeftInPlace = record.SidecarsLeftInPlace.ToList(),
        CreatedUtc = record.CreatedUtc, UpdatedUtc = record.UpdatedUtc
    };
}
