using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Cleanup;

public sealed class QuarantineRestoreProofTests : IDisposable {
    const string Media = "synthetic restore-proof media";
    readonly string _root = Path.Combine(Path.GetTempPath(), "vdf-restore-proof-" + Guid.NewGuid().ToString("N"));
    string Source => Path.Combine(_root, "source", "media.mkv");
    string Keeper => Path.Combine(_root, "keeper", "media.mkv");
    string Destination => Path.Combine(_root, "quarantine");
    string History => Path.Combine(_root, "state", "cleanup.json");

    public QuarantineRestoreProofTests() {
        foreach (var directory in new[] { Path.GetDirectoryName(Source)!, Path.GetDirectoryName(Keeper)!,
            Destination, Path.GetDirectoryName(History)! }) Directory.CreateDirectory(directory);
    }

    async Task<QuarantineRecord> QuarantineAsync() {
        await File.WriteAllTextAsync(Source, Media);
        await File.WriteAllTextAsync(Keeper, Media);
        var target = Assert.IsType<FileIdentity>(FileIdentity.Capture(Source));
        var keeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(Keeper));
        return await new QuarantineService(History).QuarantineExactAsync(target, keeper, Destination);
    }

    static QuarantineEnvelope ReadEnvelope(byte[] bytes) => Assert.IsType<QuarantineEnvelope>(
        JsonSerializer.Deserialize(bytes, CleanupJsonContext.Default.QuarantineEnvelope));

    [Fact]
    public async Task RetainedCopyCanBeRestoredAgainWithFreshCommitProof() {
        var record = await QuarantineAsync();
        var service = new QuarantineService(History);
        await service.RestoreAsync(record.OperationId);
        var previous = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, previous.State);
        File.Delete(Source);

        await service.RestoreAsync(record.OperationId);
        var current = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, current.State);
        Assert.True(current.RestoreClaimIdentity!.MatchesRelocated(current.RestoredIdentity));
        Assert.True(current.RestoredIdentity!.MatchesCurrent());
        Assert.Equal(Media, await File.ReadAllTextAsync(Source));
        Assert.False(File.Exists(current.RestoringPath));
        Assert.Equal(Media, await File.ReadAllTextAsync(current.QuarantinePath));
        await service.RestoreAsync(record.OperationId);
        Assert.Equal(current.RestoredIdentity, Assert.Single(await service.HistoryAsync()).RestoredIdentity);
    }

    async Task WriteGenerationAsync(byte[] bytes) {
        // Replace both mirrors so a newer catch/final-save generation cannot hide
        // the durable intent from the simulated crash boundary.
        await File.WriteAllBytesAsync(History, bytes);
        await File.WriteAllBytesAsync(History + ".bak", bytes);
    }

    Task WriteRecordAsync(QuarantineRecord record, int version = QuarantineEnvelope.CurrentVersion) =>
        WriteGenerationAsync(JsonSerializer.SerializeToUtf8Bytes(new QuarantineEnvelope {
            Version = version, Operations = [record]
        }, CleanupJsonContext.Default.QuarantineEnvelope));

    async Task CopyWithRecordedTimeAsync(QuarantineRecord record, string destination) {
        await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(record.QuarantinePath));
        File.SetLastWriteTimeUtc(destination, record.Target.LastWriteUtc);
    }

    async Task<(QuarantineRecord Record, byte[] Generation)> InterruptPreparedAsync() {
        var record = await QuarantineAsync();
        byte[]? generation = null;
        var service = new QuarantineService(History) {
            CheckpointAsync = async (checkpoint, _) => {
                if (checkpoint != QuarantineCheckpoint.RestorePrepared) return;
                generation = await File.ReadAllBytesAsync(History);
                var durable = Assert.Single(ReadEnvelope(generation).Operations);
                Assert.Equal(QuarantineState.Restoring, durable.State);
                Assert.True(Assert.IsType<FileIdentity>(durable.RestoreClaimIdentity)
                    .Matches(FileIdentity.Capture(durable.RestoringPath)));
                throw new IOException("Synthetic interruption after durable restore preparation.");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(record.OperationId));
        var bytes = Assert.IsType<byte[]>(generation);
        return (Assert.Single(ReadEnvelope(bytes).Operations), bytes);
    }

    async Task AssertCopiesRetainedAsync(QuarantineRecord record, bool original, bool staging) {
        Assert.Equal(original, File.Exists(record.OriginalPath));
        Assert.Equal(staging, File.Exists(record.RestoringPath));
        Assert.Equal(Media, await File.ReadAllTextAsync(record.QuarantinePath));
        Assert.Equal(Media, await File.ReadAllTextAsync(Keeper));
        Assert.True(record.QuarantineIdentity!.Matches(FileIdentity.Capture(record.QuarantinePath)));
        Assert.True(record.Keeper.Matches(FileIdentity.Capture(Keeper)));
        if (original) Assert.Equal(Media, await File.ReadAllTextAsync(record.OriginalPath));
        if (staging) Assert.Equal(Media, await File.ReadAllTextAsync(record.RestoringPath));
    }

    [Fact]
    public async Task ActualPostRenameCheckpointGenerationRecoversCompletedRestoreAndAllowsIdempotentRetry() {
        var record = await QuarantineAsync();
        byte[]? generation = null;
        var service = new QuarantineService(History) {
            CheckpointAsync = async (checkpoint, _) => {
                if (checkpoint != QuarantineCheckpoint.RestoreRenamed) return;
                generation = await File.ReadAllBytesAsync(History);
                var durable = Assert.Single(ReadEnvelope(generation).Operations);
                Assert.Equal(QuarantineState.Restoring, durable.State);
                Assert.Null(durable.RestoredIdentity);
                Assert.False(File.Exists(durable.RestoringPath));
                Assert.True(durable.RestoreClaimIdentity!.MatchesRelocated(FileIdentity.Capture(Source)));
                throw new IOException("Synthetic crash after rename before final state save.");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(record.OperationId));
        await WriteGenerationAsync(Assert.IsType<byte[]>(generation));
        var reloaded = new QuarantineService(History);
        var recovered = Assert.Single(await reloaded.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, recovered.State);
        Assert.True(recovered.RestoreClaimIdentity!.MatchesRelocated(recovered.RestoredIdentity));
        await reloaded.RestoreAsync(record.OperationId);
        await AssertCopiesRetainedAsync(recovered, original: true, staging: false);
    }

    [Fact]
    public async Task PreparedCheckpointFailureRetainsProofAndStagingAndRepeatedRestoreRefuses() {
        var (record, _) = await InterruptPreparedAsync();
        var reloaded = new QuarantineService(History);
        var failed = Assert.Single(await reloaded.HistoryAsync());
        Assert.Equal(QuarantineState.Quarantined, failed.State);
        Assert.Equal(record.RestoreClaimIdentity, failed.RestoreClaimIdentity);
        for (int attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        await AssertCopiesRetainedAsync(failed, original: false, staging: true);
    }

    [Fact]
    public async Task ForeignDestinationAppearingAfterDurablePreparationRefusesRenameAndReloadCannotDeclareSuccess() {
        var record = await QuarantineAsync();
        byte[]? generation = null;
        var service = new QuarantineService(History) {
            CheckpointAsync = async (checkpoint, _) => {
                if (checkpoint != QuarantineCheckpoint.RestorePrepared) return;
                generation = await File.ReadAllBytesAsync(History);
                await CopyWithRecordedTimeAsync(record, Source);
            }
        };
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(record.OperationId));
        var foreign = Assert.IsType<FileIdentity>(FileIdentity.Capture(Source));
        var prepared = Assert.Single(ReadEnvelope(Assert.IsType<byte[]>(generation)).Operations);
        Assert.NotEqual(prepared.RestoreClaimIdentity!.StableFileId, foreign.StableFileId);
        await WriteGenerationAsync(generation!);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        for (int attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        Assert.True(foreign.Matches(FileIdentity.Capture(Source)));
        await AssertCopiesRetainedAsync(recovered, original: true, staging: true);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task ByteIdenticalForeignDestinationNeverProvesAnInterruptedRestore(int version, bool includeProof) {
        var (record, _) = await InterruptPreparedAsync();
        string retainedStage = record.RestoringPath + ".retained";
        File.Move(record.RestoringPath, retainedStage);
        await CopyWithRecordedTimeAsync(record, Source);
        var foreign = Assert.IsType<FileIdentity>(FileIdentity.Capture(Source));
        Assert.True(record.Target.HasSameBytes(foreign));
        Assert.Equal(record.Target.LastWriteUtc, foreign.LastWriteUtc);
        Assert.NotEqual(record.RestoreClaimIdentity!.StableFileId, foreign.StableFileId);
        if (!includeProof) record.RestoreClaimIdentity = null;
        await WriteRecordAsync(record, version);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        Assert.Null(recovered.RestoredIdentity);
        for (int attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        Assert.True(foreign.Matches(FileIdentity.Capture(Source)));
        Assert.Equal(Media, await File.ReadAllTextAsync(retainedStage));
        await AssertCopiesRetainedAsync(recovered, original: true, staging: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurvivingStagePreventsCompletedRestoreEvenWhenDestinationMatchesProof(bool destinationMatchesProof) {
        var (record, generation) = await InterruptPreparedAsync();
        if (destinationMatchesProof) {
            File.Move(record.RestoringPath, Source);
            await CopyWithRecordedTimeAsync(record, record.RestoringPath);
            Assert.True(record.RestoreClaimIdentity!.MatchesRelocated(FileIdentity.Capture(Source)));
        }
        else await CopyWithRecordedTimeAsync(record, Source);
        await WriteGenerationAsync(generation);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        Assert.Null(recovered.RestoredIdentity);
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        await AssertCopiesRetainedAsync(recovered, original: true, staging: true);
    }

    [Fact]
    public async Task ByteIdenticalReplacementStageCannotSupplyTheRecordedRenameProof() {
        var (record, generation) = await InterruptPreparedAsync();
        string retainedStage = record.RestoringPath + ".retained";
        File.Move(record.RestoringPath, retainedStage);
        await CopyWithRecordedTimeAsync(record, record.RestoringPath);
        var replacement = Assert.IsType<FileIdentity>(FileIdentity.Capture(record.RestoringPath));
        Assert.True(record.RestoreClaimIdentity!.HasSameBytes(replacement));
        Assert.Equal(record.RestoreClaimIdentity.LastWriteUtc, replacement.LastWriteUtc);
        Assert.NotEqual(record.RestoreClaimIdentity.StableFileId, replacement.StableFileId);
        File.Move(record.RestoringPath, Source);
        await WriteGenerationAsync(generation);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        Assert.Equal(Media, await File.ReadAllTextAsync(retainedStage));
        await AssertCopiesRetainedAsync(recovered, original: true, staging: false);
    }

    [Fact]
    public async Task DurablePreparationWithOnlyStagingRemainingIsRetainedForManualRecovery() {
        var (record, generation) = await InterruptPreparedAsync();
        await WriteGenerationAsync(generation);
        var service = new QuarantineService(History);
        Assert.True(await service.HasPendingRecoveryAsync(record.Target, null, CancellationToken.None));
        var recovered = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        Assert.True(await service.HasPendingRecoveryAsync(record.Target, null, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        await AssertCopiesRetainedAsync(recovered, original: false, staging: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingOriginalAndStageRecoversQuarantineAndSafelyMigratesOlderHistory(int version) {
        var record = await QuarantineAsync();
        record.State = QuarantineState.Restoring;
        await WriteRecordAsync(record, version);
        var service = new QuarantineService(History);
        var recovered = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Quarantined, recovered.State);
        Assert.Null(recovered.RestoreClaimIdentity);
        Assert.Equal(QuarantineEnvelope.CurrentVersion, ReadEnvelope(await File.ReadAllBytesAsync(History)).Version);
        await AssertCopiesRetainedAsync(recovered, original: false, staging: false);
        await new QuarantineService(History).RestoreAsync(record.OperationId);
        Assert.Equal(QuarantineState.Restored, Assert.Single(await service.HistoryAsync()).State);
        await AssertCopiesRetainedAsync(recovered, original: true, staging: false);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderHistoryWithNewRestoreProofIsRejectedWithoutMutation(int version) {
        var (record, _) = await InterruptPreparedAsync();
        await WriteRecordAsync(record, version);
        var invalid = await File.ReadAllBytesAsync(History);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuarantineService(History).HistoryAsync());
        Assert.Equal(invalid, await File.ReadAllBytesAsync(History));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(History + ".bak"));
        await AssertCopiesRetainedAsync(record, original: false, staging: true);
    }

    [Theory]
    [InlineData("EmptyNativeId")]
    [InlineData("MalformedHash")]
    [InlineData("DifferentHash")]
    [InlineData("WrongPath")]
    public async Task MalformedRestoreProofIsRejectedBeforeRecoveryAndBothJournalMirrorsRemain(string defect) {
        var (record, _) = await InterruptPreparedAsync();
        var proof = Assert.IsType<FileIdentity>(record.RestoreClaimIdentity);
        record.RestoreClaimIdentity = defect switch {
            "EmptyNativeId" => proof with { StableFileId = string.Empty },
            "MalformedHash" => proof with { Sha256 = "invalid hash" },
            "DifferentHash" => proof with { Sha256 = (proof.Sha256[0] == '0' ? "1" : "0") + proof.Sha256[1..] },
            "WrongPath" => proof with { Path = Keeper },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        await WriteRecordAsync(record);
        var invalid = await File.ReadAllBytesAsync(History);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuarantineService(History).HistoryAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuarantineService(History).RestoreAsync(record.OperationId));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(History));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(History + ".bak"));
        await AssertCopiesRetainedAsync(record, original: false, staging: true);
    }

    [Fact]
    public async Task CompletedRestoreRoundTripsExactClaimProofAndRelocatedIdentityInVersionThree() {
        var record = await QuarantineAsync();
        await new QuarantineService(History).RestoreAsync(record.OperationId);
        var generation = ReadEnvelope(await File.ReadAllBytesAsync(History));
        Assert.Equal(3, generation.Version);
        var persisted = Assert.Single(generation.Operations);
        var claim = Assert.IsType<FileIdentity>(persisted.RestoreClaimIdentity);
        var restored = Assert.IsType<FileIdentity>(persisted.RestoredIdentity);
        Assert.Equal(record.RestoringPath, claim.Path);
        Assert.Equal(Source, restored.Path);
        Assert.True(claim.MatchesRelocated(restored));
        var reloaded = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(claim, reloaded.RestoreClaimIdentity);
        Assert.Equal(restored, reloaded.RestoredIdentity);
        await AssertCopiesRetainedAsync(reloaded, original: true, staging: false);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    [Fact]
    public async Task PendingRestoreBlocksItsStagingFileAndNativeAliases() {
        var (record, generation) = await InterruptPreparedAsync();
        await WriteGenerationAsync(generation);
        string alias = Path.Combine(Path.GetDirectoryName(Source)!, "pending-restore-alias.mkv");
        HardLinkUtils.CreateHardLink(alias, record.RestoringPath);
        var stage = FileIdentity.Capture(record.RestoringPath)!;
        var aliased = FileIdentity.Capture(alias)!;
        Assert.NotEqual(record.Target.StableFileId, stage.StableFileId);
        Assert.Equal(record.RestoreClaimIdentity!.StableFileId, stage.StableFileId);
        var service = new QuarantineService(History);
        Assert.True(await service.HasPendingRecoveryAsync(stage, null, CancellationToken.None));
        Assert.True(await service.HasPendingRecoveryAsync(aliased, null, CancellationToken.None));
        Assert.Equal(QuarantineState.NeedsAttention, Assert.Single(await service.HistoryAsync()).State);
        Assert.True(await service.HasPendingRecoveryAsync(aliased, null, CancellationToken.None));
        Assert.Equal(Media, await File.ReadAllTextAsync(alias));
        Assert.False(File.Exists(Source));
    }

}
