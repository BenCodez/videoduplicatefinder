// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Reflection;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Cleanup;

public sealed class CleanupProtectionGuardTests : IDisposable {
	readonly string _directory = Path.Combine(Path.GetTempPath(), "VDF.SharedProtection." + Guid.NewGuid().ToString("N"));
	string State => Path.Combine(_directory, "state");
	string PolicyFile => Path.Combine(State, CleanupProtectionPolicyStore.FileName);
	string ReviewsFile => Path.Combine(State, "ReviewedFiles.json");
	public CleanupProtectionGuardTests() => Directory.CreateDirectory(State);
	public void Dispose() {
		try { Directory.Delete(_directory, true); } catch { }
	}
	string Write(string relative, string contents = "duplicate media") {
		string path = Path.Combine(_directory, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}
	(FileIdentity Target, FileIdentity Keeper) Pair() =>
		(Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media/target.mkv"))),
		 Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("other/keeper.mkv"))));
	Task Policy(CleanupProtectionPolicy? policy = null, string? source = null) =>
		CleanupProtectionPolicyStore.SaveAsync(State, policy ?? new(), source is null ? null : FileIdentity.Capture(source));
	async Task Review(FileIdentity identity, ReviewChoice choice = ReviewChoice.None, bool protect = false) =>
		await new ReviewStore(ReviewsFile).SaveAsync([new() { Identity = identity, Choice = choice, ManuallyProtected = protect }]);
	void AssertRetained(FileIdentity target, FileIdentity keeper) {
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.True(keeper.MatchesCurrent());
		Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task VerifiedRemovalReusesOnlyTrustedUnchangedContentAndRehashesAfterCacheEviction(bool evictClaimDigest) {
		var (target, keeper) = Pair();
		if (target.ChangeStamp.Length == 0 || keeper.ChangeStamp.Length == 0) return;
		await Policy();
		IdentityReader.ClearMemoizedDigests();
		var reads = new List<string>();
		IdentityReader.HashStartedObserver.Value = path => {
			if (SafePaths.Same(path, target.Path) || SafePaths.Same(path, keeper.Path) || path.EndsWith(".holding", StringComparison.Ordinal))
				reads.Add(path);
		};
		var guard = new CleanupProtectionGuard(State);
		guard.BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null && evictClaimDigest) IdentityReader.ClearMemoizedDigests();
			return Task.CompletedTask;
		};
		try { await guard.RemoveAsync(target, keeper, File.Delete); }
		finally { IdentityReader.HashStartedObserver.Value = null; }
		Assert.Equal(evictClaimDigest ? 3 : 2, reads.Count);
		Assert.True(SafePaths.Same(reads[0], keeper.Path));
		Assert.EndsWith(".holding", reads[1]);
		Assert.False(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
	}

	[Fact]
	public async Task MissingPolicy_BlocksMutation() {
		var (target, keeper) = Pair();
		await Assert.ThrowsAsync<InvalidDataException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task ExplicitDefaultPolicy_AllowsVerifiedRemoval() {
		var (target, keeper) = Pair();
		await Policy();
		await new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete, requireByteIdentical: true);
		Assert.False(File.Exists(target.Path));
		Assert.True(keeper.MatchesCurrent());
	}

	[Fact]
	public async Task MissingInitializedReviewJournal_BlocksCleanupAndPolicyRefresh() {
		var (target, keeper) = Pair();
		await Policy();
		File.Delete(ReviewsFile);
		File.Delete(ReviewsFile + ".bak");
		await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		await Assert.ThrowsAsync<InvalidDataException>(() => Policy());
		AssertRetained(target, keeper);
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(3, false)]
	[InlineData(4, false)]
	public async Task ValidatedManualProtectionAndReviewChoices_BlockRemoval(int choice, bool protect) {
		var (target, keeper) = Pair();
		await Policy();
		await Review(target, (ReviewChoice)choice, protect);
		var guard = new CleanupProtectionGuard(State);
		Assert.True((await guard.InspectAsync(target)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task ProtectedFolder_UsesBoundaryMatching() {
		var (target, keeper) = Pair();
		await Policy(new() { FolderRules = [new() { Directory = Path.GetDirectoryName(target.Path)!, ProtectAll = true }] });
		var guard = new CleanupProtectionGuard(State);
		Assert.True((await guard.InspectAsync(target)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		FileIdentity unrelated = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("media-extra/clip.mkv")));
		Assert.False((await guard.InspectAsync(unrelated)).IsProtected);
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task FavouriteAndMalformedNfo_BothBlockRemoval() {
		var (target, keeper) = Pair();
		await Policy(new() { NfoOptions = new() { Enabled = true } });
		string nfo = Write("media/target.nfo", "<movie><favorite>true</favorite></movie>");
		var guard = new CleanupProtectionGuard(State);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		File.WriteAllText(nfo, "<malformed");
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task GroupIgnoreOnDifferentMember_BlocksTarget() {
		var (target, keeper) = Pair();
		await Policy();
		await Review(keeper, ReviewChoice.Ignore);
		await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete, [target, keeper]));
		AssertRetained(target, keeper);
	}

	[Theory]
	[InlineData(3, false)]
	[InlineData(4, false)]
	[InlineData(3, true)]
	[InlineData(4, true)]
	public async Task LateGroupVetoAfterVerifiedRollback_BlocksAnotherTargetWithStaleCachedMember(int choice, bool reviewClaimSnapshot) {
		var (target, keeper) = Pair();
		var other = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("other/second-target.mkv")));
		FileIdentity[] cachedGroup = [target, other, keeper];
		await Policy();
		bool removed = false;
		bool claimed = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is null) return;
			claimed = true;
			Assert.False(File.Exists(target.Path));
			FileIdentity reviewed = reviewClaimSnapshot
				? Assert.IsType<FileIdentity>(FileIdentity.Capture(claim)) with { Path = target.Path }
				: target;
			await Review(reviewed, (ReviewChoice)choice);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { removed = true; File.Delete(claim); }, cachedGroup));
		Assert.True(claimed);
		Assert.False(removed);
		AssertRetained(target, keeper);
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		Assert.False(target.Matches(restored));
		var store = new ReviewStore(ReviewsFile);
		await store.LoadAsync();
		var veto = Assert.IsType<ReviewRecord>(store.Find(target.Path));
		Assert.Equal((ReviewChoice)choice, veto.Choice);
		Assert.True(restored.Matches(veto.Identity));
		var history = Assert.IsType<LegacyRemovalEnvelope>(await DurableJsonFile.ReadPrimaryAsync(
			Path.Combine(State, "LegacyCleanupHistory.json"), LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope,
			LegacyRemovalService.ValidateEnvelope));
		var operation = Assert.Single(history.Records);
		Assert.False(operation.RecoveryRequired);
		Assert.Null(operation.ProtectionRefreshClaim);

		var retryGuard = new CleanupProtectionGuard(State);
		await Assert.ThrowsAnyAsync<IOException>(() => retryGuard.ValidateGroupAsync(cachedGroup));
		await Assert.ThrowsAnyAsync<IOException>(() => retryGuard.RemoveAsync(other, keeper, claim => { removed = true; File.Delete(claim); }, cachedGroup));
		Assert.False(removed);
		Assert.True(restored.MatchesCurrent());
		Assert.True(other.MatchesCurrent());
		AssertRetained(other, keeper);
		await Assert.ThrowsAnyAsync<IOException>(() => retryGuard.ValidateGroupAsync([restored, other, keeper]));
	}

	[Theory]
	[InlineData(3, false)]
	[InlineData(4, false)]
	[InlineData(3, true)]
	[InlineData(4, true)]
	public async Task CurrentReplacementGroupMember_DoesNotInheritOldReview(int choice, bool reviewedMemberIsTarget) {
		var (target, keeper) = Pair();
		var third = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("other/reviewed-member.mkv")));
		FileIdentity reviewed = reviewedMemberIsTarget ? target : third;
		await Policy();
		await Review(reviewed, (ReviewChoice)choice);
		File.Move(reviewed.Path, reviewed.Path + ".old");
		File.WriteAllText(reviewed.Path, "duplicate media");
		File.SetLastWriteTimeUtc(reviewed.Path, reviewed.LastWriteUtc);
		File.SetCreationTimeUtc(reviewed.Path, reviewed.CreationUtc);
		FileIdentity replacement = Assert.IsType<FileIdentity>(FileIdentity.Capture(reviewed.Path));
		Assert.NotEqual(reviewed.StableFileId, replacement.StableFileId);
		FileIdentity currentTarget = reviewedMemberIsTarget ? replacement : target;
		FileIdentity currentThird = reviewedMemberIsTarget ? third : replacement;
		FileIdentity[] currentGroup = [currentTarget, keeper, currentThird];
		var guard = new CleanupProtectionGuard(State);
		await guard.ValidateGroupAsync(currentGroup);
		await guard.RemoveAsync(currentTarget, keeper, File.Delete, currentGroup, requireByteIdentical: true);
		Assert.False(File.Exists(currentTarget.Path));
		Assert.True(keeper.MatchesCurrent());
		Assert.True(currentThird.MatchesCurrent());
		Assert.Equal("duplicate media", File.ReadAllText(reviewed.Path + ".old"));
		var store = new ReviewStore(ReviewsFile);
		await store.LoadAsync();
		Assert.Equal(reviewed, Assert.Single(store.Records).Identity);
		Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task TemporarilyUnreadableGroupReviewStillRequiresRetainingOtherMembers() {
		var (target, keeper) = Pair();
		var third = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("other/third.mkv")));
		await Policy();
		await Review(third, ReviewChoice.RejectGroup);
		using (var locked = new FileStream(third.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
			Assert.Null(FileIdentity.Capture(third.Path));
			await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete, [target, keeper, third]));
		}
		AssertRetained(target, keeper);
		Assert.True(third.MatchesCurrent());
	}

	[Fact]
	public async Task StaleReviewIdentity_DoesNotProtectAnUnrelatedReplacement() {
		var (target, keeper) = Pair();
		await Policy();
		await Review(target, protect: true);
		File.Move(target.Path, target.Path + ".old");
		File.WriteAllText(target.Path, "replacement content");
		FileIdentity replacement = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		Assert.False((await new CleanupProtectionGuard(State).InspectAsync(replacement)).IsProtected);
		Assert.Equal("duplicate media", File.ReadAllText(target.Path + ".old"));
	}

	[Fact]
	public async Task ChangedSettingsCannotLendTheirIdentityToAnOlderPolicy() {
		string settings = Write("config/Settings.json", "{\"nfo\":false}");
		await Policy(source: settings);
		byte[] originalPolicy = File.ReadAllBytes(PolicyFile);
		var source = Assert.IsType<FileIdentity>(FileIdentity.Capture(settings));
		File.WriteAllText(settings, "{\"nfo\":true}");
		await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtectionPolicyStore.SaveAsync(State, new(), source));
		Assert.Equal(originalPolicy, File.ReadAllBytes(PolicyFile));
		await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtectionPolicyStore.LoadAsync(State));
	}

	[Fact]
	public async Task MissingPrimaryPolicyAndSettingsCannotOverwriteTheRestrictiveBackup() {
		string settings = Write("config/Settings.json", "{\"nfo\":true}");
		var protectedPolicy = new CleanupProtectionPolicy { NfoOptions = new() { Enabled = true } };
		await Policy(protectedPolicy, settings);
		await Policy(protectedPolicy, settings);
		byte[] backup = File.ReadAllBytes(PolicyFile + ".bak");
		byte[] reviews = File.ReadAllBytes(ReviewsFile);
		File.Delete(settings);
		File.Delete(PolicyFile);
		await Assert.ThrowsAsync<InvalidDataException>(() => Policy());
		Assert.False(File.Exists(PolicyFile));
		Assert.Equal(backup, File.ReadAllBytes(PolicyFile + ".bak"));
		Assert.Equal(reviews, File.ReadAllBytes(ReviewsFile));
	}

	[Fact]
	public async Task ChangedSourceSettings_RefusesStaleProjectionUntilRefreshed() {
		var (target, keeper) = Pair();
		string settings = Write("config/Settings.json", "{\"theme\":\"light\"}");
		await Policy(source: settings);
		File.WriteAllText(settings, "{\"theme\":\"dark\"}");
		await Assert.ThrowsAsync<InvalidDataException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
		await Policy(source: settings);
		await new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete);
		Assert.False(File.Exists(target.Path));
	}

	[Fact]
	public async Task MissingEstablishedSettings_CannotReplaceProtectionPolicyWithStartupDefaults() {
		var (target, keeper) = Pair();
		string settings = Write("config/Settings.json", "{\"nfo\":true}");
		await Policy(new() { NfoOptions = new() { Enabled = true } }, settings);
		byte[] originalPolicy = File.ReadAllBytes(PolicyFile);
		File.Delete(settings);
		await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtectionPolicyStore.ValidateSettingsWriteAsync(State));
		await Assert.ThrowsAsync<InvalidDataException>(() => Policy());
		Assert.Equal(originalPolicy, File.ReadAllBytes(PolicyFile));
		await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
		File.WriteAllText(settings, "{\"nfo\":true}");
		await CleanupProtectionPolicyStore.ValidateSettingsWriteAsync(State);
		Assert.True((await CleanupProtectionPolicyStore.LoadAsync(State)).NfoOptions.Enabled);
	}

	[Fact]
	public async Task CorruptCurrentPolicy_DoesNotAuthorizeFromOlderPermissiveBackup() {
		var (target, keeper) = Pair();
		await Policy();
		byte[] permissive = File.ReadAllBytes(PolicyFile);
		await Policy(new() { NfoOptions = new() { Enabled = true } });
		File.WriteAllBytes(PolicyFile + ".bak", permissive);
		Write("media/target.nfo", "<movie><favorite>true</favorite></movie>");
		File.WriteAllText(PolicyFile, "{}");
		await Assert.ThrowsAnyAsync<JsonException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task PartialManualRecord_DoesNotAuthorizeFromOlderPermissiveBackup() {
		var (target, keeper) = Pair();
		await Policy();
		byte[] permissive = File.ReadAllBytes(ReviewsFile);
		await Review(target, protect: true);
		File.WriteAllBytes(ReviewsFile + ".bak", permissive);
		string json = File.ReadAllText(ReviewsFile).Replace("\"manuallyProtected\": true,", "", StringComparison.Ordinal);
		Assert.DoesNotContain("\"manuallyProtected\"", json);
		File.WriteAllText(ReviewsFile, json);
		await Assert.ThrowsAnyAsync<JsonException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, keeper, File.Delete));
		AssertRetained(target, keeper);
	}

	[Fact]
	public async Task LiveMovieNfoAddedAfterClaim_IsDetectedAndOriginalRestored() {
		var (target, keeper) = Pair();
		await Policy(new() { NfoOptions = new() { Enabled = true } });
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null) {
				DateTime stamp = Directory.GetLastWriteTimeUtc(Path.GetDirectoryName(target.Path)!);
				Write("media/movie.nfo", "<movie><favorite>true</favorite></movie>");
				Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(target.Path)!, stamp);
			}
			return Task.CompletedTask;
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		AssertRetained(target, keeper);
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(3, false)]
	[InlineData(4, false)]
	public async Task ManualProtectionAddedAfterClaim_IsDetectedFromChangedJournal(int choice, bool protect) {
		var (target, keeper) = Pair();
		await Policy();
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is not null) await Review(target, (ReviewChoice)choice, protect);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		AssertRetained(target, keeper);
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		var freshGuard = new CleanupProtectionGuard(State);
		Assert.True((await freshGuard.InspectAsync(restored)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(restored, keeper, File.Delete, requireByteIdentical: true));
		AssertRetained(restored, keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LateHardlinkAliasProtection_PreservesBothSnapshotsAcrossRollback(bool reviewCurrentClaimSnapshot) {
		var (initialTarget, keeper) = Pair();
		string aliasPath = Path.Combine(_directory, "media", "alias.mkv");
		HardLinkUtils.CreateHardLink(aliasPath, initialTarget.Path);
		FileIdentity target = Assert.IsType<FileIdentity>(FileIdentity.Capture(initialTarget.Path));
		FileIdentity alias = Assert.IsType<FileIdentity>(FileIdentity.Capture(aliasPath));
		await Policy();
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is null) return;
			FileIdentity reviewedAlias = reviewCurrentClaimSnapshot ? Assert.IsType<FileIdentity>(FileIdentity.Capture(aliasPath)) : alias;
			var records = new List<ReviewRecord> { new() { Identity = reviewedAlias, ManuallyProtected = true } };
			if (!reviewCurrentClaimSnapshot) records.Add(new() { Identity = target, Choice = ReviewChoice.Keep });
			await new ReviewStore(ReviewsFile).SaveAsync(records);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		var store = new ReviewStore(ReviewsFile);
		await store.LoadAsync();
		if (!reviewCurrentClaimSnapshot) Assert.NotNull(store.Find(target.Path));
		Assert.NotNull(store.Find(aliasPath));
		var freshGuard = new CleanupProtectionGuard(State);
		foreach (string path in new[] { target.Path, aliasPath }) {
			FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
			Assert.True((await freshGuard.InspectAsync(restored)).IsProtected);
			await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(restored, keeper, File.Delete));
		}
		AssertRetained(Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path)), keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptedLegacyRecovery_RefreshesProtectionBeforeCompleting(bool alreadyRestored) {
		var (target, keeper) = Pair();
		await Policy();
		await Review(target, ReviewChoice.Ignore);
		await InterruptedLegacy(target, keeper, alreadyRestored);
		await new CleanupProtectionGuard(State).RecoverAsync();
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		var freshGuard = new CleanupProtectionGuard(State);
		Assert.True((await freshGuard.InspectAsync(restored)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(restored, keeper, File.Delete));
		AssertRetained(restored, keeper);
	}

	[Fact]
	public async Task LegacySourceRestoredWithoutDurableProof_RemainsBlockedForManualRecovery() {
		var (target, keeper) = Pair();
		await Policy();
		await Review(target, protect: true);
		await InterruptedLegacy(target, keeper, alreadyRestored: true);
		string journalPath = Path.Combine(State, "LegacyCleanupHistory.json");
		var journal = Assert.IsType<LegacyRemovalEnvelope>(await DurableJsonFile.ReadPrimaryAsync(journalPath,
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope));
		journal.Version = 1;
		Assert.Single(journal.Records).ProtectionRefreshClaim = null;
		await DurableJsonFile.WriteAsync(journalPath, journal, LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope,
			LegacyRemovalService.ValidateEnvelope);
		var guard = new CleanupProtectionGuard(State);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RecoverAsync());
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		await Assert.ThrowsAnyAsync<IOException>(() => guard.InspectAsync(restored));
		AssertRetained(restored, keeper);
	}

	[Fact]
	public async Task UnavailableRetainedAlias_KeepsRecoveryPendingAndBlocksEveryAliasRetry() {
		var (initialTarget, keeper) = Pair();
		string aliasPath = Path.Combine(_directory, "aliases", "retained.mkv");
		Directory.CreateDirectory(Path.GetDirectoryName(aliasPath)!);
		HardLinkUtils.CreateHardLink(aliasPath, initialTarget.Path);
		FileIdentity target = Assert.IsType<FileIdentity>(FileIdentity.Capture(initialTarget.Path));
		FileIdentity alias = Assert.IsType<FileIdentity>(FileIdentity.Capture(aliasPath));
		await Policy();
		string unavailableDirectory = Path.Combine(_directory, "aliases-offline");
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is null) return;
			await Review(alias, protect: true);
			Directory.Move(Path.GetDirectoryName(aliasPath)!, unavailableDirectory);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		var freshGuard = new CleanupProtectionGuard(State);
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		FileIdentity alternate = Assert.IsType<FileIdentity>(FileIdentity.Capture(Path.Combine(unavailableDirectory, Path.GetFileName(aliasPath))));
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.InspectAsync(restored));
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(alternate, keeper, File.Delete));
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RecoverAsync());
		AssertRetained(restored, keeper);
		Directory.Move(unavailableDirectory, Path.GetDirectoryName(aliasPath)!);
		await freshGuard.RecoverAsync();
		restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		Assert.True((await new CleanupProtectionGuard(State).InspectAsync(restored)).IsProtected);
		Assert.True((await new CleanupProtectionGuard(State).InspectAsync(Assert.IsType<FileIdentity>(FileIdentity.Capture(aliasPath)))).IsProtected);
	}

	[Fact]
	public async Task RollbackRefresh_DoesNotProtectALaterUnrelatedReplacementWithCopiedBytesAndDates() {
		var (target, keeper) = Pair();
		await Policy();
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is not null) await Review(target, protect: true);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, File.Delete));
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		File.Move(target.Path, target.Path + ".retained");
		File.WriteAllText(target.Path, "duplicate media");
		File.SetLastWriteTimeUtc(target.Path, restored.LastWriteUtc);
		File.SetCreationTimeUtc(target.Path, restored.CreationUtc);
		FileIdentity replacement = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		Assert.NotEqual(restored.StableFileId, replacement.StableFileId);
		Assert.False((await new CleanupProtectionGuard(State).InspectAsync(replacement)).IsProtected);
		Assert.Equal("duplicate media", File.ReadAllText(target.Path + ".retained"));
	}

	async Task InterruptedLegacy(FileIdentity target, FileIdentity keeper, bool alreadyRestored) {
		string id = Guid.NewGuid().ToString("N");
		var record = new LegacyRemovalRecord {
			Id = id, Target = target, Keeper = keeper,
			ClaimPath = Path.Combine(Path.GetDirectoryName(target.Path)!, $".vdf-{id}.holding"),
			State = LegacyRemovalState.Claimed, RecoveryRequired = true, Outcome = "Synthetic interruption."
		};
		File.Move(target.Path, record.ClaimPath);
		if (alreadyRestored) {
			record.ProtectionRefreshClaim = Assert.IsType<FileIdentity>(FileIdentity.Capture(record.ClaimPath));
			File.Move(record.ClaimPath, target.Path);
		}
		await DurableJsonFile.WriteAsync(Path.Combine(State, "LegacyCleanupHistory.json"), new LegacyRemovalEnvelope { Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope);
	}

	[Fact]
	public async Task SourceChangedDuringProtectionIo_IsRetainedByFinalNativeCheck() {
		var (target, keeper) = Pair();
		await Policy();
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null) File.WriteAllText(claim, "updated source");
			return Task.CompletedTask;
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		Assert.Equal("updated source", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent());
	}

	[Fact]
	public async Task MissingKeeperDuringProtectionIo_RestoresSource() {
		var (target, keeper) = Pair();
		await Policy();
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null) File.Delete(keeper.Path);
			return Task.CompletedTask;
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
	}

	[Fact]
	public async Task SharedQuarantineRechecksFavouriteAfterCopyAndClaim() {
		var (target, keeper) = Pair();
		await Policy(new() { NfoOptions = new() { Enabled = true } });
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null) Write("media/movie.nfo", "<movie><favorite>true</favorite></movie>");
			return Task.CompletedTask;
		} };
		string destination = Path.Combine(_directory, "quarantine");
		Directory.CreateDirectory(destination);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.QuarantineExactAsync(target, keeper, destination));
		AssertRetained(target, keeper);
		Assert.NotEmpty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	[InlineData(3, false)]
	[InlineData(4, false)]
	public async Task SharedQuarantineRollback_PreservesLateDeliberateRetention(int choice, bool protect) {
		var (target, keeper) = Pair();
		await Policy();
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is not null) await Review(target, (ReviewChoice)choice, protect);
		} };
		string destination = Path.Combine(_directory, "quarantine");
		Directory.CreateDirectory(destination);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.QuarantineExactAsync(target, keeper, destination));
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		var freshGuard = new CleanupProtectionGuard(State);
		Assert.True((await freshGuard.InspectAsync(restored)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.QuarantineExactAsync(restored, keeper, destination));
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(restored, keeper, File.Delete));
		AssertRetained(restored, keeper);
		Assert.NotEmpty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptedQuarantineRecovery_RefreshesProtectionBeforeCompleting(bool alreadyRestored) {
		var (target, keeper) = Pair();
		await Policy();
		await Review(target, ReviewChoice.RejectGroup);
		string id = Guid.NewGuid().ToString("N");
		string destination = Path.Combine(_directory, "quarantine");
		Directory.CreateDirectory(destination);
		string sourceDirectory = Path.GetDirectoryName(target.Path)!;
		var record = new QuarantineRecord {
			OperationId = id, Target = target, Keeper = keeper, OriginalPath = target.Path,
			DestinationDirectory = destination, QuarantinePath = Path.Combine(destination, id + ".mkv"),
			StagingPath = Path.Combine(destination, ".vdf-" + id + ".copying"),
			SourceStagingPath = Path.Combine(sourceDirectory, ".vdf-" + id + ".holding"),
			RestoringPath = Path.Combine(sourceDirectory, ".vdf-" + id + ".restoring"),
			State = QuarantineState.SourceClaimed, ByteIdentical = true, Reason = "Synthetic interruption."
		};
		File.Move(target.Path, record.SourceStagingPath);
		if (alreadyRestored) {
			record.ProtectionRefreshClaim = Assert.IsType<FileIdentity>(FileIdentity.Capture(record.SourceStagingPath));
			File.Move(record.SourceStagingPath, target.Path);
		}
		await File.WriteAllTextAsync(Path.Combine(State, "QuarantineHistory.json"),
			JsonSerializer.Serialize(new QuarantineEnvelope { Operations = [record] }, CleanupJsonContext.Default.QuarantineEnvelope));
		await new CleanupProtectionGuard(State).RecoverAsync();
		FileIdentity restored = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		var freshGuard = new CleanupProtectionGuard(State);
		Assert.True((await freshGuard.InspectAsync(restored)).IsProtected);
		await Assert.ThrowsAnyAsync<IOException>(() => freshGuard.RemoveAsync(restored, keeper, File.Delete));
		AssertRetained(restored, keeper);
		Assert.Equal(QuarantineState.Recovered, Assert.Single(await freshGuard.QuarantineHistoryAsync()).State);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SharedQuarantineRetainsSourceWhenMediaChangesDuringLiveProtectionIo(bool removeKeeper) {
		var (target, keeper) = Pair();
		await Policy();
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = (claim, _) => {
			if (claim is not null) {
				if (removeKeeper) File.Delete(keeper.Path);
				else File.WriteAllText(claim, "updated source");
			}
			return Task.CompletedTask;
		} };
		string destination = Path.Combine(_directory, "quarantine");
		Directory.CreateDirectory(destination);
		await Assert.ThrowsAnyAsync<IOException>(() => guard.QuarantineExactAsync(target, keeper, destination));
		Assert.Equal(removeKeeper ? "duplicate media" : "updated source", File.ReadAllText(target.Path));
		Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));
		Assert.NotEmpty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task AutomaticCleanupRefusesPerceptualOnlyPair() {
		var (target, _) = Pair();
		FileIdentity different = Assert.IsType<FileIdentity>(FileIdentity.Capture(Write("other/different.mkv", "different bytes")));
		await Policy();
		await Assert.ThrowsAnyAsync<IOException>(() => new CleanupProtectionGuard(State).RemoveAsync(target, different, File.Delete, requireByteIdentical: true));
		Assert.True(target.MatchesCurrent());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task NativeMutationRejectsReplacementDuringRetentionReads(bool move) {
		var (target, keeper) = Pair();
		await Policy();
		var expected = move ? target : keeper;
		var guard = new CleanupProtectionGuard(State);
		var gate = (SemaphoreSlim)typeof(CleanupProtectionGuard).GetField("_reviewGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guard)!;
		await gate.WaitAsync();
		Task validation;
		try {
			validation = move ? guard.ValidateIdentityChangingMoveAsync(expected) : guard.ValidateHardLinkKeeperAsync(expected);
			Assert.False(validation.IsCompleted);
			File.Move(expected.Path, expected.Path + ".old");
			File.WriteAllText(expected.Path, "duplicate media");
			File.SetLastWriteTimeUtc(expected.Path, expected.LastWriteUtc);
			File.SetCreationTimeUtc(expected.Path, expected.CreationUtc);
			Assert.NotEqual(expected.StableFileId, FileIdentity.Capture(expected.Path)!.StableFileId);
		}
		finally { gate.Release(); }
		await Assert.ThrowsAsync<IOException>(() => validation);
		Assert.Equal("duplicate media", File.ReadAllText(expected.Path));
		Assert.Equal("duplicate media", File.ReadAllText(expected.Path + ".old"));
	}

	[Fact]
	public async Task FinalHardlinkRetentionAwaitCannotDeleteSourceUsingAReplacementKeeper() {
		var (target, keeper) = Pair();
		await Policy();
		var guard = new CleanupProtectionGuard(State);
		var gate = (SemaphoreSlim)typeof(CleanupProtectionGuard).GetField("_reviewGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guard)!;
		var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bool reserved = false, mutated = false;
		guard.AfterLiveCheckAsync = async (claim, ct) => {
			if (claim is null) return;
			await gate.WaitAsync(ct);
			reserved = true;
		};
		var removal = Task.Run(() => guard.RemoveAsync(target, keeper, claim => {
			callbackEntered.TrySetResult();
			guard.ValidateHardLinkKeeperAsync(keeper).GetAwaiter().GetResult();
			mutated = true;
			HardLinkUtils.CreateHardLink(target.Path, keeper.Path);
			File.Delete(claim);
		}));
		try {
			await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(removal.IsCompleted);
			File.Move(keeper.Path, keeper.Path + ".old");
			File.WriteAllText(keeper.Path, "unrelated replacement");
		}
		finally { if (reserved) gate.Release(); }
		await Assert.ThrowsAsync<IOException>(() => removal);
		Assert.False(mutated);
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.Equal("unrelated replacement", File.ReadAllText(keeper.Path));
		Assert.Equal("duplicate media", File.ReadAllText(keeper.Path + ".old"));
		Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));
	}
}
