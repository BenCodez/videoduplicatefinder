using VDF.Core.Utils;

namespace VDF.Core.Review;

/// <summary>
/// A cheap, no-hash token anchoring a displayed file before delayed full identity
/// capture. It is not authorization for cleanup and does not prove media matches.
/// Filesystems without a reliable change stamp cannot supply this token.
/// </summary>
public sealed record FileSnapshot {
    public string Path { get; init; } = string.Empty;
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime CreationUtc { get; init; }
    public string StableFileId { get; init; } = string.Empty;
    public string ChangeStamp { get; init; } = string.Empty;

    public static FileSnapshot? Capture(string path) => FileIdentity.CaptureSnapshot(path);

    public bool MatchesCurrent() {
        var current = FileIdentity.CaptureSnapshot(Path);
        return current is not null && PathComparer.ForCurrentPlatform.Equals(Path, current.Path) &&
            Length == current.Length && LastWriteUtc == current.LastWriteUtc && CreationUtc == current.CreationUtc &&
            StableFileId == current.StableFileId && ChangeStamp == current.ChangeStamp;
    }

    public bool Matches(FileIdentity identity) => identity is not null && ChangeStamp.Length != 0 &&
        PathComparer.ForCurrentPlatform.Equals(Path, identity.Path) && Length == identity.Length &&
        LastWriteUtc == identity.LastWriteUtc && CreationUtc == identity.CreationUtc &&
        StableFileId == identity.StableFileId && ChangeStamp == identity.ChangeStamp;
}
