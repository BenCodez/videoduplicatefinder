namespace VDF.Core.Review;

/// <summary>
/// A nonblocking cross-process lease for a journal transaction. The persistent
/// lock file is never removed, so another writer cannot acquire a different inode.
/// </summary>
internal static class JournalWriteLease {
    internal static IDisposable Acquire(string journalPath) {
        journalPath = SafePaths.Normalize(journalPath);
        SafePaths.EnsureParentDirectory(journalPath);
        string leasePath = journalPath + ".lock";
        RequireSafeFile(journalPath);
        RequireSafeFile(journalPath + ".bak");
        RequireSafeFile(leasePath);

        // FileShare.None fails immediately if a cooperating process owns the
        // lease. Do not retry, remove, truncate, or replace a busy lock file.
        var stream = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.WriteThrough);
        try {
            SafePaths.RequireRegularFile(leasePath);
            if (OperatingSystem.IsLinux() &&
                (!LinuxFileSystem.TryReadHandle(stream.SafeFileHandle, out var opened) ||
                !LinuxFileSystem.IsRegular(opened) || !LinuxFileSystem.TryRead(leasePath, out var current) ||
                LinuxFileSystem.FileId(opened) != LinuxFileSystem.FileId(current)))
                throw new IOException("The journal write lease changed while it was acquired.");
            RequireSafeFile(journalPath);
            RequireSafeFile(journalPath + ".bak");
            return stream;
        }
        catch {
            stream.Dispose();
            throw;
        }
    }

    private static void RequireSafeFile(string path) {
        SafePaths.RequireNoLinks(path, true);
        if (SafePaths.Exists(path)) SafePaths.RequireRegularFile(path);
    }
}
