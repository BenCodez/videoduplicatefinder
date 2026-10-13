using VDF.Core.Review;

namespace VDF.Core.Tests.Review;

public sealed class FileIdentityTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vdf-identity-" + Guid.NewGuid().ToString("N"));
    public FileIdentityTests() => Directory.CreateDirectory(_directory);
    private string FilePath(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task Capture_ReturnsStableIdentityAndFullSignature() {
        string path = FilePath("media.bin");
        await File.WriteAllTextAsync(path, "media bytes");
        var identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
        Assert.Equal(path, identity.Path);
        Assert.Equal(11, identity.Length);
        Assert.Equal(64, identity.Sha256.Length);
        Assert.NotEmpty(identity.StableFileId);
        Assert.Equal(DateTimeKind.Utc, identity.LastWriteUtc.Kind);
        Assert.True(identity.MatchesCurrent());
        Assert.True(identity.MatchesCurrent(forceHash: true));
        Assert.Equal(identity, await FileIdentity.CaptureAsync(path));
    }

    [Fact]
    public async Task ReplacementWithCopiedTimestamps_DoesNotMatchOldIdentity() {
        string path = FilePath("media.bin");
        await File.WriteAllTextAsync(path, "old bytes");
        var old = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
        string replacement = FilePath("replacement.bin");
        await File.WriteAllTextAsync(replacement, "new bytes");
        File.SetLastWriteTimeUtc(replacement, old.LastWriteUtc);
        File.SetCreationTimeUtc(replacement, old.CreationUtc);
        File.Move(replacement, path, true);
        Assert.False(old.MatchesCurrent());
        Assert.False(old.MatchesCurrent(forceHash: true));
    }

    [Fact]
    public async Task ByteIdenticalReplacement_IsStillANewFile() {
        string path = FilePath("media.bin");
        await File.WriteAllTextAsync(path, "same bytes");
        var old = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
        string replacement = FilePath("replacement.bin");
        await File.WriteAllTextAsync(replacement, "same bytes");
        File.SetLastWriteTimeUtc(replacement, old.LastWriteUtc);
        File.SetCreationTimeUtc(replacement, old.CreationUtc);
        File.Move(replacement, path, true);
        Assert.False(old.MatchesCurrent());
        Assert.Equal(old.Sha256, FileIdentity.Capture(path)!.Sha256);
        Assert.NotEqual(old.StableFileId, FileIdentity.Capture(path)!.StableFileId);
    }

    [Fact]
    public async Task InPlaceChangeWithRestoredModificationTime_InvalidatesCachedDigest() {
        string path = FilePath("media.bin");
        await File.WriteAllTextAsync(path, "old bytes");
        var old = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
        await File.WriteAllTextAsync(path, "new bytes");
        File.SetLastWriteTimeUtc(path, old.LastWriteUtc);
        Assert.False(old.MatchesCurrent());
        Assert.NotEqual(old.Sha256, FileIdentity.Capture(path)!.Sha256);
    }

    [Fact]
    public void Capture_MissingFileAndDirectory_ReturnNull() {
        Assert.Null(FileIdentity.Capture(FilePath("missing.bin")));
        Assert.Null(FileIdentity.Capture(_directory));
    }

    [Fact]
    public async Task Capture_RejectsSymbolicFileAndDirectoryAncestors() {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks may require administrator rights.
        string real = FilePath("real");
        Directory.CreateDirectory(real);
        string path = Path.Combine(real, "media.bin");
        await File.WriteAllTextAsync(path, "bytes");
        string link = FilePath("linked.bin");
        File.CreateSymbolicLink(link, path);
        Assert.Null(FileIdentity.Capture(link));
        string linkedDirectory = FilePath("linked-directory");
        Directory.CreateSymbolicLink(linkedDirectory, real);
        Assert.Null(FileIdentity.Capture(Path.Combine(linkedDirectory, "media.bin")));
    }

    [Fact]
    public async Task CaptureAsync_HonorsCancellation() {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileIdentity.CaptureAsync(FilePath("media.bin"), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => FileIdentity.Capture(FilePath("media.bin"), cancellation.Token));
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
