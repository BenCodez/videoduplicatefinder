// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests.Services;

[Collection("WebSettingsOverride")]
public sealed class CleanupSafetyTests : IAsyncLifetime {
	readonly string folder = Path.Combine(Path.GetTempPath(), "VDF.WebCleanupTests." + Guid.NewGuid().ToString("N"));
	readonly ScanService scan;
	readonly Guid group = Guid.NewGuid();
	readonly byte[] original = [1, 2, 3, 4];
	readonly byte[] keeperBytes = [5, 6, 7, 8];
	CleanupProtectionPolicy policy = new() { NfoOptions = new NfoOptions { Enabled = true } };

	public CleanupSafetyTests() {
		Directory.CreateDirectory(folder);
		WebSettingsService.TestOverrideSettingsPath = Path.Combine(folder, "web-settings.json");
		scan = new ScanService(new WebSettingsService());
		scan.Settings.CustomDatabaseFolder = folder;
	}

	public async Task InitializeAsync() {
		await CleanupProtectionPolicyStore.SaveAsync(folder, policy);
		await ScanEngine.LoadDatabase(folder);
		DatabaseUtils.Database.Clear();
	}

	public Task DisposeAsync() {
		scan.Dispose();
		WebSettingsService.TestOverrideSettingsPath = null;
		IdentityReader.HashStartedObserver.Value = null;
		Directory.Delete(folder, recursive: true);
		return Task.CompletedTask;
	}

	DuplicateItem Seed(string name, byte[]? bytes = null, bool addToResults = true) {
		string path = Path.Combine(folder, name);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes ?? original);
		var entry = new FileEntry(path);
		var item = new DuplicateItem(entry, 0.02f, group, DuplicateFlags.None);
		if (addToResults) {
			scan.Engine.Duplicates.Add(item);
			DatabaseUtils.Database.Add(entry);
		}
		return item;
	}

	(DuplicateItem Target, DuplicateItem Keeper) SeedPair(bool capture = true) {
		var target = Seed("remove/target.mp4");
		var keeper = Seed("keep/keeper.mp4", keeperBytes);
		if (capture) scan.CaptureCleanupSnapshots();
		return (target, keeper);
	}

	DuplicateItem SeedKeeperAlias(DuplicateItem keeper) {
		string path = Path.Combine(folder, "keep/existing-alias.mp4");
		HardLinkUtils.CreateHardLink(path, keeper.Path);
		var entry = new FileEntry(path);
		var alias = new DuplicateItem(entry, 0.04f, group, DuplicateFlags.None);
		scan.Engine.Duplicates.Add(alias);
		DatabaseUtils.Database.Add(entry);
		return alias;
	}

	Task<FileOpResult> Remove(DuplicateItem target, bool links) => links
		? scan.CreateLinksAsync([target], hardLinks: true)
		: scan.DeleteItemsAsync([target], permanent: true);

	void AssertRetained(FileOpResult result, DuplicateItem target, DuplicateItem keeper) {
		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Equal(0, result.FreedBytes);
		Assert.NotEmpty(result.Errors);
		Assert.Equal(original, File.ReadAllBytes(target.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
		Assert.Contains(target, scan.Duplicates);
		Assert.False(scan.FileOpRunning);
		Assert.False(scan.IsBusy);
	}


	[Theory]
	[InlineData(ReviewChoice.Keep, false, 0)]
	[InlineData(ReviewChoice.Keep, false, 1)]
	[InlineData(ReviewChoice.Keep, false, 2)]
	[InlineData(ReviewChoice.None, true, 0)]
	[InlineData(ReviewChoice.None, true, 1)]
	[InlineData(ReviewChoice.None, true, 2)]
	[InlineData(ReviewChoice.Ignore, false, 0)]
	[InlineData(ReviewChoice.Ignore, false, 1)]
	[InlineData(ReviewChoice.Ignore, false, 2)]
	[InlineData(ReviewChoice.RejectGroup, false, 0)]
	[InlineData(ReviewChoice.RejectGroup, false, 1)]
	[InlineData(ReviewChoice.RejectGroup, false, 2)]
	public async Task MoveRetainsDeliberateDecisionsOnSourceAndVisibleOrHiddenAliases(ReviewChoice choice, bool protect, int aliasKind) {
		var (target, keeper) = SeedPair(capture: false);
		string reviewedPath = target.Path;
		if (aliasKind != 0) {
			reviewedPath = Path.Combine(folder, "retained-alias.mp4");
			HardLinkUtils.CreateHardLink(reviewedPath, target.Path);
			if (aliasKind == 1)
				scan.Engine.Duplicates.Add(new DuplicateItem(new FileEntry(reviewedPath), 0.04f, group, DuplicateFlags.None));
		}
		scan.CaptureCleanupSnapshots();
		var reviewed = FileIdentity.Capture(reviewedPath)!;
		var source = FileIdentity.Capture(target.Path)!;
		string reviewPath = Path.Combine(folder, "ReviewedFiles.json");
		await new ReviewStore(reviewPath).SaveAsync([new() { Identity = reviewed, Choice = choice, ManuallyProtected = protect }]);
		byte[] journal = File.ReadAllBytes(reviewPath);
		string destination = Path.Combine(folder, "blocked-move");

		for (int retry = 0; retry < 2; retry++) {
			var result = await scan.MoveItemsAsync([target], destination);
			AssertRetained(result, target, keeper);
			Assert.Contains(result.Errors, error => error.Contains("deliberately protected", StringComparison.Ordinal));
			Assert.False(Directory.Exists(destination));
			Assert.True(source.MatchesCurrent(forceHash: true));
			Assert.True(reviewed.MatchesCurrent(forceHash: true));
			Assert.Equal(journal, File.ReadAllBytes(reviewPath));
		}
		AssertRetained(await scan.DeleteItemsAsync([target], permanent: true), target, keeper);
		Assert.True(reviewed.MatchesCurrent(forceHash: true));
		Assert.Equal(journal, File.ReadAllBytes(reviewPath));
	}

	[Theory]
	[InlineData(ReviewChoice.Keep, false)]
	[InlineData(ReviewChoice.None, true)]
	[InlineData(ReviewChoice.Ignore, false)]
	[InlineData(ReviewChoice.RejectGroup, false)]
	public async Task MoveRechecksLateAliasRetentionBeforeRenaming(ReviewChoice choice, bool protect) {
		var (target, keeper) = SeedPair(capture: false);
		string aliasPath = Path.Combine(folder, "hidden-retained-alias.mp4");
		HardLinkUtils.CreateHardLink(aliasPath, target.Path);
		scan.CaptureCleanupSnapshots();
		var alias = FileIdentity.Capture(aliasPath)!;
		var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
		await store.SaveAsync([]);
		int targetHashes = 0;
		IdentityReader.HashStartedObserver.Value = path => {
			if (PathComparer.ForCurrentPlatform.Equals(path, target.Path) && ++targetHashes == 2)
				store.SaveAsync([new() { Identity = alias, Choice = choice, ManuallyProtected = protect }]).GetAwaiter().GetResult();
		};
		string destination = Path.Combine(folder, "late-blocked-move");
		FileOpResult result;
		try { result = await scan.MoveItemsAsync([target], destination); }
		finally { IdentityReader.HashStartedObserver.Value = null; }

		AssertRetained(result, target, keeper);
		Assert.True(targetHashes >= 2);
		Assert.Contains(result.Errors, error => error.Contains("deliberately protected", StringComparison.Ordinal));
		Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(target.Path))));
		Assert.True(alias.MatchesCurrent(forceHash: true));
		AssertRetained(await scan.DeleteItemsAsync([target], permanent: true), target, keeper);
		await store.LoadAsync();
		Assert.Equal(choice, store.Find(aliasPath)!.Choice);
		Assert.Equal(protect, store.Find(aliasPath)!.ManuallyProtected);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task MoveRefusesUnavailableEstablishedJournal(int failure) {
		var (target, keeper) = SeedPair(capture: false);
		string path = Path.Combine(folder, "ReviewedFiles.json");
		var store = new ReviewStore(path);
		await store.SaveAsync([]);
		await store.SaveAsync([]); // Establish a valid backup as well as the primary.
		scan.CaptureCleanupSnapshots();
		if (failure == 0) File.WriteAllText(path, "malformed");
		else File.Delete(path);
		if (failure == 2) File.Delete(path + ".bak");
		string destination = Path.Combine(folder, "unavailable-journal-move");

		for (int retry = 0; retry < 2; retry++) {
			AssertRetained(await scan.MoveItemsAsync([target], destination), target, keeper);
			Assert.False(Directory.Exists(destination));
		}
	}


	[Theory]
	[InlineData(0, 0, ReviewChoice.Keep, false)]
	[InlineData(0, 1, ReviewChoice.None, true)]
	[InlineData(0, 2, ReviewChoice.Ignore, false)]
	[InlineData(1, 0, ReviewChoice.RejectGroup, false)]
	[InlineData(1, 1, ReviewChoice.Keep, false)]
	[InlineData(1, 2, ReviewChoice.None, true)]
	[InlineData(2, 0, ReviewChoice.Ignore, false)]
	[InlineData(2, 1, ReviewChoice.RejectGroup, false)]
	[InlineData(2, 2, ReviewChoice.Keep, false)]
	[InlineData(3, 0, ReviewChoice.None, true)]
	[InlineData(3, 1, ReviewChoice.Ignore, false)]
	[InlineData(3, 2, ReviewChoice.RejectGroup, false)]
	public async Task RestartedMoveRequiresMissingJournalWhenPersistentInitializationEvidenceRemains(
		int evidence, int aliasKind, ReviewChoice choice, bool protect) {
		var (target, keeper) = SeedPair(capture: false);
		string reviewedPath = target.Path;
		DuplicateItem? visibleAlias = null;
		if (aliasKind != 0) {
			reviewedPath = Path.Combine(folder, "restored-retained-alias.mp4");
			HardLinkUtils.CreateHardLink(reviewedPath, target.Path);
			if (aliasKind == 1)
				visibleAlias = new DuplicateItem(new FileEntry(reviewedPath), 0.04f, group, DuplicateFlags.None);
		}
		var source = FileIdentity.Capture(target.Path)!;
		var reviewed = FileIdentity.Capture(reviewedPath)!;
		string reviewPath = Path.Combine(folder, "ReviewedFiles.json");
		string policyPath = Path.Combine(folder, CleanupProtectionPolicyStore.FileName);
		await new ReviewStore(reviewPath).SaveAsync([new() { Identity = reviewed, Choice = choice, ManuallyProtected = protect }]);
		byte[] journal = File.ReadAllBytes(reviewPath);
		byte[] policyBytes = File.ReadAllBytes(policyPath);
		Assert.True(File.Exists(reviewPath + ".lock"));
		File.Delete(reviewPath);
		File.Delete(reviewPath + ".bak");
		// Isolate each form of persistent evidence; the new service has never seen the ledger.
		if (evidence != 3) File.Delete(reviewPath + ".lock");
		if (evidence == 1) File.Delete(policyPath);
		else if (evidence == 2) File.WriteAllText(policyPath, "malformed initialized policy");
		else if (evidence == 3) File.Delete(policyPath);
		if (evidence != 1) File.Delete(policyPath + ".bak");
		using var restarted = new ScanService(new WebSettingsService());
		restarted.Settings.CustomDatabaseFolder = folder;
		restarted.Engine.Duplicates.Add(target);
		restarted.Engine.Duplicates.Add(keeper);
		if (visibleAlias is not null) restarted.Engine.Duplicates.Add(visibleAlias);
		restarted.CaptureCleanupSnapshots(folder);
		string destination = Path.Combine(folder, "restart-blocked-move");

		for (int retry = 0; retry < 2; retry++) {
			var refused = await restarted.MoveItemsAsync([target], destination);
			Assert.Equal(0, refused.Done);
			Assert.Equal(1, refused.Failed);
			Assert.NotEmpty(refused.Errors);
			Assert.Contains(target, restarted.Duplicates);
			Assert.False(restarted.IsBusy);
			Assert.False(Directory.Exists(destination));
			Assert.True(source.MatchesCurrent(forceHash: true));
			Assert.True(reviewed.MatchesCurrent(forceHash: true));
			Assert.False(File.Exists(reviewPath));
			Assert.False(File.Exists(reviewPath + ".bak"));
		}
		// Restore the original generation verbatim. Move must not have invalidated its decisions.
		File.WriteAllBytes(reviewPath, journal);
		File.WriteAllBytes(reviewPath + ".bak", journal);
		File.WriteAllBytes(policyPath, policyBytes);
		File.WriteAllBytes(policyPath + ".bak", policyBytes);
		var removed = await restarted.DeleteItemsAsync([target], permanent: true);
		Assert.Equal(0, removed.Done);
		Assert.Equal(1, removed.Failed);
		Assert.Contains(removed.Errors, error => error.Contains("review decision", StringComparison.Ordinal));
		Assert.Equal(original, File.ReadAllBytes(target.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
		Assert.True(reviewed.MatchesCurrent(forceHash: true));
		Assert.Equal(journal, File.ReadAllBytes(reviewPath));
		var restored = new ReviewStore(reviewPath);
		await restored.LoadAsync();
		Assert.Equal(choice, restored.Find(reviewedPath)!.Choice);
		Assert.Equal(protect, restored.Find(reviewedPath)!.ManuallyProtected);
	}

	[Fact]
	public async Task MoveRequiresItsUninitializedStateFolderToRemainAvailable() {
		string stateFolder = Path.Combine(folder, "unavailable-web-state");
		Directory.CreateDirectory(stateFolder);
		Assert.True(await ScanEngine.LoadDatabase(stateFolder));
		scan.Settings.CustomDatabaseFolder = stateFolder;
		var (target, keeper) = SeedPair(capture: false);
		scan.CaptureCleanupSnapshots(stateFolder);
		var source = FileIdentity.Capture(target.Path)!;
		Directory.Delete(stateFolder, recursive: true);
		string destination = Path.Combine(folder, "unavailable-state-move");

		for (int retry = 0; retry < 2; retry++) {
			AssertRetained(await scan.MoveItemsAsync([target], destination), target, keeper);
			Assert.True(source.MatchesCurrent(forceHash: true));
			Assert.False(Directory.Exists(destination));
			Assert.False(Directory.Exists(stateFolder));
		}
	}

	[Fact]
	public async Task MoveFromNewWebStateDoesNotCreateDeletionAuthorityForRemainingAlias() {
		// The shared fixture initializes GUI policy/reviews; use separate untouched Web state.
		string stateFolder = Path.Combine(folder, "new-web-state");
		Directory.CreateDirectory(stateFolder);
		Assert.True(await ScanEngine.LoadDatabase(stateFolder));
		scan.Settings.CustomDatabaseFolder = stateFolder;
		var (target, keeper) = SeedPair(capture: false);
		string aliasPath = Path.Combine(folder, "source-alias.mp4");
		HardLinkUtils.CreateHardLink(aliasPath, target.Path);
		var alias = new DuplicateItem(new FileEntry(aliasPath), 0.04f, group, DuplicateFlags.None);
		scan.Engine.Duplicates.Add(alias);
		scan.CaptureCleanupSnapshots();
		string destination = Path.Combine(folder, "new-state-move");

		var moved = await scan.MoveItemsAsync([target], destination);

		Assert.Equal(1, moved.Done);
		Assert.Equal(0, moved.Failed);
		var reviews = new ReviewStore(Path.Combine(stateFolder, "ReviewedFiles.json"));
		await reviews.LoadAsync();
		Assert.Empty(reviews.Records);
		Assert.False(File.Exists(Path.Combine(stateFolder, CleanupProtectionPolicyStore.FileName)));
		AssertRetained(await scan.DeleteItemsAsync([alias], permanent: true), alias, keeper);
		await reviews.LoadAsync();
		Assert.Empty(reviews.Records);
		Assert.False(File.Exists(Path.Combine(stateFolder, CleanupProtectionPolicyStore.FileName)));
		Assert.True(HardLinkUtils.AreSameFile(aliasPath, Path.Combine(destination, Path.GetFileName(target.Path))));
	}

	[Fact]
	public async Task HardlinkReplacementRejectsAProtectedKeeperWithoutChangingEitherIdentity() {
		var (target, keeper) = SeedPair();
		var targetIdentity = FileIdentity.Capture(target.Path)!;
		var keeperIdentity = FileIdentity.Capture(keeper.Path)!;
		var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
		await store.SaveAsync([new() { Identity = keeperIdentity, ManuallyProtected = true }]);

		AssertRetained(await Remove(target, links: true), target, keeper);

		Assert.True(targetIdentity.MatchesCurrent(forceHash: true));
		Assert.True(keeperIdentity.MatchesCurrent(forceHash: true));
		await store.LoadAsync();
		Assert.True(store.Find(keeper.Path)!.ManuallyProtected);
		Assert.False(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
	}

	[Fact]
	public async Task RemovalRespectsProtectionOnAnotherAliasOfTheTarget() {
		var (target, keeper) = SeedPair(capture: false);
		string alias = Path.Combine(folder, "protected-target-alias.mp4");
		HardLinkUtils.CreateHardLink(alias, target.Path);
		scan.CaptureCleanupSnapshots();
		var aliasIdentity = FileIdentity.Capture(alias)!;
		var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
		await store.SaveAsync([new() { Identity = aliasIdentity, ManuallyProtected = true }]);

		AssertRetained(await Remove(target, links: false), target, keeper);

		Assert.True(aliasIdentity.MatchesCurrent(forceHash: true));
		Assert.False(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
	}

	[Theory]
	[InlineData(ReviewChoice.Keep, false)]
	[InlineData(ReviewChoice.None, true)]
	public async Task InterruptedClaimRecoveryPreservesAliasRetentionAcrossRescanAndRetry(ReviewChoice choice, bool protect) {
		var (target, keeper) = SeedPair(capture: false);
		string aliasPath = Path.Combine(folder, "retained-target-alias.mp4");
		HardLinkUtils.CreateHardLink(aliasPath, target.Path);
		scan.CaptureCleanupSnapshots();
		var targetIdentity = Assert.IsType<FileIdentity>(FileIdentity.Capture(target.Path));
		var keeperIdentity = Assert.IsType<FileIdentity>(FileIdentity.Capture(keeper.Path));
		var aliasIdentity = Assert.IsType<FileIdentity>(FileIdentity.Capture(aliasPath));
		var reviews = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
		await reviews.SaveAsync([new() { Identity = aliasIdentity, Choice = choice, ManuallyProtected = protect }]);
		string id = Guid.NewGuid().ToString("N");
		var pending = new LegacyRemovalRecord {
			Id = id, Target = targetIdentity, Keeper = keeperIdentity,
			ClaimPath = Path.Combine(Path.GetDirectoryName(target.Path)!, $".vdf-{id}.holding"),
			State = LegacyRemovalState.Claimed, RecoveryRequired = true,
			Outcome = "Interrupted after the source claim, before restoration proof."
		};
		string journalPath = Path.Combine(folder, "LegacyCleanupHistory.json");
		await DurableJsonFile.WriteAsync(journalPath, new LegacyRemovalEnvelope { Records = [pending] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope);
		File.Move(target.Path, pending.ClaimPath);
		Assert.Null(pending.ProtectionRefreshClaim);
		Assert.False(File.Exists(target.Path));

		AssertRetained(await scan.DeleteItemsAsync([target], permanent: true), target, keeper);

		Assert.False(File.Exists(pending.ClaimPath));
		await reviews.LoadAsync();
		var refreshedReview = Assert.IsType<ReviewRecord>(reviews.Find(aliasPath));
		Assert.Equal(choice, refreshedReview.Choice);
		Assert.Equal(protect, refreshedReview.ManuallyProtected);
		var recovered = Assert.IsType<LegacyRemovalEnvelope>(await DurableJsonFile.ReadPrimaryAsync(journalPath,
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope));
		var record = Assert.Single(recovered.Records);
		Assert.False(record.RecoveryRequired);
		Assert.Null(record.ProtectionRefreshClaim);

		// Publish current file snapshots as completed scan results would. A fresh
		// cleanup request must still honor the refreshed decision on another alias.
		scan.CaptureCleanupSnapshots();
		var retry = await scan.DeleteItemsAsync([target], permanent: true);

		AssertRetained(retry, target, keeper);
		Assert.Contains(retry.Errors, error => error.Contains("review decision requires retaining", StringComparison.Ordinal));
		Assert.Equal(original, File.ReadAllBytes(aliasPath));
		Assert.True(refreshedReview.Identity.MatchesCurrent(forceHash: true));
		Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
	}

	[Fact]
	public async Task HardlinkReplacementRespectsProtectionOnAnotherAliasOfTheKeeper() {
		var (target, keeper) = SeedPair(capture: false);
		string alias = Path.Combine(folder, "protected-alias.mp4");
		HardLinkUtils.CreateHardLink(alias, keeper.Path);
		scan.CaptureCleanupSnapshots();
		var keeperIdentity = FileIdentity.Capture(keeper.Path)!;
		var aliasIdentity = FileIdentity.Capture(alias)!;
		var store = new ReviewStore(Path.Combine(folder, "ReviewedFiles.json"));
		await store.SaveAsync([new() { Identity = aliasIdentity, ManuallyProtected = true }]);

		AssertRetained(await Remove(target, links: true), target, keeper);

		Assert.True(keeperIdentity.MatchesCurrent(forceHash: true));
		Assert.True(aliasIdentity.MatchesCurrent(forceHash: true));
		await store.LoadAsync();
		Assert.True(store.Find(alias)!.ManuallyProtected);
		Assert.False(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
	}

	[Fact]
	public async Task HardlinkReplacementRechecksAKeeperProtectedDuringClaimValidation() {
		var (target, keeper) = SeedPair();
		var keeperIdentity = FileIdentity.Capture(keeper.Path)!;
		bool added = false;
		IdentityReader.HashStartedObserver.Value = path => {
			if (added || !path.EndsWith(".holding", StringComparison.Ordinal)) return;
			added = true;
			new ReviewStore(Path.Combine(folder, "ReviewedFiles.json")).SaveAsync([
				new() { Identity = keeperIdentity, Choice = ReviewChoice.Keep }
			]).GetAwaiter().GetResult();
		};
		try {
			AssertRetained(await Remove(target, links: true), target, keeper);
			Assert.True(added);
			Assert.True(keeperIdentity.MatchesCurrent(forceHash: true));
		}
		finally { IdentityReader.HashStartedObserver.Value = null; }
	}

	[Fact]
	public async Task PermanentRemovalUsesAClaimAndLeavesAnIndependentKeeper() {
		var (target, keeper) = SeedPair();

		var result = await scan.DeleteItemsAsync([target], permanent: true);

		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.Equal(original.Length, result.FreedBytes);
		Assert.False(File.Exists(target.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
		Assert.DoesNotContain(target, scan.Duplicates);
		Assert.DoesNotContain(DatabaseUtils.Database, entry => entry.Path == target.Path);
		Assert.True(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
	}

	[Fact]
	public async Task TrashFailureRestoresOriginalWithoutPermanentFallback() {
		var (target, keeper) = SeedPair();
		string? privateClaim = null;
		scan.TrashMover = (claim, originalPath) => {
			privateClaim = claim;
			Assert.Equal(target.Path, originalPath);
			Assert.False(File.Exists(originalPath));
			Assert.Equal(original, File.ReadAllBytes(claim));
			return false;
		};

		var result = await scan.DeleteItemsAsync([target], permanent: false);

		AssertRetained(result, target, keeper);
		Assert.NotNull(privateClaim);
		Assert.False(File.Exists(privateClaim));
		Assert.Contains(result.Errors, error => error.Contains("trash failed", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
	}

	[Fact]
	public async Task LinkCreationFailureRestoresOriginal() {
		var (target, keeper) = SeedPair();
		bool attempted = false;
		scan.LinkCreator = (path, _, _) => {
			attempted = true;
			Assert.Equal(target.Path, path);
			Assert.False(File.Exists(path));
			throw new IOException("Test link creation failure.");
		};

		var result = await scan.CreateLinksAsync([target], hardLinks: true);

		Assert.True(attempted);
		AssertRetained(result, target, keeper);
	}

	[Theory]
	[InlineData(false, "<movie><favorite>true</favorite></movie>")]
	[InlineData(true, "<movie><favorite>true</favorite></movie>")]
	[InlineData(false, "<movie><favorite>")]
	[InlineData(true, "<movie><favorite>")]
	public async Task FavouriteOrUncertainNfoBlocksDeleteAndLinkReplacement(bool links, string xml) {
		var (target, keeper) = SeedPair();
		File.WriteAllText(Path.ChangeExtension(target.Path, ".nfo"), xml);

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ProtectedFolderBlocksDeleteAndLinkReplacement(bool links) {
		var (target, keeper) = SeedPair();
		policy.FolderRules.Add(new() { Directory = Path.GetDirectoryName(target.Path)!, ProtectAll = true });
		await CleanupProtectionPolicyStore.SaveAsync(folder, policy);

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Theory]
	[InlineData(ReviewChoice.Keep, false, false)]
	[InlineData(ReviewChoice.Keep, false, true)]
	[InlineData(ReviewChoice.None, true, false)]
	[InlineData(ReviewChoice.None, true, true)]
	public async Task ValidManualReviewBlocksDeleteAndLinkReplacement(ReviewChoice choice, bool protect, bool links) {
		var (target, keeper) = SeedPair();
		await new ReviewStore(Path.Combine(folder, "ReviewedFiles.json")).SaveAsync([
			new ReviewRecord { Identity = FileIdentity.Capture(target.Path)!, Choice = choice, ManuallyProtected = protect }
		]);

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Theory]
	[InlineData(ReviewChoice.Ignore, false)]
	[InlineData(ReviewChoice.Ignore, true)]
	[InlineData(ReviewChoice.RejectGroup, false)]
	[InlineData(ReviewChoice.RejectGroup, true)]
	public async Task ReviewOnAnotherMemberBlocksTheWholeGroup(ReviewChoice choice, bool links) {
		var (target, keeper) = SeedPair(capture: false);
		var reviewed = Seed("reviewed.mp4");
		scan.CaptureCleanupSnapshots();
		await new ReviewStore(Path.Combine(folder, "ReviewedFiles.json")).SaveAsync([
			new ReviewRecord { Identity = FileIdentity.Capture(reviewed.Path)!, Choice = choice }
		]);

		AssertRetained(await Remove(target, links), target, keeper);
		Assert.True(File.Exists(reviewed.Path));
	}

	[Theory]
	[InlineData(ReviewChoice.Ignore)]
	[InlineData(ReviewChoice.RejectGroup)]
	public async Task TemporarilyUnreadableReviewedThirdMemberBlocksDeletion(ReviewChoice choice) {
		var (target, keeper) = SeedPair(capture: false);
		var reviewed = Seed("reviewed.mp4");
		scan.CaptureCleanupSnapshots();
		await new ReviewStore(Path.Combine(folder, "ReviewedFiles.json")).SaveAsync([
			new ReviewRecord { Identity = FileIdentity.Capture(reviewed.Path)!, Choice = choice }
		]);

		using (var locked = new FileStream(reviewed.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
			Assert.Null(FileIdentity.Capture(reviewed.Path));
			var result = await scan.DeleteItemsAsync([target], permanent: true);

			AssertRetained(result, target, keeper);
			Assert.Contains(result.Errors, error => error.Contains("group member is unavailable", StringComparison.Ordinal));
			Assert.Contains(reviewed, scan.Duplicates);
			Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
			Assert.False(File.Exists(Path.Combine(folder, "LegacyCleanupHistory.json")));
		}
		Assert.Equal(original, File.ReadAllBytes(reviewed.Path));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReplacementWithCopiedDatesDoesNotInheritTheScanIdentity(bool links) {
		var (target, keeper) = SeedPair();
		DateTime created = File.GetCreationTimeUtc(target.Path);
		DateTime modified = File.GetLastWriteTimeUtc(target.Path);
		File.Move(target.Path, target.Path + ".previous");
		File.WriteAllBytes(target.Path, original);
		File.SetCreationTimeUtc(target.Path, created);
		File.SetLastWriteTimeUtc(target.Path, modified);

		AssertRetained(await Remove(target, links), target, keeper);
		Assert.Equal(original, File.ReadAllBytes(target.Path + ".previous"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ChangedKeeperCannotAuthorizeCleanup(bool links) {
		var (target, keeper) = SeedPair();
		File.Move(keeper.Path, keeper.Path + ".previous");
		File.WriteAllBytes(keeper.Path, keeperBytes);
		File.SetLastWriteTimeUtc(keeper.Path, keeper.DateModified);

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SelectingEveryMemberCannotRemoveTheLastCopy(bool links) {
		var (target, keeper) = SeedPair();
		var result = links ? await scan.CreateLinksAsync([target, keeper], hardLinks: true)
			: await scan.DeleteItemsAsync([target, keeper], permanent: true);

		Assert.Equal(0, result.Done);
		Assert.Equal(2, result.Failed);
		Assert.Equal(original, File.ReadAllBytes(target.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
	}

	[Fact]
	public async Task AHardlinkAliasIsNotAnIndependentKeeper() {
		var target = Seed("target.mp4");
		string aliasPath = Path.Combine(folder, "alias.mp4");
		HardLinkUtils.CreateHardLink(aliasPath, target.Path);
		var alias = new DuplicateItem(new FileEntry(aliasPath), 0.02f, group, DuplicateFlags.None);
		scan.Engine.Duplicates.Add(alias);
		scan.CaptureCleanupSnapshots();

		var result = await scan.DeleteItemsAsync([target], permanent: true);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Equal(original, File.ReadAllBytes(target.Path));
		Assert.Equal(original, File.ReadAllBytes(aliasPath));
	}

	[Fact]
	public async Task HardlinkBatchRefreshesItsOwnKeeperChangeStamp() {
		var (first, keeper) = SeedPair(capture: false);
		var second = Seed("remove/second.mp4");
		scan.CaptureCleanupSnapshots();

		var result = await scan.CreateLinksAsync([first, second], hardLinks: true);

		Assert.Equal(2, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.True(HardLinkUtils.AreSameFile(first.Path, keeper.Path));
		Assert.True(HardLinkUtils.AreSameFile(second.Path, keeper.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
	}

	[Fact]
	public async Task HardlinkBatchRefreshesEveryVerifiedExistingKeeperAlias() {
		var (first, keeper) = SeedPair(capture: false);
		var second = Seed("remove/second.mp4");
		var alias = SeedKeeperAlias(keeper);
		scan.CaptureCleanupSnapshots();

		var result = await scan.CreateLinksAsync([first, second], hardLinks: true);

		Assert.Equal(2, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		foreach (string path in new[] { first.Path, second.Path, alias.Path })
			Assert.True(HardLinkUtils.AreSameFile(path, keeper.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(alias.Path));
		Assert.Contains(alias, scan.Duplicates);
		Assert.Contains(keeper, scan.Duplicates);
		Assert.DoesNotContain(DatabaseUtils.Database, entry => entry.Path == first.Path || entry.Path == second.Path);
		Assert.False(scan.IsBusy);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HardlinkKeeperRefreshDoesNotAuthorizeChangedOrReplacedAlias(bool replaceAlias) {
		var (first, keeper) = SeedPair(capture: false);
		var second = Seed("remove/second.mp4");
		var alias = SeedKeeperAlias(keeper);
		scan.CaptureCleanupSnapshots();
		var before = Assert.IsType<FileIdentity>(FileIdentity.Capture(alias.Path));
		byte[] changedBytes = [9, 10, 11, 12];
		scan.LinkCreator = (path, remaining, _) => {
			HardLinkUtils.CreateHardLink(path, remaining);
			if (replaceAlias) {
				File.Move(alias.Path, alias.Path + ".previous");
				File.WriteAllBytes(alias.Path, keeperBytes);
				File.SetCreationTimeUtc(alias.Path, before.CreationUtc);
			}
			else File.WriteAllBytes(alias.Path, changedBytes);
			File.SetLastWriteTimeUtc(alias.Path, before.LastWriteUtc);
		};

		var result = await scan.CreateLinksAsync([first, second], hardLinks: true);

		Assert.Equal(1, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.Equal(original, File.ReadAllBytes(second.Path));
		Assert.Equal(replaceAlias ? keeperBytes : changedBytes, File.ReadAllBytes(alias.Path));
		var after = Assert.IsType<FileIdentity>(FileIdentity.Capture(alias.Path));
		if (replaceAlias) {
			Assert.NotEqual(before.StableFileId, after.StableFileId);
			Assert.Equal(keeperBytes, File.ReadAllBytes(alias.Path + ".previous"));
		}
		else {
			Assert.Equal(before.StableFileId, after.StableFileId);
			Assert.NotEqual(before.Sha256, after.Sha256);
		}
		Assert.Contains(second, scan.Duplicates);
		Assert.Contains(alias, scan.Duplicates);
		Assert.Contains(DatabaseUtils.Database, entry => entry.Path == second.Path);
		Assert.False(scan.IsBusy);
	}

	[Fact]
	public async Task FailedHardlinkAttemptRefreshesVerifiedKeeperAliasesBeforeLaterCleanup() {
		var (first, keeper) = SeedPair(capture: false);
		var second = Seed("remove/second.mp4");
		var alias = SeedKeeperAlias(keeper);
		scan.CaptureCleanupSnapshots();
		scan.LinkCreator = (path, remaining, _) => {
			HardLinkUtils.CreateHardLink(path, remaining);
			File.Delete(path);
			throw new IOException("Interrupted after creating and removing the replacement link.");
		};

		AssertRetained(await scan.CreateLinksAsync([first], hardLinks: true), first, keeper);

		// Restoring the failed source changes its own stamp and requires a rescan.
		// Removing that result leaves only unchanged, verified keeper aliases.
		scan.RemoveFromResults([first]);
		scan.LinkCreator = (path, remaining, _) => HardLinkUtils.CreateHardLink(path, remaining);
		var retry = await scan.CreateLinksAsync([second], hardLinks: true);

		Assert.Equal(1, retry.Done);
		Assert.Equal(0, retry.Failed);
		Assert.Empty(retry.Errors);
		Assert.True(HardLinkUtils.AreSameFile(second.Path, keeper.Path));
		Assert.True(HardLinkUtils.AreSameFile(alias.Path, keeper.Path));
		Assert.Equal(original, File.ReadAllBytes(first.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(alias.Path));
		Assert.False(scan.IsBusy);
	}

	[Fact]
	public async Task PermanentRemovalBatchVerifiesAllRemainingGroupMembers() {
		var (first, keeper) = SeedPair(capture: false);
		var second = Seed("remove/second.mp4");
		scan.CaptureCleanupSnapshots();

		var result = await scan.DeleteItemsAsync([first, second], permanent: true);

		Assert.Equal(2, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Empty(result.Errors);
		Assert.Equal(2 * original.Length, result.FreedBytes);
		Assert.False(File.Exists(first.Path));
		Assert.False(File.Exists(second.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
		Assert.DoesNotContain(DatabaseUtils.Database, entry => entry.Path == first.Path || entry.Path == second.Path);
		Assert.Contains(DatabaseUtils.Database, entry => entry.Path == keeper.Path);
		Assert.False(scan.IsBusy);
	}

	[Fact]
	public async Task SymbolicLinkIsCreatedBeforeThePrivateClaimIsRemoved() {
		var (target, keeper) = SeedPair();

		var result = await scan.CreateLinksAsync([target], hardLinks: false);

		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Equal(keeper.Path, new FileInfo(target.Path).LinkTarget);
		Assert.Equal(keeperBytes, File.ReadAllBytes(target.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ResultsWithoutSnapshotsRequireARescan(bool links) {
		var (target, keeper) = SeedPair(capture: false);

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Fact]
	public async Task StaleScannedMetadataCannotAcquireCleanupAuthority() {
		var (target, keeper) = SeedPair(capture: false);
		target.DateModified = target.DateModified.AddMinutes(-1);
		scan.CaptureCleanupSnapshots();

		AssertRetained(await Remove(target, links: false), target, keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MissingProtectionPolicyFailsClosed(bool links) {
		var (target, keeper) = SeedPair();
		string policyPath = Path.Combine(folder, CleanupProtectionPolicyStore.FileName);
		File.Delete(policyPath);
		File.Delete(policyPath + ".bak");

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnreadableProtectionPolicyCopiesFailClosed(bool links) {
		var (target, keeper) = SeedPair();
		string policyPath = Path.Combine(folder, CleanupProtectionPolicyStore.FileName);
		File.WriteAllText(policyPath, "{}");
		File.WriteAllText(policyPath + ".bak", "{}");

		AssertRetained(await Remove(target, links), target, keeper);
	}

	[Fact]
	public async Task AFileOutsideCurrentResultsCannotBeRemoved() {
		var (_, keeper) = SeedPair();
		var outside = Seed("outside.mp4", addToResults: false);

		var result = await Remove(outside, links: false);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Equal(original, File.ReadAllBytes(outside.Path));
		Assert.Equal(keeperBytes, File.ReadAllBytes(keeper.Path));
	}

	[Fact]
	public async Task ChangingSettingsFolderDoesNotSwitchExistingCleanupAuthority() {
		var (target, keeper) = SeedPair();
		policy.FolderRules.Add(new() { Directory = Path.GetDirectoryName(target.Path)!, ProtectAll = true });
		await CleanupProtectionPolicyStore.SaveAsync(folder, policy);
		string alternate = Path.Combine(folder, "alternate-state");
		Directory.CreateDirectory(alternate);
		await CleanupProtectionPolicyStore.SaveAsync(alternate, new CleanupProtectionPolicy());
		scan.Settings.CustomDatabaseFolder = alternate;

		AssertRetained(await Remove(target, links: false), target, keeper);
	}

	[Fact]
	public async Task CleanupUpdatesTheResultsDatabaseAfterMaintenanceChangesTheCurrentFolder() {
		var (target, keeper) = SeedPair();
		ScanEngine.SaveDatabase();
		byte[] resultsDatabaseBefore = File.ReadAllBytes(Path.Combine(folder, "ScannedFiles.db"));
		string alternate = Path.Combine(folder, "alternate-state");
		Directory.CreateDirectory(alternate);
		Assert.Empty(Directory.EnumerateFileSystemEntries(alternate));
		scan.Settings.CustomDatabaseFolder = alternate;
		await scan.CleanDatabaseAsync();
		Assert.Empty(DatabaseUtils.Database);
		Assert.Equal(resultsDatabaseBefore, File.ReadAllBytes(Path.Combine(folder, "ScannedFiles.db")));
		string alternateDatabase = Path.Combine(alternate, "ScannedFiles.db");
		byte[] alternateBefore = File.ReadAllBytes(alternateDatabase);

		var result = await Remove(target, links: false);

		Assert.Equal(1, result.Done);
		Assert.Equal(0, result.Failed);
		Assert.Equal(alternateBefore, File.ReadAllBytes(alternateDatabase));
		Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(DatabaseUtils.GetDatabaseFolderPath()));
		DatabaseUtils.Database.Clear();
		await ScanEngine.LoadDatabase(folder);
		Assert.DoesNotContain(DatabaseUtils.Database, entry => entry.Path == target.Path);
		Assert.Contains(DatabaseUtils.Database, entry => entry.Path == keeper.Path);
	}

	[Fact]
	public async Task PublishedSnapshotsUseTheFolderActuallyPreparedByCore() {
		var (target, keeper) = SeedPair(capture: false);
		policy.FolderRules.Add(new() { Directory = Path.GetDirectoryName(target.Path)!, ProtectAll = true });
		await CleanupProtectionPolicyStore.SaveAsync(folder, policy);
		string alternate = Path.Combine(folder, "alternate-state");
		Directory.CreateDirectory(alternate);
		await CleanupProtectionPolicyStore.SaveAsync(alternate, new CleanupProtectionPolicy());
		scan.Settings.CustomDatabaseFolder = alternate;
		scan.CaptureCleanupSnapshots(DatabaseUtils.GetDatabaseFolderPath());

		AssertRetained(await Remove(target, links: false), target, keeper);
	}

	[Fact]
	public async Task FavouriteAddedDuringClaimPreventsRemovalAndRestoresTheVideo() {
		var (target, keeper) = SeedPair();
		bool added = false;
		IdentityReader.HashStartedObserver.Value = path => {
			if (added || !path.EndsWith(".holding", StringComparison.Ordinal) || File.Exists(target.Path)) return;
			added = true;
			File.WriteAllText(Path.Combine(Path.GetDirectoryName(target.Path)!, "movie.nfo"),
				"<movie><favorite>true</favorite></movie>");
		};
		try {
			AssertRetained(await Remove(target, links: false), target, keeper);
			Assert.True(added);
		}
		finally { IdentityReader.HashStartedObserver.Value = null; }
	}

	[Fact]
	public async Task StartupReservationSurvivesErrorDisplayAndBlocksOtherOperations() {
		var (target, keeper) = SeedPair();
		var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int starts = 0;
		scan.PrepareScanAsync = (_, ct) => {
			starts++;
			preparing.TrySetResult();
			return wait.Task.WaitAsync(ct);
		};
		var start = scan.StartScanAndCompare();
		try {
			await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.True(scan.IsBusy);
			scan.SetError(new IOException("Background error while startup is still reserved."));
			Assert.True(scan.IsBusy);
			Assert.Equal(1, (await Remove(target, links: false)).Failed);
			Assert.Equal(1, (await Remove(target, links: true)).Failed);
			string destination = Path.Combine(folder, "blocked-move");
			Assert.Equal(1, (await scan.MoveItemsAsync([target], destination)).Failed);
			Assert.False(Directory.Exists(destination));
			await Assert.ThrowsAsync<InvalidOperationException>(() => scan.CleanDatabaseAsync());
			await Assert.ThrowsAsync<InvalidOperationException>(() => scan.ClearDatabaseAsync());
			Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
			Assert.Contains("operation is running", await scan.TestFilePairAsync(target.Path, keeper.Path));
			scan.Reset();
			Assert.Equal(ScanState.Error, scan.State);
			await scan.StartScanAndCompare();
			Assert.Equal(1, starts);
			Assert.Equal(original, File.ReadAllBytes(target.Path));
		}
		finally {
			scan.Stop();
			await start.WaitAsync(TimeSpan.FromSeconds(10));
		}
		Assert.False(scan.IsBusy);
	}

	[Fact]
	public async Task CleanupReservationBlocksConcurrentScanResetAndDatabaseOperations() {
		var (target, keeper) = SeedPair();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		scan.TrashMover = (_, _) => {
			entered.TrySetResult();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup test was not released.");
			return false;
		};
		var cleanup = scan.DeleteItemsAsync([target], permanent: false);
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(scan.IsBusy);
			Assert.True(scan.FileOpRunning);
			await scan.StartScanAndCompare();
			Assert.Equal(ScanState.Idle, scan.State);
			scan.Reset();
			Assert.Contains(target, scan.Duplicates);
			scan.RemoveFromResults([target]);
			Assert.Contains(target, scan.Duplicates);
			await Assert.ThrowsAsync<InvalidOperationException>(() => scan.CleanDatabaseAsync());
			await Assert.ThrowsAsync<InvalidOperationException>(() => scan.ClearDatabaseAsync());
			Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
			Assert.Equal(1, (await Remove(target, links: true)).Failed);
			scan.SetError(new IOException("Error display must not release cleanup."));
			Assert.Equal(1, (await Remove(target, links: false)).Failed);
		}
		finally { release.Set(); }
		AssertRetained(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)), target, keeper);
	}
}
