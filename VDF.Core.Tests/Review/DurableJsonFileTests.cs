using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Selection;

namespace VDF.Core.Tests.Review;

public sealed class DurableJsonFileTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vdf-durable-" + Guid.NewGuid().ToString("N"));
    public DurableJsonFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SuccessfulSave_MirrorsCurrentGenerationBeforeReturning() {
        string path = Path.Combine(_directory, "state", "reviews.json");
        string media = Path.Combine(_directory, "media.bin");
        await File.WriteAllTextAsync(media, "reviewed bytes");
        var identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(media));
        var store = new ReviewStore(path);
        await store.SaveAsync(new[] { new ReviewRecord { Identity = identity, Choice = ReviewChoice.Remove } });
        await store.SaveAsync(new[] { new ReviewRecord {
            Identity = identity, Choice = ReviewChoice.Keep, ManuallyProtected = true, Reason = "New deliberate protection"
        } });
        Assert.Equal(await File.ReadAllTextAsync(path), await File.ReadAllTextAsync(path + ".bak"));
        await File.WriteAllTextAsync(path, "{ corrupt primary");
        var recovered = new ReviewStore(path);
        await recovered.LoadAsync();
        var record = Assert.Single(recovered.Records);
        Assert.True(record.ManuallyProtected);
        Assert.Equal(ReviewChoice.Keep, record.Choice);
        Assert.Equal("New deliberate protection", record.Reason);
    }

    [Fact]
    public async Task CancellationAfterFirstNamespaceCommit_CompletesBothMirrors() {
        string path = Path.Combine(_directory, "state", "reviews.json");
        var store = new ReviewStore(path);
        await store.SaveAsync(Array.Empty<ReviewRecord>());
        string media = Path.Combine(_directory, "media.bin");
        await File.WriteAllTextAsync(media, "reviewed bytes");
        var identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(media));
        using var cancellation = new CancellationTokenSource();
        DurableFileOperations.FileMovedObserver.Value = (_, destination) => {
            if (SafePaths.Same(destination, path + ".bak")) cancellation.Cancel();
        };
        try {
            await store.SaveAsync(new[] { new ReviewRecord { Identity = identity, ManuallyProtected = true } }, cancellation.Token);
        }
        finally { DurableFileOperations.FileMovedObserver.Value = null; }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(Assert.Single(store.Records).ManuallyProtected);
        Assert.Equal(await File.ReadAllTextAsync(path), await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task FreshNestedJournalParent_FlushesEveryContainingAncestorBeforeFileCommit() {
        if (OperatingSystem.IsWindows()) return; // This verifies the Unix directory-fsync contract.
        string first = Path.Combine(_directory, "new-state");
        string second = Path.Combine(first, "nested");
        string path = Path.Combine(second, "reviews.json");
        var flushed = new List<string>();
        DurableFileOperations.DirectoryFlushedObserver.Value = directory => {
            if (SafePaths.Within(directory, _directory)) flushed.Add(directory);
        };
        try { await new ReviewStore(path).SaveAsync(Array.Empty<ReviewRecord>()); }
        finally { DurableFileOperations.DirectoryFlushedObserver.Value = null; }
        Assert.Contains(_directory, flushed);
        Assert.Contains(first, flushed);
        Assert.Contains(second, flushed);
        Assert.True(flushed.IndexOf(_directory) < flushed.IndexOf(first));
        Assert.True(flushed.IndexOf(first) < flushed.IndexOf(second));
        Assert.True(File.Exists(path));
        Assert.Equal(await File.ReadAllTextAsync(path), await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public void FailedAncestorFlush_IsRetriedEvenWhenChildDirectoryAlreadyExists() {
        if (OperatingSystem.IsWindows()) return;
        string child = Path.Combine(_directory, "new-state");
        string path = Path.Combine(child, "reviews.json");
        DurableFileOperations.DirectoryFlushedObserver.Value = directory => {
            if (SafePaths.Same(directory, _directory)) throw new IOException("Synthetic ancestor durability failure.");
        };
        try { Assert.Throws<IOException>(() => SafePaths.EnsureParentDirectory(path)); }
        finally { DurableFileOperations.DirectoryFlushedObserver.Value = null; }
        Assert.True(Directory.Exists(child));
        bool retried = false;
        DurableFileOperations.DirectoryFlushedObserver.Value = directory => {
            if (SafePaths.Same(directory, _directory)) retried = true;
        };
        try { SafePaths.EnsureParentDirectory(path); }
        finally { DurableFileOperations.DirectoryFlushedObserver.Value = null; }
        Assert.True(retried);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AncestorFlushFailure_PreventsAnyCleanupClaimOrCallback() {
        if (OperatingSystem.IsWindows()) return;
        string targetPath = Path.Combine(_directory, "target.bin");
        string keeperPath = Path.Combine(_directory, "keeper.bin");
        await File.WriteAllTextAsync(targetPath, "reviewed source");
        await File.WriteAllTextAsync(keeperPath, "reviewed keeper");
        var target = Assert.IsType<FileIdentity>(FileIdentity.Capture(targetPath));
        var keeper = Assert.IsType<FileIdentity>(FileIdentity.Capture(keeperPath));
        string journal = Path.Combine(_directory, "new-state", "legacy.json");
        bool removed = false;
        DurableFileOperations.DirectoryFlushedObserver.Value = directory => {
            if (SafePaths.Same(directory, _directory)) throw new IOException("Synthetic ancestor durability failure.");
        };
        try {
            await Assert.ThrowsAsync<IOException>(() => new LegacyRemovalService(journal).RemoveAsync(target, keeper, _ => removed = true));
        }
        finally { DurableFileOperations.DirectoryFlushedObserver.Value = null; }
        Assert.False(removed);
        Assert.True(target.MatchesCurrent(true));
        Assert.Empty(Directory.GetFiles(_directory, ".vdf-*.holding"));
        Assert.False(File.Exists(journal));
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
