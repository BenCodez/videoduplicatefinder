// SPDX-License-Identifier: AGPL-3.0-or-later

using System.CommandLine;
using VDF.CLI.Actions;
using VDF.CLI.Commands;
using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Review;
using VDF.Core.Selection;
using VDF.Core.ViewModels;

namespace VDF.CLI.Tests.Actions;

public sealed class GuardedDeletionTests : IDisposable {
	readonly string _directory = Path.Combine(Path.GetTempPath(), "VDF.CliProtection." + Guid.NewGuid().ToString("N"));
	string State => Path.Combine(_directory, "state");
	public GuardedDeletionTests() => Directory.CreateDirectory(State);
	public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
	string Write(string relative, string contents = "duplicate media") {
		string path = Path.Combine(_directory, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}
	static DuplicateItem Item(string path, Guid group) {
		var file = new FileInfo(path);
		return new() { Path = path, GroupId = group, SizeLong = file.Length,
			DateModified = file.LastWriteTimeUtc, DateCreated = file.CreationTimeUtc, Similarity = 100 };
	}
	(DuplicateItem Target, DuplicateItem Keeper) Pair() {
		Guid group = Guid.NewGuid();
		return (Item(Write("media/target.mkv"), group), Item(Write("media/keeper.mkv"), group));
	}
	Task Policy(CleanupProtectionPolicy? policy = null) => CleanupProtectionPolicyStore.SaveAsync(State, policy ?? new());
	Task<int> Delete(DuplicateItem target, DuplicateItem keeper, bool dryRun = false) =>
		GuardedDeletion.ExecuteAsync([target], [target, keeper], State, permanent: true, dryRun, CancellationToken.None);

	[Theory]
	[InlineData("--state")]
	[InlineData("--db")]
	public void MarkAcceptsExplicitStateContext(string option) {
		var parsed = new RootCommand { MarkCommand.Build() }.Parse($"mark -i results.json --strategy lowest-quality {option} state");
		Assert.Empty(parsed.Errors);
	}

	[Fact]
	public async Task DestructiveExecutionWithoutExplicitState_IsRefused() {
		var (target, keeper) = Pair();
		int failed = await MarkCommand.ExecuteDeletion([target], dryRun: false, permanent: true, allItems: [target, keeper]);
		Assert.Equal(1, failed);
		Assert.True(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
	}

	[Fact]
	public async Task MissingSharedPolicy_IsRefusedBeforeAnyRemoval() {
		var (target, keeper) = Pair();
		int failed = await MarkCommand.ExecuteDeletion([target], dryRun: false, permanent: true, State, [target, keeper]);
		Assert.Equal(1, failed);
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task CleanupRestoresAnInterruptedUnselectedClaimWithoutReplayingItsDeletion() {
		var (target, keeper) = Pair();
		Guid oldGroup = Guid.NewGuid();
		var interrupted = Item(Write("old/target.mkv"), oldGroup);
		var oldKeeper = Item(Write("old/keeper.mkv"), oldGroup);
		await Policy();
		string id = Guid.NewGuid().ToString("N");
		string claim = Path.Combine(Path.GetDirectoryName(interrupted.Path)!, $".vdf-{id}.holding");
		var record = new LegacyRemovalRecord {
			Id = id, Target = FileIdentity.Capture(interrupted.Path)!, Keeper = FileIdentity.Capture(oldKeeper.Path)!,
			ClaimPath = claim, State = LegacyRemovalState.Claimed, RecoveryRequired = true, Outcome = "Synthetic interruption."
		};
		await DurableJsonFile.WriteAsync(Path.Combine(State, "LegacyCleanupHistory.json"), new LegacyRemovalEnvelope { Records = [record] },
			LegacyRemovalJsonContext.Default.LegacyRemovalEnvelope, LegacyRemovalService.ValidateEnvelope);
		File.Move(interrupted.Path, claim);
		Assert.Equal(0, await Delete(target, keeper));
		Assert.False(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
		Assert.Equal("duplicate media", File.ReadAllText(interrupted.Path));
		Assert.Equal("duplicate media", File.ReadAllText(oldKeeper.Path));
		Assert.False(File.Exists(claim));
	}

	[Fact]
	public async Task ExplicitDefaultPolicy_RemovesOnlyOneVerifiedCopy() {
		var (target, keeper) = Pair();
		await Policy();
		Assert.Equal(0, await Delete(target, keeper));
		Assert.False(File.Exists(target.Path));
		Assert.Equal("duplicate media", File.ReadAllText(keeper.Path));
	}

	[Fact]
	public async Task Favourite_IsRetainedEvenWhenStrategyMarksIt() {
		var (target, keeper) = Pair();
		Write("media/target.nfo", "<movie><tag>Favourite</tag></movie>");
		await Policy(new() { NfoOptions = new() { Enabled = true } });
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
	}

	[Fact]
	public async Task ProtectedFolder_IsRetained() {
		var (target, keeper) = Pair();
		await Policy(new() { FolderRules = [new() { Directory = Path.GetDirectoryName(target.Path)!, ProtectAll = true }] });
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task PersistedManualProtection_IsRetained() {
		var (target, keeper) = Pair();
		await Policy();
		await new ReviewStore(Path.Combine(State, "ReviewedFiles.json")).SaveAsync([
			new() { Identity = FileIdentity.Capture(target.Path)!, ManuallyProtected = true }
		]);
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task GroupIgnoreOnKeeper_BlocksAllMarkedMembers() {
		var (target, keeper) = Pair();
		await Policy();
		await new ReviewStore(Path.Combine(State, "ReviewedFiles.json")).SaveAsync([
			new() { Identity = FileIdentity.Capture(keeper.Path)!, Choice = ReviewChoice.Ignore }
		]);
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task UnreadableIgnoredThirdMemberCannotDisappearFromTheGroupGuard() {
		var (target, keeper) = Pair();
		var third = Item(Write("media/third.mkv"), target.GroupId);
		await Policy();
		await new ReviewStore(Path.Combine(State, "ReviewedFiles.json")).SaveAsync([
			new() { Identity = FileIdentity.Capture(third.Path)!, Choice = ReviewChoice.Ignore }
		]);
		using (var locked = new FileStream(third.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
			Assert.Null(FileIdentity.Capture(third.Path));
			int failed = await GuardedDeletion.ExecuteAsync([target], [target, keeper, third], State, true, false, CancellationToken.None);
			Assert.Equal(1, failed);
			Assert.True(File.Exists(target.Path));
			Assert.True(File.Exists(keeper.Path));
		}
		Assert.Equal("duplicate media", File.ReadAllText(third.Path));
	}

	[Fact]
	public async Task MissingKeeper_CannotJustifyDeletingTarget() {
		var (target, keeper) = Pair();
		await Policy();
		File.Delete(keeper.Path);
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task EntireSelectedBatch_CannotUseAnotherSelectedFileAsKeeper() {
		var (target, keeper) = Pair();
		await Policy();
		int failed = await GuardedDeletion.ExecuteAsync([target, keeper], [target, keeper], State, true, false, CancellationToken.None);
		Assert.Equal(2, failed);
		Assert.True(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
	}

	[Fact]
	public async Task StaleExportMetadata_IsRefused() {
		var (target, keeper) = Pair();
		await Policy();
		File.WriteAllText(target.Path, "replacement file with different size");
		Assert.Equal(1, await Delete(target, keeper));
		Assert.Equal("replacement file with different size", File.ReadAllText(target.Path));
	}

	[Fact]
	public async Task PerceptualSimilarityDoesNotAuthorizeAutomaticDeletionOfDifferentBytes() {
		var (target, keeper) = Pair();
		await Policy();
		File.WriteAllText(keeper.Path, "different media");
		keeper = Item(keeper.Path, target.GroupId);
		Assert.Equal(1, await Delete(target, keeper));
		Assert.True(File.Exists(target.Path));
	}

	[Fact]
	public async Task GuardedDryRun_LeavesBothCopies() {
		var (target, keeper) = Pair();
		await Policy();
		Assert.Equal(0, await Delete(target, keeper, dryRun: true));
		Assert.True(File.Exists(target.Path));
		Assert.True(File.Exists(keeper.Path));
	}
}
