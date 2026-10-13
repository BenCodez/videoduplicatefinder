// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;

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
		CleanupProtectionPolicyStore.SaveAsync(State, policy ?? new(), source);
	async Task Review(FileIdentity identity, ReviewChoice choice = ReviewChoice.None, bool protect = false) =>
		await new ReviewStore(ReviewsFile).SaveAsync([new() { Identity = identity, Choice = choice, ManuallyProtected = protect }]);
	void AssertRetained(FileIdentity target, FileIdentity keeper) {
		Assert.True(target.MatchesRelocated(FileIdentity.Capture(target.Path)));
		Assert.True(keeper.MatchesCurrent());
		Assert.Empty(Directory.EnumerateFiles(_directory, ".vdf-*.holding", SearchOption.AllDirectories));
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

	[Fact]
	public async Task ManualProtectionAddedAfterClaim_IsDetectedFromChangedJournal() {
		var (target, keeper) = Pair();
		await Policy();
		bool callback = false;
		var guard = new CleanupProtectionGuard(State) { BeforeLiveCheckAsync = async (claim, _) => {
			if (claim is not null) await Review(target, protect: true);
		} };
		await Assert.ThrowsAnyAsync<IOException>(() => guard.RemoveAsync(target, keeper, claim => { callback = true; File.Delete(claim); }));
		Assert.False(callback);
		AssertRetained(target, keeper);
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
}
