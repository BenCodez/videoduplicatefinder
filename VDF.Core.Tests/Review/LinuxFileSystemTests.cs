using System.Runtime.InteropServices;
using VDF.Core.Review;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Review;

public sealed class LinuxFileSystemTests {
    [Theory]
    [InlineData(Architecture.Arm, 0x8C000)]
    [InlineData(Architecture.Arm64, 0x8C000)]
    [InlineData(Architecture.X86, 0xB0000)]
    [InlineData(Architecture.X64, 0xB0000)]
    public void DirectoryOpenFlags_MatchLinuxArchitectureUapi(Architecture architecture, int expected) =>
        Assert.Equal(expected, LinuxFileSystem.DirectoryOpenFlags(architecture));

    [Fact]
    public void DirectoryOpenFlags_RejectUnsupportedArchitecture() =>
        Assert.Throws<PlatformNotSupportedException>(() => LinuxFileSystem.DirectoryOpenFlags(Architecture.Wasm));

    [Fact]
    public void DirectoryFlush_RequiresARealDirectoryAndRefusesSymbolicLinks() {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("vdf-directory-flush-").FullName;
        try {
            string child = Directory.CreateDirectory(Path.Combine(directory, "real")).FullName;
            string alias = Path.Combine(directory, "alias");
            Directory.CreateSymbolicLink(alias, child);
            string file = Path.Combine(directory, "file");
            File.WriteAllText(file, "bytes");
            LinuxFileSystem.FlushDirectory(child);
            Assert.Throws<IOException>(() => LinuxFileSystem.FlushDirectory(alias));
            Assert.Throws<IOException>(() => LinuxFileSystem.FlushDirectory(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void StatxLayout_MatchesFixedLinuxKernelAbi() {
        Assert.Equal(16, Marshal.SizeOf<LinuxFileSystem.StatxTimestamp>());
        Assert.Equal(256, Marshal.SizeOf<LinuxFileSystem.StatxBuffer>());
        Assert.Equal(0x00, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Mask)).ToInt32());
        Assert.Equal(0x1C, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Mode)).ToInt32());
        Assert.Equal(0x20, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Inode)).ToInt32());
        Assert.Equal(0x28, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Size)).ToInt32());
        Assert.Equal(0x50, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Birth)).ToInt32());
        Assert.Equal(0x60, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Change)).ToInt32());
        Assert.Equal(0x70, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.Modification)).ToInt32());
        Assert.Equal(0x88, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.DeviceMajor)).ToInt32());
        Assert.Equal(0x8C, Marshal.OffsetOf<LinuxFileSystem.StatxBuffer>(nameof(LinuxFileSystem.StatxBuffer.DeviceMinor)).ToInt32());
    }

    [Fact]
    public async Task NativeStatx_PathAndOpenHandleReportSameRegularFile() {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(Path.GetTempPath(), "vdf-statx-" + Guid.NewGuid().ToString("N"));
        try {
            await File.WriteAllTextAsync(path, "native bytes");
            Assert.True(LinuxFileSystem.TryRead(path, out var byPath));
            Assert.True(LinuxFileSystem.IsRegular(byPath));
            using var stream = File.OpenRead(path);
            Assert.True(LinuxFileSystem.TryReadHandle(stream.SafeFileHandle, out var byHandle));
            Assert.Equal(LinuxFileSystem.FileId(byPath), LinuxFileSystem.FileId(byHandle));
            Assert.Equal(12UL, byPath.Size);
            Assert.Equal(byPath.Change.Seconds, byHandle.Change.Seconds);
            Assert.Equal(byPath.Change.Nanoseconds, byHandle.Change.Nanoseconds);
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc, LinuxFileSystem.TimeUtc(byPath.Modification));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SameFileScanComparison_FollowsSymbolicAliasWhileCleanupIdentityRejectsIt() {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "vdf-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string real = Path.Combine(directory, "real.bin");
        string alias = Path.Combine(directory, "alias.bin");
        try {
            await File.WriteAllTextAsync(real, "media bytes");
            File.CreateSymbolicLink(alias, real);
            Assert.True(HardLinkUtils.AreSameFile(real, alias));
            Assert.True(HardLinkUtils.AreSameFile(alias, real));
            Assert.Null(FileIdentity.Capture(alias));
            Assert.Null(FileIdentity.CaptureSnapshot(alias));
            Assert.True(LinuxFileSystem.TryRead(alias, out var noFollow));
            Assert.False(LinuxFileSystem.IsRegular(noFollow));
            Assert.True(LinuxFileSystem.TryRead(alias, out var followed, followLinks: true));
            Assert.True(LinuxFileSystem.IsRegular(followed));
        }
        finally { Directory.Delete(directory, true); }
    }
}

[CollectionDefinition("Identity hash diagnostics", DisableParallelization = true)]
public sealed class IdentityHashDiagnosticsCollection { }

[Collection("Identity hash diagnostics")]
public sealed class IdentityMemoizationTests {
    [Fact]
    public async Task CheapSnapshot_DoesNotHashAndRejectsAReplacementBeforeFullCapture() {
        string directory = Path.Combine(Path.GetTempPath(), "vdf-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "media.bin");
        try {
            await File.WriteAllTextAsync(path, "snapshot bytes");
            long count = IdentityReader.HashComputationCount;
            var snapshot = Assert.IsType<FileSnapshot>(FileIdentity.CaptureSnapshot(path));
            Assert.True(snapshot.MatchesCurrent());
            Assert.Equal(count, IdentityReader.HashComputationCount);
            string replacement = Path.Combine(directory, "replacement.bin");
            await File.WriteAllTextAsync(replacement, "snapshot bytes");
            File.SetLastWriteTimeUtc(replacement, snapshot.LastWriteUtc);
            File.SetCreationTimeUtc(replacement, snapshot.CreationUtc);
            File.Move(replacement, path, true);
            Assert.False(snapshot.MatchesCurrent());
            Assert.False(snapshot.Matches(Assert.IsType<FileIdentity>(FileIdentity.Capture(path))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HeldIdentity_DoesNotRehashAfterPathCacheEviction_ButDestructiveCheckDoes() {
        string path = Path.Combine(Path.GetTempPath(), "vdf-cache-" + Guid.NewGuid().ToString("N"));
        try {
            await File.WriteAllTextAsync(path, "cache test bytes");
            var identity = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
            if (identity.ChangeStamp.Length == 0) return; // Unsupported change stamps intentionally force hashing.
            IdentityReader.ClearMemoizedDigests();
            long count = IdentityReader.HashComputationCount;
            Assert.True(identity.MatchesCurrent());
            Assert.True(identity.MatchesCurrent());
            Assert.Equal(count, IdentityReader.HashComputationCount);
            Assert.True(identity.MatchesCurrent(forceHash: true));
            Assert.Equal(count + 1, IdentityReader.HashComputationCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UntrustedDeserializedIdentity_ValidatesOnceBeforeAvoidingRepeatedHashes() {
        string path = Path.Combine(Path.GetTempPath(), "vdf-cache-" + Guid.NewGuid().ToString("N"));
        try {
            await File.WriteAllTextAsync(path, "cache test bytes");
            var captured = Assert.IsType<FileIdentity>(FileIdentity.Capture(path));
            if (captured.ChangeStamp.Length == 0) return;
            // A record copy is a new immutable object, like a deserialized identity.
            var untrusted = captured with { };
            IdentityReader.ClearMemoizedDigests();
            long count = IdentityReader.HashComputationCount;
            Assert.True(untrusted.MatchesCurrent());
            Assert.Equal(count + 1, IdentityReader.HashComputationCount);
            IdentityReader.ClearMemoizedDigests();
            Assert.True(untrusted.MatchesCurrent());
            Assert.Equal(count + 1, IdentityReader.HashComputationCount);
        }
        finally { File.Delete(path); }
    }
}
