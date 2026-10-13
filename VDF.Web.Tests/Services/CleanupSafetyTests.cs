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
		string alternate = Path.Combine(folder, "alternate-state");
		Directory.CreateDirectory(alternate);
		scan.Settings.CustomDatabaseFolder = alternate;
		await scan.CleanDatabaseAsync();
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
		int starts = 0;
		scan.PrepareScanAsync = (_, ct) => {
			starts++;
			return wait.Task.WaitAsync(ct);
		};
		var start = scan.StartScanAndCompare();
		try {
			Assert.True(scan.IsBusy);
			scan.SetError(new IOException("Background error while startup is still reserved."));
			Assert.True(scan.IsBusy);
			Assert.Equal(1, (await Remove(target, links: false)).Failed);
			Assert.Equal(1, (await Remove(target, links: true)).Failed);
			string destination = Path.Combine(folder, "blocked-move");
			Assert.Equal(1, (await scan.MoveItemsAsync([target], destination)).Failed);
			Assert.False(Directory.Exists(destination));
			await scan.ClearDatabaseAsync();
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
			await scan.ClearDatabaseAsync();
			Assert.Contains(DatabaseUtils.Database, entry => entry.Path == target.Path);
			Assert.Equal(1, (await Remove(target, links: true)).Failed);
			scan.SetError(new IOException("Error display must not release cleanup."));
			Assert.Equal(1, (await Remove(target, links: false)).Failed);
		}
		finally { release.Set(); }
		AssertRetained(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)), target, keeper);
	}
}
