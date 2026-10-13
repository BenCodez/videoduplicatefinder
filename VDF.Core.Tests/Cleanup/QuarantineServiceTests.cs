using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Cleanup;

public sealed class QuarantineServiceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vdf-quarantine-" + Guid.NewGuid().ToString("N"));
    private string SourceDirectory => Path.Combine(_root, "source");
    private string KeeperDirectory => Path.Combine(_root, "keeper");
    private string Destination => Path.Combine(_root, "quarantine");
    private string History => Path.Combine(_root, "state", "cleanup.json");

    public QuarantineServiceTests() {
        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(KeeperDirectory);
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(History)!);
    }

    private async Task<(FileIdentity Target, FileIdentity Keeper)> PairAsync(bool exact = true) {
        string target = Path.Combine(SourceDirectory, "media.mkv");
        string keeper = Path.Combine(KeeperDirectory, "media.mkv");
        await File.WriteAllTextAsync(target, "original media bytes");
        await File.WriteAllTextAsync(keeper, exact ? "original media bytes" : "different media bytes");
        return (Assert.IsType<FileIdentity>(FileIdentity.Capture(target)),
            Assert.IsType<FileIdentity>(FileIdentity.Capture(keeper)));
    }

    [Fact]
    public async Task ValidLongAsciiAndUnicodeNamesQuarantineAndRestoreWithoutExceedingComponentLimits() {
        foreach (string name in new[] { new string('x', 223) + ".mkv", new string('界', 76) + ".mp4", "media." + new string('x', 223) }) {
            string source = Path.Combine(SourceDirectory, name);
            string keeper = Path.Combine(KeeperDirectory, "short.mkv");
            await File.WriteAllTextAsync(source, "long named media bytes");
            await File.WriteAllTextAsync(keeper, "long named media bytes");
            var targetIdentity = FileIdentity.Capture(source)!;
            var keeperIdentity = FileIdentity.Capture(keeper)!;
            var service = new QuarantineService(History);
            await service.PreviewAsync(targetIdentity, keeperIdentity, Destination);
            var record = await service.QuarantineExactAsync(targetIdentity, keeperIdentity, Destination);
            Assert.Equal(QuarantineState.Quarantined, record.State);
            Assert.Equal(source, record.OriginalPath);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(record.QuarantinePath)) <= 255);
            Assert.Equal("long named media bytes", await File.ReadAllTextAsync(record.QuarantinePath));
            Assert.False(File.Exists(source));
            await service.RestoreAsync(record.OperationId);
            Assert.Equal("long named media bytes", await File.ReadAllTextAsync(source));
            Assert.True(File.Exists(keeper));
        }
    }

    [Fact]
    public async Task ExistingFullBasenameHistoryRemainsRestorableAfterBoundedNameChange() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var record = await service.QuarantineExactAsync(pair.Target, pair.Keeper, Destination);
        string legacyPath = Path.Combine(Destination, record.OperationId + "_" + Path.GetFileName(pair.Target.Path));
        File.Move(record.QuarantinePath, legacyPath);
        record.QuarantinePath = legacyPath;
        record.QuarantineIdentity = FileIdentity.Capture(legacyPath);
        await DurableJsonFile.WriteAsync(History, new QuarantineEnvelope { Operations = [record] },
            CleanupJsonContext.Default.QuarantineEnvelope, envelope => envelope.Version == 1);
        await new QuarantineService(History).RestoreAsync(record.OperationId);
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(File.Exists(legacyPath));
        Assert.Equal(QuarantineState.Restored, Assert.Single(await service.HistoryAsync()).State);
    }

    [Fact]
    public async Task FailedDestinationParentFlushCannotBeBypassedByAnExistingLeafOnRetry() {
        if (OperatingSystem.IsWindows()) return; // Unix directory fsync failure path.
        var pair = await PairAsync(exact: false);
        string destinationParent = Directory.CreateTempSubdirectory("vdf-quarantine-durability-").FullName;
        string destination = Path.Combine(destinationParent, "new-leaf");
        int parentFlushes = 0;
        DurableFileOperations.DirectoryFlushedObserver.Value = path => {
            if (!SafePaths.Same(path, destinationParent)) return;
            if (++parentFlushes <= 2) throw new IOException("Injected destination-parent durability failure.");
        };
        try {
            var service = new QuarantineService(History); // Journal has a separate parent.
            await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, destination));
            Assert.True(Directory.Exists(destination));
            Assert.True(pair.Target.MatchesCurrent(true));
            await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, destination));
            Assert.Equal(2, parentFlushes);
            Assert.True(pair.Target.MatchesCurrent(true));
            Assert.Empty(Directory.GetFiles(destination));
            var record = await service.QuarantineAsync(pair.Target, pair.Keeper, destination);
            Assert.True(parentFlushes >= 3);
            Assert.False(File.Exists(pair.Target.Path));
            Assert.Equal("original media bytes", await File.ReadAllTextAsync(record.QuarantinePath));
            Assert.Equal("different media bytes", await File.ReadAllTextAsync(pair.Keeper.Path));
        }
        finally {
            DurableFileOperations.DirectoryFlushedObserver.Value = null;
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public async Task Preview_IsReadOnlyAndExplainsByteIdentityAndRetainedSidecars() {
        var pair = await PairAsync();
        string nfo = Path.Combine(SourceDirectory, "media.nfo");
        string shared = Path.Combine(SourceDirectory, "movie.nfo");
        string subs = Path.Combine(SourceDirectory, "media.en.srt");
        string artwork = Path.Combine(SourceDirectory, "poster.jpg");
        foreach (string path in new[] { nfo, shared, subs, artwork }) await File.WriteAllTextAsync(path, "sidecar");
        var preview = await new QuarantineService(History).PreviewAsync(pair.Target, pair.Keeper, Destination);
        Assert.True(preview.ByteIdentical);
        Assert.Equal(4, preview.SidecarsLeftInPlace.Count);
        Assert.Contains("remain in place", preview.Reason);
        Assert.True(File.Exists(pair.Target.Path));
        Assert.Empty(Directory.GetFiles(Destination));
        Assert.False(File.Exists(History));
    }

    [Fact]
    public async Task QuarantineAndRestore_VerifiesBytesPreservesSidecarsAndRetainsRecoveryCopy() {
        var pair = await PairAsync();
        string sidecar = Path.Combine(SourceDirectory, "media.nfo");
        await File.WriteAllTextAsync(sidecar, "protected metadata");
        var service = new QuarantineService(History);
        var record = await service.QuarantineExactAsync(pair.Target, pair.Keeper, Destination);
        Assert.Equal(QuarantineState.Quarantined, record.State);
        Assert.False(File.Exists(pair.Target.Path));
        Assert.True(pair.Keeper.MatchesCurrent(true));
        Assert.Equal(pair.Target.Sha256, FileIdentity.Capture(record.QuarantinePath)!.Sha256);
        Assert.Equal("protected metadata", await File.ReadAllTextAsync(sidecar));
        Assert.Contains(sidecar, record.SidecarsLeftInPlace);
        Assert.Equal(QuarantineState.Quarantined, Assert.Single(await new QuarantineService(History).HistoryAsync()).State);

        await service.RestoreAsync(record.OperationId);
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(File.Exists(record.QuarantinePath));
        var restored = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, restored.State);
        Assert.Contains("recovery copy was retained", restored.Reason);
        await service.RestoreAsync(record.OperationId); // Idempotent only for the validated restored file.
    }

    [Fact]
    public async Task DifferentBytes_RequireManualEntryPoint() {
        var pair = await PairAsync(exact: false);
        var service = new QuarantineService(History);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QuarantineExactAsync(pair.Target, pair.Keeper, Destination));
        Assert.True(File.Exists(pair.Target.Path));
        var record = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        Assert.False(record.ByteIdentical);
        Assert.Contains("Explicit manual", record.Reason);
        Assert.True(File.Exists(pair.Keeper.Path));
    }

    [Fact]
    public async Task ProtectedFile_IsRejectedAtBothQuarantineEntryPoints() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Destination, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QuarantineExactAsync(pair.Target, pair.Keeper, Destination, true));
        Assert.True(pair.Target.MatchesCurrent(true));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task OfflineKeeper_PreventsQuarantine() {
        var pair = await PairAsync();
        File.Delete(pair.Keeper.Path);
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).QuarantineAsync(pair.Target, pair.Keeper, Destination));
        Assert.True(File.Exists(pair.Target.Path));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task HardLinkedKeeper_IsNotAnIndependentRemainingCopy() {
        if (OperatingSystem.IsWindows()) return; // Windows hard-link API exercised by existing HardLinkUtils tests.
        var pair = await PairAsync();
        File.Delete(pair.Keeper.Path);
        HardLinkUtils.CreateHardLink(pair.Keeper.Path, pair.Target.Path);
        var linkedKeeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(pair.Keeper.Path));
        var currentTarget = Assert.IsType<FileIdentity>(FileIdentity.Capture(pair.Target.Path));
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).QuarantineAsync(currentTarget, linkedKeeper, Destination));
        Assert.True(File.Exists(pair.Target.Path));
    }

    [Fact]
    public async Task ReplacementWithOldDates_DoesNotRemoveNewFile() {
        var pair = await PairAsync();
        string replacement = Path.Combine(SourceDirectory, "replacement.mkv");
        await File.WriteAllTextAsync(replacement, "original media bytes");
        File.SetLastWriteTimeUtc(replacement, pair.Target.LastWriteUtc);
        File.SetCreationTimeUtc(replacement, pair.Target.CreationUtc);
        File.Move(replacement, pair.Target.Path, true);
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(History).QuarantineAsync(pair.Target, pair.Keeper, Destination));
        Assert.True(File.Exists(pair.Target.Path));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task Restore_DoesNotOverwriteAnOccupiedOriginalPath() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        await File.WriteAllTextAsync(pair.Target.Path, "new unrelated file");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(operation.OperationId));
        Assert.Equal("new unrelated file", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Fact]
    public async Task Restore_RejectsReplacedQuarantineFileEvenWithIdenticalBytes() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        string replacement = Path.Combine(Destination, "replacement.mkv");
        await File.WriteAllTextAsync(replacement, "original media bytes");
        File.Move(replacement, operation.QuarantinePath, true);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(operation.OperationId));
        Assert.False(File.Exists(pair.Target.Path));
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Theory]
    [InlineData((int)QuarantineCheckpoint.Prepared)]
    [InlineData((int)QuarantineCheckpoint.CopyFlushed)]
    [InlineData((int)QuarantineCheckpoint.CopyCommitted)]
    [InlineData((int)QuarantineCheckpoint.SourceClaimed)]
    [InlineData((int)QuarantineCheckpoint.BeforeSourceDelete)]
    public async Task CancellationBeforeCommit_LeavesOriginalAndKeeper(int checkpointValue) {
        var checkpoint = (QuarantineCheckpoint)checkpointValue;
        var pair = await PairAsync();
        using var cancellation = new CancellationTokenSource();
        var service = new QuarantineService(History) {
            CheckpointAsync = (current, _) => { if (current == checkpoint) cancellation.Cancel(); return Task.CompletedTask; }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.QuarantineAsync(pair.Target, pair.Keeper, Destination, ct: cancellation.Token));
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(pair.Keeper.MatchesCurrent(true));
        var history = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Cancelled, history.State);
        Assert.False(File.Exists(history.SourceStagingPath));
    }

    [Fact]
    public async Task KeeperDisappearingAfterClaim_RestoresOriginalAndRetainsCopy() {
        var pair = await PairAsync();
        var service = new QuarantineService(History) {
            CheckpointAsync = (stage, _) => {
                if (stage == QuarantineCheckpoint.BeforeSourceDelete) File.Delete(pair.Keeper.Path);
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Destination));
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        var operation = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Failed, operation.State);
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Fact]
    public async Task HistoryRecovery_RestoresInterruptedSourceClaimWithoutResumingDeletion() {
        var pair = await PairAsync();
        var operation = MakeRecord(pair, QuarantineState.SourceClaimed);
        File.Copy(pair.Target.Path, operation.QuarantinePath);
        operation.QuarantineIdentity = FileIdentity.Capture(operation.QuarantinePath);
        File.Move(pair.Target.Path, operation.SourceStagingPath);
        await WriteJournalAsync(operation);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.Recovered, recovered.State);
        Assert.True(File.Exists(pair.Target.Path));
        Assert.False(File.Exists(operation.SourceStagingPath));
        Assert.True(File.Exists(operation.QuarantinePath));
        Assert.Contains("no deletion was resumed", recovered.Reason);
    }

    [Fact]
    public async Task HistoryRecovery_PreservesPartialCopyAndOriginal() {
        var pair = await PairAsync();
        var operation = MakeRecord(pair, QuarantineState.Prepared);
        await File.WriteAllTextAsync(operation.StagingPath, "partial");
        await WriteJournalAsync(operation);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.Recovered, recovered.State);
        Assert.True(pair.Target.MatchesCurrent(true));
        Assert.Equal("partial", await File.ReadAllTextAsync(operation.StagingPath));
    }

    [Fact]
    public async Task HistoryRecovery_RecognizesCompletedRemovalFromCommittedCopy() {
        var pair = await PairAsync();
        var operation = MakeRecord(pair, QuarantineState.CopyReady);
        File.Copy(pair.Target.Path, operation.QuarantinePath);
        operation.QuarantineIdentity = FileIdentity.Capture(operation.QuarantinePath);
        File.Delete(pair.Target.Path);
        await WriteJournalAsync(operation);
        Assert.Equal(QuarantineState.Quarantined, Assert.Single(await new QuarantineService(History).HistoryAsync()).State);
    }

    [Fact]
    public async Task HistoryRecovery_DoesNotOverwriteReplacementAtOriginalPath() {
        var pair = await PairAsync();
        var operation = MakeRecord(pair, QuarantineState.SourceClaimed);
        File.Move(pair.Target.Path, operation.SourceStagingPath);
        await File.WriteAllTextAsync(pair.Target.Path, "replacement bytes");
        await WriteJournalAsync(operation);
        var recovered = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(QuarantineState.NeedsAttention, recovered.State);
        Assert.Equal("replacement bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(File.Exists(operation.SourceStagingPath));
    }

    [Fact]
    public async Task HistoryRecovery_RecognizesInterruptedCompletedRestore() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        File.Copy(operation.QuarantinePath, pair.Target.Path);
        operation.State = QuarantineState.Restoring;
        await WriteJournalAsync(operation);
        var recovered = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, recovered.State);
        Assert.True(File.Exists(pair.Target.Path));
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Fact]
    public async Task MissingDestinationAndTraversal_AreRejectedBeforeMutation() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Path.Combine(_root, "missing-parent", "missing")));
        await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Destination + "/../quarantine"));
        await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, SourceDirectory));
        Assert.True(pair.Target.MatchesCurrent(true));
    }

    [Fact]
    public async Task SymbolicDestination_IsRejectedBeforeMutation() {
        if (OperatingSystem.IsWindows()) return;
        var pair = await PairAsync();
        string linked = Path.Combine(_root, "linked-destination");
        Directory.CreateSymbolicLink(linked, Destination);
        await Assert.ThrowsAnyAsync<IOException>(() => new QuarantineService(History).QuarantineAsync(pair.Target, pair.Keeper, linked));
        Assert.True(File.Exists(pair.Target.Path));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task NewDestinationLeaf_IsPreviewedWithoutMutationAndSafelyCreatedOnExecution() {
        var pair = await PairAsync();
        string destination = Path.Combine(_root, "new-quarantine");
        var service = new QuarantineService(History);
        var preview = await service.PreviewAsync(pair.Target, pair.Keeper, destination);
        Assert.True(preview.DestinationWillBeCreated);
        Assert.False(Directory.Exists(destination));
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, destination);
        Assert.Equal(QuarantineState.Quarantined, operation.State);
        Assert.True(File.Exists(operation.QuarantinePath));
        Assert.False(File.Exists(pair.Target.Path));
    }

    [Fact]
    public async Task CrossFilesystemQuarantine_UsesVerifiedCopyAndRestoresOriginal() {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) return;
        string destination = Path.Combine("/dev/shm", "vdf-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try {
            var pair = await PairAsync();
            var service = new QuarantineService(History);
            var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, destination);
            Assert.NotEqual(string.Join(':', pair.Target.StableFileId.Split(':').Take(3)),
                string.Join(':', operation.QuarantineIdentity!.StableFileId.Split(':').Take(3)));
            Assert.Equal(pair.Target.Sha256, operation.QuarantineIdentity.Sha256);
            Assert.False(File.Exists(pair.Target.Path));
            await service.RestoreAsync(operation.OperationId);
            Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        }
        finally { Directory.Delete(destination, true); }
    }

    [Fact]
    public async Task HistoryOverlappingSource_IsRejectedBeforeWritingJournal() {
        var pair = await PairAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => new QuarantineService(pair.Target.Path).QuarantineAsync(pair.Target, pair.Keeper, Destination));
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
    }

    [Fact]
    public async Task MaliciousJournalClaimPath_IsPreservedWithoutMovingExternalFile() {
        var pair = await PairAsync();
        var operation = MakeRecord(pair, QuarantineState.SourceClaimed);
        string unrelated = Path.Combine(_root, "unrelated.bin");
        await File.WriteAllTextAsync(unrelated, "unrelated bytes");
        operation.SourceStagingPath = unrelated;
        await WriteJournalAsync(operation);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuarantineService(History).HistoryAsync());
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(unrelated));
        Assert.True(File.Exists(pair.Target.Path));
    }

    [Fact]
    public async Task CorruptPrimaryHistory_RecoversFromBackup() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        await File.WriteAllTextAsync(History, "{ broken");
        var loaded = Assert.Single(await new QuarantineService(History).HistoryAsync());
        Assert.Equal(operation.OperationId, loaded.OperationId);
        Assert.Equal(QuarantineState.Quarantined, loaded.State);
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Fact]
    public async Task CorruptPrimaryAfterSecondPreparedClaim_CurrentMirrorRestoresInterruptedSource() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var earlier = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        string secondPath = Path.Combine(SourceDirectory, "second.mkv");
        await File.WriteAllTextAsync(secondPath, "original media bytes");
        var second = Assert.IsType<FileIdentity>(FileIdentity.Capture(secondPath));
        var prepared = MakeRecord((second, pair.Keeper), QuarantineState.Prepared);
        await DurableJsonFile.WriteAsync(History, new QuarantineEnvelope { Operations = new() { earlier, prepared } },
            CleanupJsonContext.Default.QuarantineEnvelope, envelope => envelope.Version == 1);
        File.Copy(second.Path, prepared.QuarantinePath);
        File.Move(second.Path, prepared.SourceStagingPath); // Stops before any later state save.
        await File.WriteAllTextAsync(History, "{ corrupt current primary");
        var recovered = await service.HistoryAsync();
        Assert.Equal(2, recovered.Count);
        Assert.Equal(QuarantineState.Recovered, recovered.Single(r => r.OperationId == prepared.OperationId).State);
        Assert.True(second.MatchesRelocated(FileIdentity.Capture(second.Path)));
        Assert.False(File.Exists(prepared.SourceStagingPath));
        Assert.True(File.Exists(prepared.QuarantinePath));
        Assert.True(File.Exists(earlier.QuarantinePath));
    }

    [Fact]
    public async Task CorruptPrimaryAfterRestoreCommit_CurrentMirrorRetainsRestoreIntent() {
        var pair = await PairAsync();
        var service = new QuarantineService(History);
        var operation = await service.QuarantineAsync(pair.Target, pair.Keeper, Destination);
        operation.State = QuarantineState.Restoring;
        await DurableJsonFile.WriteAsync(History, new QuarantineEnvelope { Operations = new() { operation } },
            CleanupJsonContext.Default.QuarantineEnvelope, envelope => envelope.Version == 1);
        File.Copy(operation.QuarantinePath, pair.Target.Path); // Restore committed before the final state save.
        await File.WriteAllTextAsync(History, "{ corrupt current primary");
        var recovered = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Restored, recovered.State);
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.True(File.Exists(operation.QuarantinePath));
    }

    [Fact]
    public async Task SourceWriterDuringFinalKeeperHash_SourceIsRevalidatedLastAndRestored() {
        if (!OperatingSystem.IsLinux()) return;
        var pair = await PairAsync();
        using var writer = new FileStream(pair.Target.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        bool changed = false;
        bool armed = false;
        var service = new QuarantineService(History) {
            CheckpointAsync = (stage, _) => { if (stage == QuarantineCheckpoint.BeforeSourceDelete) armed = true; return Task.CompletedTask; }
        };
        IdentityReader.HashStartedObserver.Value = path => {
            if (!armed || changed || !SafePaths.Same(path, pair.Keeper.Path)) return;
            changed = true;
            writer.SetLength(0);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("source changed through original handle"));
            writer.Flush(true);
        };
        try {
            await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Destination));
        }
        finally { IdentityReader.HashStartedObserver.Value = null; }
        Assert.True(changed);
        Assert.Equal("source changed through original handle", await File.ReadAllTextAsync(pair.Target.Path));
        var failed = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Failed, failed.State);
        Assert.False(File.Exists(failed.SourceStagingPath));
        Assert.True(File.Exists(failed.QuarantinePath));
    }

    [Fact]
    public async Task KeeperWriterDuringFinalSourceHash_NativeSnapshotRecheckCancelsRemoval() {
        if (!OperatingSystem.IsLinux()) return;
        var pair = await PairAsync();
        using var writer = new FileStream(pair.Keeper.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        bool changed = false;
        bool armed = false;
        var service = new QuarantineService(History) {
            CheckpointAsync = (stage, _) => { if (stage == QuarantineCheckpoint.BeforeSourceDelete) armed = true; return Task.CompletedTask; }
        };
        IdentityReader.HashStartedObserver.Value = path => {
            if (!armed || changed || !path.EndsWith(".holding", StringComparison.Ordinal)) return;
            changed = true;
            writer.SetLength(0);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("keeper changed during source hash"));
            writer.Flush(true);
        };
        try {
            await Assert.ThrowsAnyAsync<IOException>(() => service.QuarantineAsync(pair.Target, pair.Keeper, Destination));
        }
        finally { IdentityReader.HashStartedObserver.Value = null; }
        Assert.True(changed);
        Assert.Equal("original media bytes", await File.ReadAllTextAsync(pair.Target.Path));
        Assert.Equal("keeper changed during source hash", await File.ReadAllTextAsync(pair.Keeper.Path));
        var failed = Assert.Single(await service.HistoryAsync());
        Assert.Equal(QuarantineState.Failed, failed.State);
        Assert.False(File.Exists(failed.SourceStagingPath));
    }

    private QuarantineRecord MakeRecord((FileIdentity Target, FileIdentity Keeper) pair, QuarantineState state) {
        string id = Guid.NewGuid().ToString("N");
        return new() {
            OperationId = id, Target = pair.Target, Keeper = pair.Keeper, OriginalPath = pair.Target.Path,
            DestinationDirectory = Destination, QuarantinePath = Path.Combine(Destination, id + "_" + Path.GetFileName(pair.Target.Path)),
            StagingPath = Path.Combine(Destination, ".vdf-" + id + ".copying"),
            SourceStagingPath = Path.Combine(SourceDirectory, ".vdf-" + id + ".holding"),
            RestoringPath = Path.Combine(SourceDirectory, ".vdf-" + id + ".restoring"),
            State = state, ByteIdentical = true, Reason = "test operation"
        };
    }

    private Task WriteJournalAsync(QuarantineRecord record) => File.WriteAllTextAsync(History,
        JsonSerializer.Serialize(new QuarantineEnvelope { Operations = new() { record } },
            CleanupJsonContext.Default.QuarantineEnvelope));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
