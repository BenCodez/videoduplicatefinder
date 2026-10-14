// SPDX-License-Identifier: AGPL-3.0-or-later

using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;

namespace VDF.Core.Tests.Cleanup;

public sealed class ProtectionMutationBoundaryTests : IDisposable {
	readonly string root = Directory.CreateTempSubdirectory("vdf-protection-boundary-").FullName;
	string State => Path.Combine(root, "state");
	string Reviews => Path.Combine(State, "ReviewedFiles.json");
	string Settings => Path.Combine(root, "Settings.json");
	async Task<(FileIdentity Target, FileIdentity Keeper)> PairAsync() {
		Directory.CreateDirectory(State);
		File.WriteAllText(Path.Combine(root, "target.mkv"), "synthetic duplicate bytes");
		File.WriteAllText(Path.Combine(root, "keeper.mkv"), "synthetic duplicate bytes");
		File.WriteAllText(Settings, "{}");
		await CleanupProtectionPolicyStore.SaveAsync(State, new(), FileIdentity.Capture(Settings));
		return (FileIdentity.Capture(Path.Combine(root, "target.mkv"))!, FileIdentity.Capture(Path.Combine(root, "keeper.mkv"))!);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FinalValidationThroughMutation_ExcludesReviewPolicyAndSettingsWriters(bool quarantine) {
		var (target, keeper) = await PairAsync();
		bool finalCheck = false, removed = false;
		byte[] reviews = File.ReadAllBytes(Reviews), policy = File.ReadAllBytes(Path.Combine(State, CleanupProtectionPolicyStore.FileName));
		var guard = new CleanupProtectionGuard(State) {
			AfterLiveCheckAsync = async (claim, _) => {
				if (claim is null) return;
				finalCheck = true;
				await Assert.ThrowsAnyAsync<IOException>(() => new ReviewStore(Reviews).SaveAsync([
					new() { Identity = target, ManuallyProtected = true, Choice = ReviewChoice.Keep }
				]));
				await Assert.ThrowsAnyAsync<IOException>(() => CleanupProtectionPolicyStore.SaveAsync(State,
					new() { FolderRules = [new() { Directory = root, ProtectAll = true }] }, FileIdentity.Capture(Settings)));
				Assert.ThrowsAny<IOException>(() => { using var lease = JournalWriteLease.Acquire(Settings); });
				Assert.Equal(reviews, File.ReadAllBytes(Reviews));
				Assert.Equal(policy, File.ReadAllBytes(Path.Combine(State, CleanupProtectionPolicyStore.FileName)));
			}
		};
		if (quarantine) {
			var record = await guard.QuarantineExactAsync(target, keeper, Path.Combine(root, "quarantine"));
			Assert.Equal(QuarantineState.Quarantined, record.State);
			Assert.Equal("synthetic duplicate bytes", File.ReadAllText(record.QuarantinePath));
		}
		else {
			await guard.RemoveAsync(target, keeper, claim => {
				Assert.True(finalCheck);
				Assert.ThrowsAny<IOException>(() => { using var lease = JournalWriteLease.Acquire(Reviews); });
				Assert.ThrowsAny<IOException>(() => { using var lease = JournalWriteLease.Acquire(Path.Combine(State, CleanupProtectionPolicyStore.FileName)); });
				removed = true;
				File.Delete(claim);
			});
			Assert.True(removed);
		}
		Assert.True(finalCheck);
		Assert.False(File.Exists(target.Path));
		Assert.True(keeper.MatchesCurrent(true));
		// Every lease is released after the mutation, including the settings anchor.
		await new ReviewStore(Reviews).SaveAsync([new() { Identity = keeper, Choice = ReviewChoice.Keep }]);
		await CleanupProtectionPolicyStore.SaveAsync(State, new(), FileIdentity.Capture(Settings));
		using var released = JournalWriteLease.Acquire(Settings);
	}

	[Theory]
	[InlineData(false, ReviewChoice.Keep, false)]
	[InlineData(false, ReviewChoice.None, true)]
	[InlineData(false, ReviewChoice.Ignore, false)]
	[InlineData(false, ReviewChoice.RejectGroup, false)]
	[InlineData(true, ReviewChoice.Keep, false)]
	[InlineData(true, ReviewChoice.None, true)]
	[InlineData(true, ReviewChoice.Ignore, false)]
	[InlineData(true, ReviewChoice.RejectGroup, false)]
	public async Task DecisionCommittedBeforeFinalReservation_IsRetainedThroughRollback(bool quarantine, ReviewChoice choice, bool protect) {
		var (target, keeper) = await PairAsync();
		bool wrote = false;
		var guard = new CleanupProtectionGuard(State) {
			BeforeLiveCheckAsync = async (claim, _) => {
				if (claim is null) return;
				await new ReviewStore(Reviews).SaveAsync([new() { Identity = target, Choice = choice, ManuallyProtected = protect }]);
				wrote = true;
			}
		};
		await Assert.ThrowsAnyAsync<IOException>(async () => {
			if (quarantine) await guard.QuarantineExactAsync(target, keeper, Path.Combine(root, "quarantine"), [target, keeper]);
			else await guard.RemoveAsync(target, keeper, _ => throw new InvalidOperationException("Protected mutation must not run."), [target, keeper]);
		});
		Assert.True(wrote);
		Assert.Equal("synthetic duplicate bytes", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent(true));
		var store = new ReviewStore(Reviews);
		await store.LoadAsync();
		var saved = Assert.Single(store.Records);
		Assert.Equal(choice, saved.Choice);
		Assert.Equal(protect, saved.ManuallyProtected);
		Assert.True(saved.Identity.MatchesCurrent(true));
		Assert.Empty(Directory.EnumerateFiles(root, "*.holding", SearchOption.AllDirectories));
		Assert.True((await guard.InspectAsync(FileIdentity.Capture(target.Path)!)).IsProtected);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SavedSettingsChangeBeforeFinalReservation_InvalidatesProjectionAndRetainsMedia(bool quarantine) {
		var (target, keeper) = await PairAsync();
		var guard = new CleanupProtectionGuard(State) {
			BeforeLiveCheckAsync = (claim, _) => {
				if (claim is not null) { using var lease = JournalWriteLease.Acquire(Settings); File.WriteAllText(Settings, "{\"newProtection\":true}"); }
				return Task.CompletedTask;
			}
		};
		await Assert.ThrowsAsync<InvalidDataException>(async () => {
			if (quarantine) await guard.QuarantineExactAsync(target, keeper, Path.Combine(root, "quarantine"));
			else await guard.RemoveAsync(target, keeper, File.Delete);
		});
		Assert.Equal("synthetic duplicate bytes", File.ReadAllText(target.Path));
		Assert.True(keeper.MatchesCurrent(true));
		Assert.Equal("{\"newProtection\":true}", File.ReadAllText(Settings));
	}

	public void Dispose() { try { Directory.Delete(root, true); } catch { } }
}
