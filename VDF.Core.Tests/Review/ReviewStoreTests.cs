using VDF.Core.Review;
using VDF.Core.Selection;

namespace VDF.Core.Tests.Review;

public sealed class ReviewStoreTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vdf-review-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "state", "review.json");
    public ReviewStoreTests() => Directory.CreateDirectory(_directory);

    private async Task<ReviewRecord> NewRecordAsync(string name = "media.bin", string reason = "manual") {
        string path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, "sample bytes");
        return new() {
            Identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(path)), Choice = ReviewChoice.Keep,
            ManuallyProtected = true, AutomaticallySelected = true, IsKeeper = true,
            Profile = "QualityFirst", Reason = reason, UpdatedUtc = DateTime.UtcNow
        };
    }

    [Fact]
    public async Task SaveLoad_RoundTripsDecisionsSeparatelyFromScanDatabase() {
        var record = await NewRecordAsync();
        string scanDatabase = Path.Combine(_directory, "ScannedFiles.db");
        await File.WriteAllTextAsync(scanDatabase, "existing scan database");
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        var reloaded = new ReviewStore(StorePath);
        await reloaded.LoadAsync();
        var loaded = Assert.Single(reloaded.Records);
        Assert.Equal(record.Identity, loaded.Identity);
        Assert.Equal(ReviewChoice.Keep, loaded.Choice);
        Assert.True(loaded.ManuallyProtected);
        Assert.True(loaded.AutomaticallySelected);
        Assert.True(loaded.IsKeeper);
        Assert.Equal(record.Profile, loaded.Profile);
        Assert.Equal(record.Reason, loaded.Reason);
        Assert.NotNull(reloaded.Find(record.Identity.Path));
        Assert.Contains("\"version\": 1", await File.ReadAllTextAsync(StorePath));
        Assert.Equal("existing scan database", await File.ReadAllTextAsync(scanDatabase));
    }

    [Fact]
    public async Task Find_DoesNotApplyRemovalToReplacement() {
        var record = await NewRecordAsync();
        record.Choice = ReviewChoice.Remove;
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        string newPath = Path.Combine(_directory, "replacement.bin");
        await File.WriteAllTextAsync(newPath, "sample bytes");
        File.SetLastWriteTimeUtc(newPath, record.Identity.LastWriteUtc);
        File.SetCreationTimeUtc(newPath, record.Identity.CreationUtc);
        File.Move(newPath, record.Identity.Path, true);
        Assert.Null(store.Find(record.Identity.Path));
        Assert.Single(store.Records); // Retained as history, never silently applied.
    }

    [Fact]
    public async Task PathChange_InvalidatesDecisionWithoutHashRelinking() {
        var record = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        string renamed = Path.Combine(_directory, "renamed.bin");
        File.Move(record.Identity.Path, renamed);
        Assert.Null(store.Find(renamed));
        Assert.Null(store.Find(record.Identity.Path));
    }

    [Fact]
    public async Task OfflineProtection_IsStoredButNotAppliedWithoutValidation() {
        var record = await NewRecordAsync();
        File.Delete(record.Identity.Path);
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        await store.LoadAsync();
        Assert.True(Assert.Single(store.Records).ManuallyProtected);
        Assert.Null(store.Find(record.Identity.Path));
    }

    [Fact]
    public async Task CorruptPrimary_RecoversLatestMirroredDecision() {
        var first = await NewRecordAsync(reason: "first decision");
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { first });
        first.Reason = "new decision";
        await store.SaveAsync(new[] { first });
        await File.WriteAllTextAsync(StorePath, "{ incomplete");
        var recovered = new ReviewStore(StorePath);
        await recovered.LoadAsync();
        Assert.Equal("new decision", Assert.Single(recovered.Records).Reason);
        await recovered.SaveAsync(recovered.Records);
        await new ReviewStore(StorePath).LoadAsync();
        Assert.Contains("new decision", await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task FirstGeneration_HasRecoverableBackupAndUnknownShapeIsInvalid() {
        var record = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        await File.WriteAllTextAsync(StorePath, "{}");
        await store.LoadAsync();
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task UnsupportedFutureSchema_IsPreservedAndNotDowngraded() {
        var record = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        const string future = "{\"version\":999,\"records\":[]}";
        await File.WriteAllTextAsync(StorePath, future);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(new[] { record }));
        Assert.Equal(future, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task Cancellation_LeavesPreviousGenerationIntact() {
        var record = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        string before = await File.ReadAllTextAsync(StorePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        record.Choice = ReviewChoice.Remove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new[] { record }, cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(ReviewChoice.Keep, Assert.Single(store.Records).Choice);
    }

    [Fact]
    public async Task Update_WaitsForOtherStoreWriterAndMergesItsLatestProtection() {
        var initial = await NewRecordAsync("initial.bin", "old decision");
        var protection = await NewRecordAsync("protected.bin", "saved elsewhere");
        protection.Choice = ReviewChoice.Ignore;
        var added = await NewRecordAsync("added.bin", "this update");
        var writer = new ReviewStore(StorePath);
        // A different spelling must still share the normalized journal gate.
        var updater = new ReviewStore(Path.Combine(_directory, "state", ".", "review.json"));
        await writer.SaveAsync(new[] { initial });
        await updater.LoadAsync();
        initial.Reason = "latest decision";

        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWriter = new ManualResetEventSlim();
        DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (SafePaths.Same(destination, StorePath + ".bak")) {
                writerStarted.TrySetResult();
                if (!releaseWriter.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the journal writer.");
            }
        };
        Task write;
        try { write = Task.Run(() => writer.SaveAsync(new[] { initial, protection })); }
        finally { DurableFileOperations.FileMovedObserver.Value = null; }
        Task? update = null;
        bool transformed = false;
        try {
            await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            update = updater.UpdateAsync(current => {
                transformed = true;
                Assert.Equal("latest decision", current.Single(r => r.Identity.Path == initial.Identity.Path).Reason);
                Assert.Equal(ReviewChoice.Ignore, current.Single(r => r.Identity.Path == protection.Identity.Path).Choice);
                return current.Concat(new[] { added });
            });
            Assert.False(update.IsCompleted);
            Assert.False(transformed);
        }
        finally {
            releaseWriter.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            if (update is not null) await update.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var reloaded = new ReviewStore(StorePath);
        await reloaded.LoadAsync();
        Assert.Equal(3, reloaded.Records.Count);
        var retained = reloaded.Records.Single(r => r.Identity.Path == protection.Identity.Path);
        Assert.True(retained.ManuallyProtected);
        Assert.Equal(ReviewChoice.Ignore, retained.Choice);
        Assert.Equal("latest decision", reloaded.Records.Single(r => r.Identity.Path == initial.Identity.Path).Reason);
        Assert.True(transformed);
        Assert.Equal(await File.ReadAllTextAsync(StorePath), await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWithSnapshot_ReturnsItsCommittedGenerationAfterForeignDecisionAndSameStoreReload(bool cleanupReload) {
        var initial = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { initial });
        DateTime committedUtc = DateTime.UtcNow;

        var generation = await store.UpdateWithSnapshotAsync(current => {
            var record = Assert.Single(current);
            record.Choice = ReviewChoice.Remove;
            record.ManuallyProtected = false;
            record.AutomaticallySelected = false;
            record.IsKeeper = false;
            record.Profile = "ExplicitLocalReview";
            record.Reason = "committed local removal";
            record.UpdatedUtc = committedUtc;
            return current;
        });
        var committed = Assert.Single(generation);
        var writer = new ReviewStore(StorePath);
        await writer.UpdateAsync(current => {
            var record = Assert.Single(current);
            record.Choice = ReviewChoice.Keep;
            record.ManuallyProtected = true;
            record.Reason = "newer foreign protection";
            record.UpdatedUtc = committedUtc.AddSeconds(1);
            return current;
        });
        if (cleanupReload) await store.LoadForCleanupAsync();
        else await store.LoadAsync();

        Assert.Equal(initial.Identity, committed.Identity);
        Assert.Equal(ReviewChoice.Remove, committed.Choice);
        Assert.False(committed.ManuallyProtected);
        Assert.False(committed.AutomaticallySelected);
        Assert.False(committed.IsKeeper);
        Assert.Equal("ExplicitLocalReview", committed.Profile);
        Assert.Equal("committed local removal", committed.Reason);
        Assert.Equal(committedUtc, committed.UpdatedUtc);
        var reloaded = Assert.Single(store.Records);
        Assert.NotSame(committed, reloaded);
        Assert.Equal(ReviewChoice.Keep, reloaded.Choice);
        Assert.True(reloaded.ManuallyProtected);
        Assert.Equal("newer foreign protection", reloaded.Reason);
        Assert.Equal(committedUtc.AddSeconds(1), reloaded.UpdatedUtc);
    }

    [Fact]
    public async Task UpdateWithSnapshot_ReturnedRecordsCannotMutatePublishedCacheOrDurableGeneration() {
        var initial = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { initial });
        var generation = await store.UpdateWithSnapshotAsync(current => {
            Assert.Single(current).Reason = "committed snapshot";
            return current;
        });
        string primary = await File.ReadAllTextAsync(StorePath);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        var returned = Assert.Single(generation);
        Assert.NotSame(returned, Assert.Single(store.Records));

        returned.Identity = returned.Identity with { Path = Path.Combine(_directory, "unrelated.bin") };
        returned.Choice = ReviewChoice.Remove;
        returned.ManuallyProtected = false;
        returned.AutomaticallySelected = false;
        returned.IsKeeper = false;
        returned.Profile = "mutated returned record";
        returned.Reason = "mutated returned record";
        returned.UpdatedUtc = initial.UpdatedUtc.AddDays(1);

        var cached = Assert.Single(store.Records);
        Assert.Equal(initial.Identity, cached.Identity);
        Assert.Equal(ReviewChoice.Keep, cached.Choice);
        Assert.True(cached.ManuallyProtected);
        Assert.True(cached.AutomaticallySelected);
        Assert.True(cached.IsKeeper);
        Assert.Equal(initial.Profile, cached.Profile);
        Assert.Equal("committed snapshot", cached.Reason);
        Assert.Equal(initial.UpdatedUtc, cached.UpdatedUtc);
        Assert.Equal(primary, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
        await new ReviewStore(StorePath).LoadForCleanupAsync();
        Assert.Equal(primary, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task Update_CorruptPrimaryPreservesBothGenerationsWithoutRunningTransform() {
        var record = await NewRecordAsync();
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { record });
        const string malformed = "{ incomplete primary";
        await File.WriteAllTextAsync(StorePath, malformed);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        bool transformed = false;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.UpdateAsync(current => {
            transformed = true;
            return current;
        }));
        Assert.False(transformed);
        Assert.Equal(malformed, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
        Assert.True(Assert.Single(store.Records).ManuallyProtected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_MissingPrimaryFailsWithoutCreatingIt(bool keepBackup) {
        var store = new ReviewStore(StorePath);
        string? backup = null;
        if (keepBackup) {
            await store.SaveAsync(new[] { await NewRecordAsync() });
            backup = await File.ReadAllTextAsync(StorePath + ".bak");
            File.Delete(StorePath);
        }
        bool transformed = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(current => {
            transformed = true;
            return current;
        }));
        Assert.False(transformed);
        Assert.False(File.Exists(StorePath));
        if (keepBackup) Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
        else Assert.False(File.Exists(StorePath + ".bak"));
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("manuallyProtected")]
    [InlineData("identity")]
    public async Task Update_MissingRequiredRecordFieldPreservesPrimary(string field) {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(StorePath))!;
        document["records"]![0]!.AsObject().Remove(field);
        string malformed = document.ToJsonString();
        await File.WriteAllTextAsync(StorePath, malformed);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.UpdateAsync(current => current));
        Assert.Equal(malformed, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task Update_UnsupportedSchemaPreservesPrimaryAndBackup() {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        const string future = "{\"version\":999,\"records\":[]}";
        await File.WriteAllTextAsync(StorePath, future);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateAsync(current => current));
        Assert.Equal(future, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task Save_FreezeBeforeWaitingRetainsExplicitReplacementContract() {
        var record = await NewRecordAsync();
        var replacement = await NewRecordAsync("replacement.bin", "frozen replacement");
        var writer = new ReviewStore(StorePath);
        var replacingStore = new ReviewStore(StorePath);
        await writer.SaveAsync(new[] { record });
        var transformStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTransform = new ManualResetEventSlim();
        Task writerTask = Task.Run(() => writer.UpdateAsync(current => {
            transformStarted.TrySetResult();
            if (!releaseTransform.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the review transform.");
            return current;
        }));
        Task? replacementTask = null;
        try {
            await transformStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            replacementTask = replacingStore.SaveAsync(new[] { replacement });
            Assert.False(replacementTask.IsCompleted);
            replacement.Reason = "mutated after save started";
            replacement.ManuallyProtected = false;
        }
        finally {
            releaseTransform.Set();
            await writerTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (replacementTask is not null) await replacementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var reloaded = new ReviewStore(StorePath);
        await reloaded.LoadAsync();
        var saved = Assert.Single(reloaded.Records);
        Assert.Equal(replacement.Identity.Path, saved.Identity.Path);
        Assert.Equal("frozen replacement", saved.Reason);
        Assert.True(saved.ManuallyProtected);
    }

    [Fact]
    public async Task Update_FreezesCallbackRecordsBeforeDurableWriteAndCachePublication() {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        ReviewRecord? retained = null;
        DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (SafePaths.Same(destination, StorePath + ".bak")) {
                Assert.NotNull(retained);
                retained.Reason = "mutated while writing";
                retained.ManuallyProtected = false;
            }
        };
        try {
            await store.UpdateAsync(current => {
                retained = Assert.Single(current);
                retained.Reason = "frozen update";
                return current;
            });
        }
        finally { DurableFileOperations.FileMovedObserver.Value = null; }
        var cached = Assert.Single(store.Records);
        Assert.Equal("frozen update", cached.Reason);
        Assert.True(cached.ManuallyProtected);
        var reloaded = new ReviewStore(StorePath);
        await reloaded.LoadAsync();
        Assert.Equal("frozen update", Assert.Single(reloaded.Records).Reason);
        Assert.True(Assert.Single(reloaded.Records).ManuallyProtected);
    }

    [Fact]
    public async Task Initialize_CreatesMirroredEmptyJournalAndPreservesLaterPopulatedJournal() {
        var initializer = new ReviewStore(StorePath);
        await initializer.InitializeAsync();
        Assert.Empty(initializer.Records);
        Assert.True(File.Exists(StorePath));
        Assert.Equal(await File.ReadAllTextAsync(StorePath), await File.ReadAllTextAsync(StorePath + ".bak"));

        await new ReviewStore(StorePath).SaveAsync(new[] { await NewRecordAsync() });
        string primary = await File.ReadAllTextAsync(StorePath);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        await initializer.InitializeAsync();
        Assert.True(Assert.Single(initializer.Records).ManuallyProtected);
        Assert.Equal(primary, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task Initialize_WaitsForFirstGenerationWriterAndPreservesItsRecords() {
        var record = await NewRecordAsync();
        var initializer = new ReviewStore(StorePath);
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWriter = new ManualResetEventSlim();
        DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (SafePaths.Same(destination, StorePath + ".bak")) {
                writerStarted.TrySetResult();
                if (!releaseWriter.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the first-generation writer.");
            }
        };
        Task write;
        try { write = Task.Run(() => new ReviewStore(StorePath).SaveAsync(new[] { record })); }
        finally { DurableFileOperations.FileMovedObserver.Value = null; }
        Task? initialization = null;
        try {
            await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            initialization = initializer.InitializeAsync();
            Assert.False(initialization.IsCompleted);
        }
        finally {
            releaseWriter.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            if (initialization is not null) await initialization.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(record.Identity, Assert.Single(initializer.Records).Identity);
        var reloaded = new ReviewStore(StorePath);
        await reloaded.LoadAsync();
        Assert.True(Assert.Single(reloaded.Records).ManuallyProtected);
        Assert.Equal(await File.ReadAllTextAsync(StorePath), await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task Initialize_BackupOnlyFailsWithoutCreatingPrimary() {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        File.Delete(StorePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReviewStore(StorePath).InitializeAsync());
        Assert.False(File.Exists(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task Initialize_CorruptPrimaryFailsWithoutRecoveringOrOverwriting() {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        const string malformed = "{ incomplete primary";
        await File.WriteAllTextAsync(StorePath, malformed);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new ReviewStore(StorePath).InitializeAsync());
        Assert.Equal(malformed, await File.ReadAllTextAsync(StorePath));
        Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
    }

    [Fact]
    public async Task BusyLease_BlocksEveryReviewWriterAndLeavesGenerationsIntact() {
        var store = new ReviewStore(StorePath);
        await store.SaveAsync(new[] { await NewRecordAsync() });
        string primary = await File.ReadAllTextAsync(StorePath);
        string backup = await File.ReadAllTextAsync(StorePath + ".bak");
        using (JournalWriteLease.Acquire(StorePath)) {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Array.Empty<ReviewRecord>()));
            await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(_ => Array.Empty<ReviewRecord>()));
            await Assert.ThrowsAsync<IOException>(() => new ReviewStore(StorePath).InitializeAsync());
            Assert.Equal(primary, await File.ReadAllTextAsync(StorePath));
            Assert.Equal(backup, await File.ReadAllTextAsync(StorePath + ".bak"));
        }
        // A failed acquisition must release the process gate for later writers.
        await store.UpdateAsync(current => current);
        Assert.True(Assert.Single(store.Records).ManuallyProtected);
    }

    [Fact]
    public void Lease_DirectoryOrSymbolicLinkIsRejectedWithoutTouchingTarget() {
        string path = Path.Combine(_directory, "unsafe.json");
        Directory.CreateDirectory(path + ".lock");
        Assert.Throws<IOException>(() => JournalWriteLease.Acquire(path));
        Directory.Delete(path + ".lock");
        if (OperatingSystem.IsWindows()) return; // Symlink creation needs privileges on Windows.
        string target = Path.Combine(_directory, "untouched.bin");
        File.WriteAllText(target, "do not overwrite");
        File.CreateSymbolicLink(path + ".lock", target);
        Assert.Throws<IOException>(() => JournalWriteLease.Acquire(path));
        Assert.Equal("do not overwrite", File.ReadAllText(target));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task SelectionPlan_PersistsExplanationsButDoesNotTouchFiles() {
        var record = await NewRecordAsync();
        string path = Path.Combine(_directory, "plans", "last-plan.json");
        var plan = new SelectionPlan {
            Profile = SelectionProfile.NewestFirst, EstimatedSavings = record.Identity.Length,
            Decisions = new() { new() {
                Path = record.Identity.Path, GroupId = Guid.NewGuid(), RemoveCandidate = true,
                RequiresReview = true, Reason = "Newest keeper selected using modification UTC."
            } }
        };
        await SelectionPlanStore.SaveAsync(path, plan);
        var loaded = Assert.IsType<SelectionPlan>(await SelectionPlanStore.LoadAsync(path));
        Assert.Equal(plan.Profile, loaded.Profile);
        Assert.Equal(plan.Decisions[0].Reason, Assert.Single(loaded.Decisions).Reason);
        Assert.True(File.Exists(record.Identity.Path));
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
