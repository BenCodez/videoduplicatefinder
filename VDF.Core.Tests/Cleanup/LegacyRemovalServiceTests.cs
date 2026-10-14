// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Cleanup;

public sealed class LegacyRemovalServiceTests : IDisposable {
	readonly string _directory = Path.Combine(Path.GetTempPath(), "VDF.LegacyRemoval." + Guid.NewGuid().ToString("N"));
	string JournalPath => Path.Combine(_directory, "config", "removal.json");
	LegacyRemovalService Service => new(JournalPath);

	public LegacyRemovalServiceTests() => Directory.CreateDirectory(_directory);
	public void Dispose() {
		try { Directory.Delete(_directory, recursive: true); } catch { /* Best-effort fixture cleanup. */ }
	}
	string Write(string name, string contents = "duplicate media") {
		string path = Path.Combine(_directory, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}
	(FileIdentity Target, FileIdentity Keeper) Pair() =>
		(Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/target.mkv"))),
		 Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/keeper.mkv"))));
	async Task<LegacyRemovalEnvelope> ReadJournal() => Assert.IsType<LegacyRemovalEnvelope>(
		await DurableJsonFile.ReadAsync(JournalPath, LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope,
			LegacyRemovalService.ValidateEnvelope));
	async Task<LegacyRemovalRecord> Interrupted(FileIdentity target, FileIdentity keeper,
		LegacyRemovalState state = LegacyRemovalState.Claimed, bool move = true) {
		string id = Guid.NewGuid().ToString("N");
		var record = new LegacyRemovalRecord {
			Id = id, Target = target, Keeper = keeper,
			ClaimPath = Path.Combine(Path.GetDirectoryName(target.Path)!, $".vdf-{id}.holding"),
			State = state, RecoveryRequired = true, Outcome = "Synthetic interruption."
		};
		await DurableJsonFile.WriteAsync(JournalPath, new LegacyRemovalEnvelope { Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope);
		if (move) File.Move(target.Path, record.ClaimPath);
		return record;
	}
	void AssertNoClaims() => Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));

	[Fact]
	public async Task SuccessfulRemoval_DeletesVerifiedClaimAndKeepsIndependentCopy() {
		var (target, keeper) = Pair();
		string? removedPath = null;
		await Service.RemoveAsync(target, keeper, claim => {
			removedPath = claim;
			Assert.NotEqual(target.Path, claim);
			Assert.Equal(Path.GetDirectoryName(target.Path), Path.GetDirectoryName(claim));
			Assert.StartsWith(".vdf-", Path.GetFileName(claim));
			Assert.False(File.Exists(target.Path));
			Assert.True(target.MatchesRelocated(IdentityReader.Capture(claim, true, CancellationToken.None)));
			using var json = JsonDocument.Parse(File.ReadAllText(JournalPath));
			Assert.Equal("Claimed", json.RootElement.GetProperty("records")[0].GetProperty("state").GetString());
			File.Delete(claim);
		});
		Assert.NotNull(removedPath);
		Assert.False(File.Exists(target.Path));
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
		LegacyRemovalRecord record = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Completed, record.State);
		Assert.False(record.RecoveryRequired);
		Assert.Equal(target, record.Target);
		Assert.Equal(keeper, record.Keeper);
	}

	[Fact]
	public async Task TrashFailure_RestoresSourceWithoutPermanentDeletionFallback() {
		var (target, keeper) = Pair();
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper,
			_ => throw new IOException("Synthetic trash failure.")));
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
		LegacyRemovalRecord record = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Failed, record.State);
		Assert.False(record.RecoveryRequired);
		Assert.Contains("restored", record.Outcome);
	}

	[Fact]
	public async Task CallbackThatLeavesClaim_IsRejectedAndSourceRestored() {
		var (target, keeper) = Pair();
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, _ => { }));
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
		Assert.Equal(LegacyRemovalState.Failed, Assert.Single((await ReadJournal()).Records).State);
	}

	[Fact]
	public async Task ChangedClaimDuringFailedOperation_IsRestoredAndRequiresFreshReview() {
		var (target, keeper) = Pair();
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, claim => {
			File.WriteAllText(claim, "updated source content");
			throw new IOException("Synthetic writer changed a claimed source.");
		}));
		Assert.Equal("updated source content", File.ReadAllText(target.Path));
		Assert.False(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
		Assert.Contains("manual review", Assert.Single((await ReadJournal()).Records).Outcome);
	}

	[Fact]
	public async Task ReplacementAtPublicTargetPath_IsNeverDeletedByCallback() {
		var (target, keeper) = Pair();
		await Service.RemoveAsync(target, keeper, claim => {
			File.WriteAllText(target.Path, "unrelated replacement");
			File.Delete(claim);
		});
		Assert.Equal("unrelated replacement", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
	}

	[Fact]
	public async Task ReplacedTargetIdentity_IsRejectedBeforeMutation() {
		var (target, keeper) = Pair();
		File.Move(target.Path, target.Path + ".old");
		File.WriteAllText(target.Path, "duplicate media");
		File.SetLastWriteTimeUtc(target.Path, target.LastWriteUtc);
		bool invoked = false;
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, _ => invoked = true));
		Assert.False(invoked);
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		Assert.True(File.Exists(target.Path + ".old"));
		Assert.False(File.Exists(JournalPath));
		AssertNoClaims();
	}

	[Fact]
	public async Task SameLengthSameTimestampEdit_IsRejectedByForcedHash() {
		var (target, keeper) = Pair();
		File.WriteAllText(target.Path, "different media");
		File.SetLastWriteTimeUtc(target.Path, target.LastWriteUtc);
		Assert.Equal(target.Length, new FileInfo(target.Path).Length);
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, File.Delete));
		Assert.Equal("different media", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
	}

	[Fact]
	public async Task MissingKeeper_IsRejectedWithoutClaimingTarget() {
		var (target, keeper) = Pair();
		File.Delete(keeper.Path);
		await Assert.ThrowsAnyAsync<IOException>(() => Service.RemoveAsync(target, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		Assert.False(File.Exists(JournalPath));
		AssertNoClaims();
	}

	[Fact]
	public async Task ChangedKeeper_IsRejectedWithoutClaimingTarget() {
		var (target, keeper) = Pair();
		File.WriteAllText(keeper.Path, "replacement keeper");
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		Assert.Equal("replacement keeper", File.ReadAllText(keeper.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task ProtectedTarget_IsRejectedBeforeAnyMutation() {
		var (target, keeper) = Pair();
		bool invoked = false;
		await Assert.ThrowsAsync<InvalidOperationException>(() => Service.RemoveAsync(target, keeper,
			_ => invoked = true, protectedFile: true));
		Assert.False(invoked);
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		Assert.False(File.Exists(JournalPath));
		AssertNoClaims();
	}

	[Fact]
	public async Task SamePath_IsNotAnIndependentKeeper() {
		var (target, _) = Pair();
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, target, File.Delete));
		Assert.True(target.MatchesCurrent());
		AssertNoClaims();
	}

	[Fact]
	public async Task HardLinkedKeeper_IsNotAnIndependentCopy() {
		string targetPath = Write("media/target.mkv");
		string keeperPath = Path.Combine(_directory, "media", "keeper.mkv");
		HardLinkUtils.CreateHardLink(keeperPath, targetPath);
		FileIdentity target = Assert.IsType<FileIdentity>(FileIdentity.Capture(targetPath));
		FileIdentity keeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(keeperPath));
		Assert.Equal(target.StableFileId, keeper.StableFileId);
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, File.Delete));
		Assert.True(File.Exists(targetPath));
		Assert.True(File.Exists(keeperPath));
		AssertNoClaims();
	}

	[Fact]
	public async Task SymbolicTarget_IsRejected() {
		var (target, keeper) = Pair();
		string linked = Path.Combine(_directory, "media", "linked.mkv");
		try { File.CreateSymbolicLink(linked, target.Path); }
		catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target with { Path = linked }, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		Assert.True(File.Exists(linked));
		AssertNoClaims();
	}

	[Fact]
	public async Task SymbolicAncestor_IsRejected() {
		var (target, keeper) = Pair();
		string linkedDirectory = Path.Combine(_directory, "linked");
		try { Directory.CreateSymbolicLink(linkedDirectory, Path.GetDirectoryName(target.Path)!); }
		catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
		string linkedTarget = Path.Combine(linkedDirectory, Path.GetFileName(target.Path));
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target with { Path = linkedTarget }, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		AssertNoClaims();
	}

	[Theory]
	[InlineData(0)] // Prepared
	[InlineData(1)] // Claimed
	public async Task InterruptedClaim_IsRestoredWithoutDeletionReplay(int state) {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper, (LegacyRemovalState)state);
		await Service.RecoverAsync();
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.True(keeper.MatchesCurrent());
		Assert.False(File.Exists(record.ClaimPath));
		LegacyRemovalRecord recovered = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Failed, recovered.State);
		Assert.False(recovered.RecoveryRequired);
		await Service.RecoverAsync();
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task RecoveryDoesNotRequireKeeper_WhenRestoringSource() {
		var (target, keeper) = Pair();
		await Interrupted(target, keeper);
		File.Delete(keeper.Path);
		await Service.RecoverAsync();
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task PreparedJournalWithIntactOriginal_IsCancelled() {
		var (target, keeper) = Pair();
		await Interrupted(target, keeper, LegacyRemovalState.Prepared, move: false);
		await Service.RecoverAsync();
		Assert.True(target.MatchesCurrent());
		LegacyRemovalRecord record = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Failed, record.State);
		Assert.False(record.RecoveryRequired);
		Assert.Contains("cancelled", record.Outcome);
	}

	[Fact]
	public async Task CrashAfterClaimDisappeared_RecordsAmbiguousCompletionWithoutReplaying() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		File.Delete(record.ClaimPath);
		await Service.RecoverAsync();
		Assert.False(File.Exists(target.Path));
		Assert.True(keeper.MatchesCurrent());
		LegacyRemovalRecord recovered = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Completed, recovered.State);
		Assert.False(recovered.RecoveryRequired);
		Assert.Contains("uncertain", recovered.Outcome);
		await Service.RecoverAsync();
		Assert.True(keeper.MatchesCurrent());
	}

	[Fact]
	public async Task ExistingReplacement_BlocksRecoveryWithoutOverwritingEitherFile() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		File.WriteAllText(target.Path, "unrelated replacement");
		await Assert.ThrowsAsync<IOException>(() => Service.RecoverAsync());
		Assert.Equal("unrelated replacement", File.ReadAllText(target.Path));
		Assert.Equal("duplicate media", File.ReadAllText(record.ClaimPath));
		Assert.True(Assert.Single((await ReadJournal()).Records).RecoveryRequired);
		File.Delete(target.Path); // Synthetic manual resolution of the collision.
		await Service.RecoverAsync();
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task ChangedClaim_IsPreservedForManualRecovery() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		File.WriteAllText(record.ClaimPath, "unrelated claim replacement");
		await Assert.ThrowsAsync<IOException>(() => Service.RecoverAsync());
		Assert.False(File.Exists(target.Path));
		Assert.Equal("unrelated claim replacement", File.ReadAllText(record.ClaimPath));
		Assert.True(Assert.Single((await ReadJournal()).Records).RecoveryRequired);
	}

	[Fact]
	public async Task FailedCallbackWithReplacedOriginal_LeavesClaimAndDurableRecoveryNote() {
		var (target, keeper) = Pair();
		string? privateClaim = null;
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, claim => {
			privateClaim = claim;
			File.WriteAllText(target.Path, "unrelated replacement");
			throw new IOException("Synthetic failure after a public path replacement.");
		}));
		Assert.Equal("unrelated replacement", File.ReadAllText(target.Path));
		Assert.Equal("duplicate media", File.ReadAllText(privateClaim!));
		LegacyRemovalRecord record = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Failed, record.State);
		Assert.True(record.RecoveryRequired);
		Assert.Contains("manual review", record.Outcome);
	}

	[Fact]
	public async Task CallbackFailureAfterDeletion_IsRecordedWithoutASecondDeletion() {
		var (target, keeper) = Pair();
		await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, claim => {
			File.Delete(claim);
			throw new IOException("Synthetic error after removal.");
		}));
		LegacyRemovalRecord record = Assert.Single((await ReadJournal()).Records);
		Assert.Equal(LegacyRemovalState.Completed, record.State);
		Assert.False(record.RecoveryRequired);
		Assert.Contains("uncertain", record.Outcome);
		await Service.RecoverAsync();
		Assert.True(keeper.MatchesCurrent());
	}

	[Fact]
	public async Task CorruptPrimary_UsesLastGoodBackupToRestoreClaim() {
		var (target, keeper) = Pair();
		await Interrupted(target, keeper);
		File.WriteAllText(JournalPath, "{ malformed json");
		await Service.RecoverAsync();
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		AssertNoClaims();
		Assert.Equal(LegacyRemovalState.Failed, Assert.Single((await ReadJournal()).Records).State);
	}

	[Fact]
	public async Task CorruptPrimaryAfterSecondPreparedClaim_CurrentMirrorRestoresUncommittedClaim() {
		var (earlier, keeper) = Pair();
		await Service.RemoveAsync(earlier, keeper, File.Delete);
		FileIdentity target = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/second.mkv")));
		LegacyRemovalEnvelope journal = await ReadJournal();
		string id = Guid.NewGuid().ToString("N");
		var prepared = new LegacyRemovalRecord {
			Id = id, Target = target, Keeper = keeper,
			ClaimPath = Path.Combine(Path.GetDirectoryName(target.Path)!, $".vdf-{id}.holding"),
			State = LegacyRemovalState.Prepared, RecoveryRequired = true, Outcome = "Second operation prepared."
		};
		journal.Records.Add(prepared);
		await DurableJsonFile.WriteAsync(JournalPath, journal, LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope,
			LegacyRemovalService.ValidateEnvelope);
		File.Move(target.Path, prepared.ClaimPath); // Process stops before saving Claimed.
		File.WriteAllText(JournalPath, "{ corrupt current primary");
		await Service.RecoverAsync();
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.False(File.Exists(prepared.ClaimPath));
		var recovered = (await ReadJournal()).Records;
		Assert.Equal(2, recovered.Count);
		Assert.False(recovered.Single(record => record.Id == id).RecoveryRequired);
		Assert.True(keeper.MatchesCurrent(true));
	}

	[Fact]
	public async Task FailureBetweenMirroredCommits_PreventsPreparedOperationFromClaimingSource() {
		var (earlier, keeper) = Pair();
		await Service.RemoveAsync(earlier, keeper, File.Delete);
		FileIdentity target = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/second.mkv")));
		bool callback = false;
		DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
			if (SafePaths.Same(destination, JournalPath + ".bak"))
				throw new IOException("Synthetic interruption before primary commit.");
		};
		try {
			await Assert.ThrowsAsync<IOException>(() => Service.RemoveAsync(target, keeper, _ => callback = true));
		}
		finally { DurableFileOperations.FileMovedObserver.Value = null; }
		Assert.False(callback);
		Assert.True(target.MatchesCurrent(true));
		AssertNoClaims();
		Assert.Single((await ReadJournal()).Records);
	}

	[Fact]
	public async Task WriterDuringPostClaimKeeperHash_SourceIsRevalidatedLastAndRestored() {
		if (!OperatingSystem.IsLinux()) return;
		var (target, keeper) = Pair();
		using var writer = new FileStream(target.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		bool changed = false;
		bool callback = false;
		IdentityReader.HashStartedObserver.Value = path => {
			if (changed || !SafePaths.Same(path, keeper.Path) || File.Exists(target.Path)) return;
			changed = true;
			writer.SetLength(0);
			writer.Write(System.Text.Encoding.UTF8.GetBytes("changed through the original open handle"));
			writer.Flush(true);
		};
		try {
			await Assert.ThrowsAnyAsync<IOException>(() => Service.RemoveAsync(target, keeper, _ => callback = true));
		}
		finally { IdentityReader.HashStartedObserver.Value = null; }
		Assert.True(changed);
		Assert.False(callback);
		Assert.Equal("changed through the original open handle", File.ReadAllText(target.Path));
		AssertNoClaims();
		Assert.Contains("identity changed", Assert.Single((await ReadJournal()).Records).Outcome);
	}

	[Fact]
	public async Task KeeperWriterDuringFinalSourceHash_NativeKeeperRecheckCancelsRemoval() {
		if (!OperatingSystem.IsLinux()) return;
		var (target, keeper) = Pair();
		using var writer = new FileStream(keeper.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		bool changed = false;
		bool callback = false;
		IdentityReader.HashStartedObserver.Value = path => {
			if (changed || !path.EndsWith(".holding", StringComparison.Ordinal)) return;
			changed = true;
			writer.SetLength(0);
			writer.Write(System.Text.Encoding.UTF8.GetBytes("keeper changed during source hash"));
			writer.Flush(true);
		};
		try {
			await Assert.ThrowsAnyAsync<IOException>(() => Service.RemoveAsync(target, keeper, _ => callback = true));
		}
		finally { IdentityReader.HashStartedObserver.Value = null; }
		Assert.True(changed);
		Assert.False(callback);
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		Assert.Equal("keeper changed during source hash", File.ReadAllText(keeper.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task EmptyJsonObject_IsCorruptionAndBackupRestoresPendingClaim() {
		var (target, keeper) = Pair();
		await Interrupted(target, keeper);
		File.WriteAllText(JournalPath, "{}");
		await Service.RecoverAsync();
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task MissingRequiredFieldsWithoutBackup_ArePreservedAndRecoveryRefused() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		File.WriteAllText(JournalPath, "{}");
		File.Delete(JournalPath + ".bak");
		await Assert.ThrowsAsync<InvalidDataException>(() => Service.RecoverAsync());
		Assert.Equal("{}", File.ReadAllText(JournalPath));
		Assert.True(File.Exists(record.ClaimPath));
		Assert.False(File.Exists(target.Path));
	}

	[Fact]
	public async Task VersionOneJournal_IsRecoveredAndMigratedToVersionTwo() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		File.WriteAllText(JournalPath, JsonSerializer.Serialize(new LegacyRemovalEnvelope { Version = 1, Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope));
		await Service.RecoverAsync();
		Assert.Equal(LegacyRemovalEnvelope.CurrentVersion, (await ReadJournal()).Version);
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		AssertNoClaims();
	}

	[Fact]
	public async Task VersionOneJournalWithProtectionRefreshProof_IsRejectedWithoutRestoringSource() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		record.ProtectionRefreshClaim = Assert.IsType<FileIdentity>(FileIdentity.Capture(record.ClaimPath));
		string invalid = JsonSerializer.Serialize(new LegacyRemovalEnvelope { Version = 1, Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope);
		File.WriteAllText(JournalPath, invalid);
		File.Delete(JournalPath + ".bak");
		await Assert.ThrowsAsync<InvalidDataException>(() => Service.RecoverAsync());
		Assert.Equal(invalid, File.ReadAllText(JournalPath));
		Assert.True(File.Exists(record.ClaimPath));
		Assert.False(File.Exists(target.Path));
	}

	[Fact]
	public async Task UnsupportedJournalVersion_IsPreservedWithoutMediaMutation() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		string unsupported = JsonSerializer.Serialize(new LegacyRemovalEnvelope { Version = 99, Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope);
		File.WriteAllText(JournalPath, unsupported);
		await Assert.ThrowsAsync<NotSupportedException>(() => Service.RecoverAsync());
		Assert.Equal(unsupported, File.ReadAllText(JournalPath));
		Assert.True(File.Exists(record.ClaimPath));
		Assert.False(File.Exists(target.Path));
	}

	[Fact]
	public async Task InvalidClaimAssociation_IsRejectedWithoutRestoringUnrelatedFile() {
		var (target, keeper) = Pair();
		LegacyRemovalRecord record = await Interrupted(target, keeper);
		string actualClaim = record.ClaimPath;
		record.ClaimPath = Write("unrelated.holding", "unrelated content");
		string invalid = JsonSerializer.Serialize(new LegacyRemovalEnvelope { Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope);
		File.WriteAllText(JournalPath, invalid);
		File.Delete(JournalPath + ".bak");
		await Assert.ThrowsAsync<InvalidDataException>(() => Service.RecoverAsync());
		Assert.Equal("unrelated content", File.ReadAllText(record.ClaimPath));
		Assert.True(File.Exists(actualClaim));
		Assert.False(File.Exists(target.Path));
	}

	[Fact]
	public async Task AlreadyCancelledRequest_DoesNotMutateMedia() {
		var (target, keeper) = Pair();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.RemoveAsync(target, keeper, File.Delete, ct: cancellation.Token));
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		Assert.False(File.Exists(JournalPath));
		AssertNoClaims();
	}

	[Fact]
	public async Task CancellationAfterClaim_RestoresSourceDespiteCancelledToken() {
		var (target, keeper) = Pair();
		using var cancellation = new CancellationTokenSource();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.RemoveAsync(target, keeper, _ => {
			cancellation.Cancel();
			throw new OperationCanceledException(cancellation.Token);
		}, ct: cancellation.Token));
		Assert.Equal("duplicate media", File.ReadAllText(target.Path));
		AssertNoClaims();
		Assert.False(Assert.Single((await ReadJournal()).Records).RecoveryRequired);
	}

	[Fact]
	public async Task ConcurrentServices_PreserveBothJournalRecords() {
		var (target, keeper) = Pair();
		FileIdentity second = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/second.mkv")));
		await Task.WhenAll(Service.RemoveAsync(target, keeper, File.Delete), Service.RemoveAsync(second, keeper, File.Delete));
		Assert.Equal(2, (await ReadJournal()).Records.Count);
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
	}

	[Fact]
	public async Task HeldJournalLease_PreventsMutationByAnotherService() {
		var (target, keeper) = Pair();
		Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
		using var lease = new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate,
			FileAccess.ReadWrite, FileShare.None);
		await Assert.ThrowsAnyAsync<IOException>(() => Service.RemoveAsync(target, keeper, File.Delete));
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
		AssertNoClaims();
	}

	[Fact]
	public async Task CompetingRemovalJournals_CannotRemoveEachOthersLastKeeper() {
		var (target, keeper) = Pair();
		var otherService = new LegacyRemovalService(Path.Combine(_directory, "config", "other-removal.json"));
		Task first = Service.RemoveAsync(target, keeper, File.Delete);
		Task second = otherService.RemoveAsync(keeper, target, File.Delete);
		await Assert.ThrowsAnyAsync<IOException>(() => Task.WhenAll(first, second));
		Assert.True(File.Exists(target.Path) || File.Exists(keeper.Path));
		Assert.False(File.Exists(target.Path) && File.Exists(keeper.Path));
		AssertNoClaims();
	}

	[Fact]
	public async Task EmptyRecovery_DoesNotCreateJournalOrReplayAnything() {
		var (target, keeper) = Pair();
		await Service.RecoverAsync();
		Assert.False(File.Exists(JournalPath));
		Assert.True(target.MatchesCurrent());
		Assert.True(keeper.MatchesCurrent());
	}
}
