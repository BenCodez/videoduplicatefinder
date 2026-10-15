using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using VDF.Core.Utils;

namespace VDF.Core.Review;

/// <summary>
/// A conservative identity for one regular file at one path. File IDs and change
/// stamps prevent an old review from applying to a replacement with copied dates.
/// The first capture reads all bytes; subsequent captures can reuse a digest only
/// when the filesystem supplies an unchanged, reliable change stamp.
/// </summary>
public sealed record FileIdentity {
    public string Path { get; init; } = string.Empty;
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime CreationUtc { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string StableFileId { get; init; } = string.Empty;
    public string ChangeStamp { get; init; } = string.Empty;

    public static FileIdentity? Capture(string path) => Capture(path, CancellationToken.None);

    /// <summary>No-hash token for anchoring later Capture; never use it to authorize deletion.</summary>
    public static FileSnapshot? CaptureSnapshot(string path) => IdentityReader.CaptureSnapshot(path);

    public static FileIdentity? Capture(string path, CancellationToken cancellationToken) =>
        IdentityReader.Capture(path, false, cancellationToken);

    public static Task<FileIdentity?> CaptureAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(path, cancellationToken), cancellationToken);

    public bool MatchesCurrent() => MatchesCurrent(false);

    /// <summary>Destructive callers can bypass digest memoization for a full byte validation.</summary>
    public bool MatchesCurrent(bool forceHash) => IdentityReader.MatchesCurrent(this, forceHash);

    internal bool Matches(FileIdentity? other) => other is not null &&
        PathComparer.ForCurrentPlatform.Equals(Path, other.Path) && SameFileSnapshot(other);

    internal bool SameFileSnapshot(FileIdentity other) =>
        StableFileId == other.StableFileId && Length == other.Length &&
        LastWriteUtc == other.LastWriteUtc && CreationUtc == other.CreationUtc &&
        ChangeStamp == other.ChangeStamp && Sha256 == other.Sha256;

    // A rename may change ctime (and platforms without birth time expose ctime as
    // CreationTime). After a private, journaled source claim we verify the actual
    // file ID and full bytes, without requiring the pre-rename change stamp.
    internal bool MatchesRelocated(FileIdentity? other) => other is not null &&
        StableFileId == other.StableFileId && Length == other.Length &&
        LastWriteUtc == other.LastWriteUtc && Sha256 == other.Sha256;

    internal bool HasSameBytes(FileIdentity other) => Length == other.Length && Sha256 == other.Sha256;

    internal bool IsWellFormed() {
        try {
            return Length >= 0 && !string.IsNullOrWhiteSpace(Path) &&
                System.IO.Path.IsPathFullyQualified(Path) &&
                PathComparer.ForCurrentPlatform.Equals(Path, SafePaths.Normalize(Path, true)) &&
                LastWriteUtc.Kind == DateTimeKind.Utc && CreationUtc.Kind == DateTimeKind.Utc &&
                !string.IsNullOrWhiteSpace(StableFileId) && ChangeStamp is not null && Sha256 is not null && Sha256.Length == 64 &&
                System.Linq.Enumerable.All(Sha256, Uri.IsHexDigit);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}

internal static partial class IdentityReader {
    private sealed record Snapshot(string Path, long Length, DateTime Modified, DateTime Created,
        string FileId, string ChangeStamp);
    private sealed record CachedDigest(Snapshot Snapshot, string Sha256);
    private sealed record TrustedSnapshot(Snapshot Snapshot);
    private static readonly ConcurrentDictionary<string, CachedDigest> DigestCache =
        new(PathComparer.ForCurrentPlatform);
    // A GUI may hold far more files than the bounded path cache. Trusted captures
    // retain their validated snapshot only while the immutable identity is alive.
    private static readonly ConditionalWeakTable<FileIdentity, TrustedSnapshot> TrustedIdentities = new();
    private static long _hashComputationCount;
    internal static readonly AsyncLocal<Action<string>?> HashStartedObserver = new();
    internal static long HashComputationCount => Volatile.Read(ref _hashComputationCount);
    internal static void ClearMemoizedDigests() => DigestCache.Clear();

    internal static bool IsLinuxRegularFile(string path) =>
        LinuxFileSystem.TryRead(path, out var stat) && LinuxFileSystem.IsRegular(stat);

    internal static bool TryGetLinuxFileId(string path, out string fileId, bool followLinks = false) =>
        LinuxFileSystem.TryGetFileId(path, out fileId, followLinks);

    internal static bool MatchesCurrentSnapshot(FileIdentity identity) =>
        CaptureSnapshot(identity.Path)?.Matches(identity) == true;

    internal static FileSnapshot? CaptureSnapshot(string path) {
        try {
            string fullPath = SafePaths.Normalize(path);
            SafePaths.RequireRegularFile(fullPath);
            var snapshot = ReadPathSnapshot(fullPath);
            if (snapshot is null || snapshot.ChangeStamp.Length == 0) return null;
            using var stream = OpenRead(fullPath);
            if (!HandleMatches(stream.SafeFileHandle, snapshot)) return null;
            SafePaths.RequireRegularFile(fullPath);
            if (ReadPathSnapshot(fullPath) != snapshot || !HandleMatches(stream.SafeFileHandle, snapshot)) return null;
            return new() {
                Path = fullPath, Length = snapshot.Length, LastWriteUtc = snapshot.Modified,
                CreationUtc = snapshot.Created, StableFileId = snapshot.FileId, ChangeStamp = snapshot.ChangeStamp
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException) {
            return null;
        }
    }

    internal static bool MatchesCurrent(FileIdentity identity, bool forceHash) {
        if (forceHash) return identity.Matches(Capture(identity.Path, true, CancellationToken.None));
        if (MatchesTrustedSnapshot(identity) is { } matched) return matched;
        var current = Capture(identity.Path, false, CancellationToken.None);
        if (!identity.Matches(current)) return false;
        if (current!.ChangeStamp.Length != 0 && TrustedIdentities.TryGetValue(current, out var snapshotTrust))
            TrustedIdentities.GetValue(identity, _ => snapshotTrust);
        return true;
    }

    // null means this object has no in-process content proof with a reliable
    // native stamp. Destructive preflight must force a hash in that case.
    internal static bool? MatchesTrustedSnapshot(FileIdentity identity) {
        if (TrustedIdentities.TryGetValue(identity, out var trusted) && trusted.Snapshot.ChangeStamp.Length != 0) {
            try {
                SafePaths.RequireRegularFile(identity.Path);
                var snapshot = ReadPathSnapshot(identity.Path);
                if (snapshot != trusted.Snapshot) return false;
                using var stream = OpenRead(identity.Path);
                if (!HandleMatches(stream.SafeFileHandle, snapshot)) return false;
                SafePaths.RequireRegularFile(identity.Path);
                return ReadPathSnapshot(identity.Path) == snapshot && HandleMatches(stream.SafeFileHandle, snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException) {
                return false;
            }
        }
        return null;
    }

    internal static FileIdentity? Capture(string path, bool forceHash, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        try {
            string fullPath = SafePaths.Normalize(path);
            SafePaths.RequireRegularFile(fullPath);
            var before = ReadPathSnapshot(fullPath);
            if (before is null) return null;
            using var stream = OpenRead(fullPath);
            if (!HandleMatches(stream.SafeFileHandle, before)) return null;

            string digest;
            if (!forceHash && before.ChangeStamp.Length != 0 &&
                DigestCache.TryGetValue(fullPath, out var cached) && cached.Snapshot == before) {
                digest = cached.Sha256;
            }
            else {
                Interlocked.Increment(ref _hashComputationCount);
                HashStartedObserver.Value?.Invoke(fullPath);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try {
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) {
                        ct.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, count);
                    }
                    digest = Convert.ToHexString(hash.GetHashAndReset());
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            ct.ThrowIfCancellationRequested();
            SafePaths.RequireRegularFile(fullPath);
            if (ReadPathSnapshot(fullPath) != before || !HandleMatches(stream.SafeFileHandle, before)) return null;
            if (before.ChangeStamp.Length != 0) {
                // Keep only the latest snapshot per path, with a bounded process cache.
                if (DigestCache.Count > 50_000) ClearMemoizedDigests();
                DigestCache[fullPath] = new(before, digest);
            }
            var identity = new FileIdentity {
                Path = fullPath, Length = before.Length, LastWriteUtc = before.Modified,
                CreationUtc = before.Created, StableFileId = before.FileId,
                ChangeStamp = before.ChangeStamp, Sha256 = digest
            };
            if (before.ChangeStamp.Length != 0) TrustedIdentities.GetValue(identity, _ => new(before));
            return identity;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException or
            DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    internal static FileStream OpenRead(string path) => new(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);

    private static Snapshot? ReadPathSnapshot(string path) {
        if (OperatingSystem.IsLinux()) {
            if (!LinuxFileSystem.TryRead(path, out var stat) || !LinuxFileSystem.IsRegular(stat)) return null;
            return LinuxSnapshot(path, stat);
        }
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        if (OperatingSystem.IsWindows()) {
            using var file = OpenRead(path);
            if (!GetFileInformationByHandle(file.SafeFileHandle, out var id)) return null;
            string fileId = $"win:{id.VolumeSerialNumber:X8}:{id.FileIndexHigh:X8}{id.FileIndexLow:X8}";
            if ((id.FileIndexHigh | id.FileIndexLow) == 0) return null;
            string change = GetFileInformationByHandleEx(file.SafeFileHandle, 0, out var basic,
                (uint)Marshal.SizeOf<BasicInformation>()) && basic.ChangeTime > 0
                ? basic.ChangeTime.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            return new(path, ((long)id.FileSizeHigh << 32) | id.FileSizeLow,
                DateTime.FromFileTimeUtc(((long)id.LastWriteTime.High << 32) | id.LastWriteTime.Low),
                DateTime.FromFileTimeUtc(((long)id.CreationTime.High << 32) | id.CreationTime.Low), fileId, change);
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) {
            if (Mono.Unix.Native.Syscall.lstat(path, out var stat) != 0 || !IsRegular(stat.st_mode)) return null;
            return new(path, stat.st_size, info.LastWriteTimeUtc, info.CreationTimeUtc,
                $"unix:{stat.st_dev:X}:{stat.st_ino:X}", $"{stat.st_ctime}:{stat.st_ctime_nsec}");
        }
        return null; // Unsupported file-ID APIs cannot safely validate reviews.
    }

    private static bool HandleMatches(SafeFileHandle handle, Snapshot snapshot) {
        if (OperatingSystem.IsLinux()) {
            return LinuxFileSystem.TryReadHandle(handle, out var linuxStat) && LinuxFileSystem.IsRegular(linuxStat) &&
                LinuxSnapshot(snapshot.Path, linuxStat) == snapshot;
        }
        if (OperatingSystem.IsWindows()) {
            if (!GetFileInformationByHandle(handle, out var id)) return false;
            if (snapshot.FileId != $"win:{id.VolumeSerialNumber:X8}:{id.FileIndexHigh:X8}{id.FileIndexLow:X8}" ||
                snapshot.Length != (((long)id.FileSizeHigh << 32) | id.FileSizeLow)) return false;
            if (snapshot.ChangeStamp.Length == 0) return true;
            return GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>()) &&
                snapshot.ChangeStamp == basic.ChangeTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (Mono.Unix.Native.Syscall.fstat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0 || !IsRegular(stat.st_mode))
            return false;
        return snapshot.FileId == $"unix:{stat.st_dev:X}:{stat.st_ino:X}" && snapshot.Length == stat.st_size &&
            snapshot.ChangeStamp == $"{stat.st_ctime}:{stat.st_ctime_nsec}";
    }

    private static bool IsRegular(Mono.Unix.Native.FilePermissions mode) => ((uint)mode & 0xF000u) == 0x8000u;

    private static Snapshot LinuxSnapshot(string path, LinuxFileSystem.StatxBuffer stat) {
        bool changed = (stat.Mask & LinuxFileSystem.ChangeMask) != 0;
        var creation = (stat.Mask & LinuxFileSystem.BirthMask) != 0 ? stat.Birth : changed ? stat.Change : stat.Modification;
        string changeStamp = changed ? stat.Change.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
            stat.Change.Nanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        return new(path, (long)stat.Size, LinuxFileSystem.TimeUtc(stat.Modification), LinuxFileSystem.TimeUtc(creation),
            LinuxFileSystem.FileId(stat), changeStamp);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation {
        public uint FileAttributes;
        public NativeFileTime CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out BasicInformation information, uint bufferSize);
}
