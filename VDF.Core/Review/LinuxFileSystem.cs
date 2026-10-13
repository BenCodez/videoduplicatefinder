using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VDF.Core.Review;

/// <summary>
/// Uses Linux's fixed statx UAPI, rather than libc's architecture-dependent stat
/// struct or a glibc-only native package. Works with both glibc and Alpine musl.
/// Missing statx support fails closed. Layout follows include/uapi/linux/stat.h.
/// </summary>
internal static partial class LinuxFileSystem {
    private const int AtCurrentDirectory = -100;
    private const int AtNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const int ForceSync = 0x2000;
    private const uint BasicAndBirthTime = 0x7FF | 0x800;
    internal const uint TypeMask = 0x1, ModificationMask = 0x40, ChangeMask = 0x80,
        InodeMask = 0x100, SizeMask = 0x200, BirthMask = 0x800;
    private const uint RequiredMask = TypeMask | ModificationMask | InodeMask | SizeMask;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct StatxTimestamp {
        public long Seconds;
        public uint Nanoseconds;
        private int _reserved;
    }

    // The kernel always writes 256 bytes, including reserved space. Explicit
    // offsets avoid native long/time_t/struct-stat layout differences on ARM.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct StatxBuffer {
        [FieldOffset(0x00)] public uint Mask;
        [FieldOffset(0x1C)] public ushort Mode;
        [FieldOffset(0x20)] public ulong Inode;
        [FieldOffset(0x28)] public ulong Size;
        [FieldOffset(0x50)] public StatxTimestamp Birth;
        [FieldOffset(0x60)] public StatxTimestamp Change;
        [FieldOffset(0x70)] public StatxTimestamp Modification;
        [FieldOffset(0x88)] public uint DeviceMajor;
        [FieldOffset(0x8C)] public uint DeviceMinor;
    }

    internal static bool TryRead(string path, out StatxBuffer stat, bool followLinks = false) =>
        TryStatx(AtCurrentDirectory, path, (followLinks ? 0 : AtNoFollow) | ForceSync, out stat);

    internal static bool TryReadHandle(SafeFileHandle handle, out StatxBuffer stat) =>
        TryStatx(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath | ForceSync, out stat);

    private static bool TryStatx(int descriptor, string path, int flags, out StatxBuffer stat) {
        stat = default;
        if (!OperatingSystem.IsLinux()) return false;
        try {
            return Statx(descriptor, path, flags, BasicAndBirthTime, out stat) == 0 &&
                (stat.Mask & RequiredMask) == RequiredMask && stat.Size <= long.MaxValue && stat.Inode != 0 &&
                ValidTime(stat.Modification) && ((stat.Mask & ChangeMask) == 0 || ValidTime(stat.Change)) &&
                ((stat.Mask & BirthMask) == 0 || ValidTime(stat.Birth));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static bool ValidTime(StatxTimestamp time) => time.Nanoseconds < 1_000_000_000;
    internal static bool IsRegular(StatxBuffer stat) => (stat.Mode & 0xF000) == 0x8000;
    internal static string FileId(StatxBuffer stat) => $"linux:{stat.DeviceMajor:X}:{stat.DeviceMinor:X}:{stat.Inode:X}";
    internal static DateTime TimeUtc(StatxTimestamp time) =>
        DateTimeOffset.FromUnixTimeSeconds(time.Seconds).UtcDateTime.AddTicks(time.Nanoseconds / 100);

    internal static bool TryGetFileId(string path, out string fileId, bool followLinks = false) {
        fileId = string.Empty;
        if (!TryRead(path, out var stat, followLinks) || !IsRegular(stat)) return false;
        fileId = FileId(stat);
        return true;
    }

    internal static void FlushDirectory(string directory) {
        // ARM32 has legacy flag values; x86/x64/ARM64 use asm-generic values.
        var architecture = RuntimeInformation.ProcessArchitecture;
        int directoryFlag = architecture == Architecture.Arm ? 0x4000 : 0x10000;
        int noFollowFlag = architecture == Architecture.Arm ? 0x8000 : 0x20000;
        if (architecture is not (Architecture.Arm or Architecture.Arm64 or Architecture.X64 or Architecture.X86))
            throw new PlatformNotSupportedException("Directory durability flags are unavailable for this Linux architecture.");
        const int CloseOnExec = 0x80000;
        int descriptor = Open(directory, directoryFlag | noFollowFlag | CloseOnExec);
        if (descriptor < 0)
            throw new IOException("The containing directory could not be opened for a durability flush.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        try {
            if (Fsync(descriptor) != 0)
                throw new IOException("The containing directory could not be flushed; removal was not authorized.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Close(descriptor); }
    }

    internal static void CreateHardLink(string linkPath, string existingFilePath) {
        if (Link(existingFilePath, linkPath) != 0)
            throw new IOException("The hard link could not be created on this filesystem.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int descriptor, string path, int flags, uint mask, out StatxBuffer stat);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);
    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingFilePath, string linkPath);
}
