using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VDF.Core.Review;

/// <summary>Flush namespace changes before later steps can remove another copy.</summary>
internal static partial class DurableFileOperations {
    internal static readonly AsyncLocal<Action<string>?> DirectoryFlushedObserver = new();
    internal static readonly AsyncLocal<Action<string, string>?> FileMovedObserver = new();
    internal static void Move(string source, string destination, bool overwrite = false) {
        SafePaths.RequireRegularFile(source);
        SafePaths.RequireDirectory(Path.GetDirectoryName(destination)!);
        SafePaths.RequireNoLinks(destination, true);
        if (OperatingSystem.IsWindows()) {
            const uint WriteThrough = 8;
            const uint ReplaceExisting = 1;
            if (!MoveFileEx(source, destination, WriteThrough | (overwrite ? ReplaceExisting : 0)))
                throw new IOException("A durable file rename failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else {
            File.Move(source, destination, overwrite);
            FlushDirectory(Path.GetDirectoryName(destination)!);
            if (!SafePaths.Same(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!))
                FlushDirectory(Path.GetDirectoryName(source)!);
        }
        FileMovedObserver.Value?.Invoke(source, destination);
    }

    internal static void FlushDirectory(string directory) {
        if (OperatingSystem.IsWindows()) return; // MoveFileEx WRITE_THROUGH commits the rename.
        SafePaths.RequireDirectory(directory);
        if (OperatingSystem.IsLinux()) {
            LinuxFileSystem.FlushDirectory(directory);
            DirectoryFlushedObserver.Value?.Invoke(directory);
            return;
        }
        int descriptor = Mono.Unix.Native.Syscall.open(directory,
            Mono.Unix.Native.OpenFlags.O_RDONLY | Mono.Unix.Native.OpenFlags.O_DIRECTORY | Mono.Unix.Native.OpenFlags.O_NOFOLLOW);
        if (descriptor < 0) throw new IOException("The containing directory could not be opened for a durability flush.");
        try {
            if (Mono.Unix.Native.Syscall.fsync(descriptor) != 0)
                throw new IOException("The containing directory could not be flushed; removal was not authorized.");
        }
        finally { Mono.Unix.Native.Syscall.close(descriptor); }
        DirectoryFlushedObserver.Value?.Invoke(directory);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string destination, uint flags);
}
