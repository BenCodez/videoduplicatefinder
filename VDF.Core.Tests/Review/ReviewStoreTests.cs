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
