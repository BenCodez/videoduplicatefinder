using System.Linq;
using VDF.Core.Utils;

namespace VDF.Core.Review;

/// <summary>Conservative checks shared by the review journal and media operations.</summary>
internal static class SafePaths {
    internal static string Normalize(string path, bool rejectTraversal = false) {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            throw new ArgumentException("A valid filesystem path is required.", nameof(path));
        if (rejectTraversal && path.Split(new[] { System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar }).Any(part => part is "." or ".."))
            throw new IOException("Traversal components are not permitted in cleanup paths.");
        string full = System.IO.Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            full.AsSpan(System.IO.Path.GetPathRoot(full)!.Length).Contains(':')))
            throw new IOException("Device paths and alternate data streams are not supported.");
        return System.IO.Path.TrimEndingDirectorySeparator(full);
    }

    internal static void RequireNoLinks(string path, bool allowMissing = false) {
        string full = Normalize(path);
        string root = System.IO.Path.GetPathRoot(full)!;
        string current = root;
        CheckNode(current, true);
        string[] parts = full.Substring(root.Length).Split(System.IO.Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) {
            current = System.IO.Path.Combine(current, parts[i]);
            try { CheckNode(current, i != parts.Length - 1); }
            catch (Exception ex) when (allowMissing && (ex is FileNotFoundException or DirectoryNotFoundException)) { return; }
        }
    }

    private static void CheckNode(string path, bool directory) {
        // GetAttributes also detects broken symbolic links; File.Exists does not.
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links and reparse points are not permitted.");
        if (directory && (attributes & FileAttributes.Directory) == 0)
            throw new IOException("A path ancestor is not a directory.");
    }

    internal static void RequireRegularFile(string path) {
        RequireNoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("A regular file is required.");
        if (OperatingSystem.IsLinux()) {
            if (!IdentityReader.IsLinuxRegularFile(path)) throw new IOException("A regular, available file is required.");
        }
        else if (!OperatingSystem.IsWindows() &&
            (Mono.Unix.Native.Syscall.lstat(path, out var stat) != 0 || ((uint)stat.st_mode & 0xF000u) != 0x8000u))
            throw new IOException("A regular, available file is required.");
    }

    internal static void RequireDirectory(string path) {
        RequireNoLinks(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The required directory is unavailable.");
    }

    internal static void EnsureParentDirectory(string path) {
        string parent = System.IO.Path.GetDirectoryName(Normalize(path))!;
        string root = System.IO.Path.GetPathRoot(parent)!;
        RequireDirectory(root);
        string current = root;
        foreach (string part in parent.Substring(root.Length).Split(System.IO.Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries)) {
            string child = System.IO.Path.Combine(current, part);
            RequireNoLinks(child, true);
            if (!Exists(child)) Directory.CreateDirectory(child);
            RequireDirectory(child);
            // Persist the namespace entry that makes the child reachable. This
            // includes existing ancestors: a prior failed attempt may have created
            // one without completing its flush, and a retry must not bypass it.
            DurableFileOperations.FlushDirectory(current);
            current = child;
        }
        RequireDirectory(parent);
    }

    internal static bool Exists(string path) {
        try { File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    internal static bool Same(string a, string b) => PathComparer.ForCurrentPlatform.Equals(a, b);

    internal static bool Within(string candidate, string directory) => Same(candidate, directory) ||
        candidate.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
