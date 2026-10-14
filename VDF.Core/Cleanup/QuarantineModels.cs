using System.Text.Json.Serialization;
using VDF.Core.Review;

namespace VDF.Core.Cleanup;

public enum QuarantineState {
    Prepared, CopyReady, SourceClaimed, Quarantined, Restoring, Restored,
    Cancelled, Failed, Recovered, NeedsAttention
}

public sealed class QuarantineRecord {
    public string OperationId { get; set; } = string.Empty;
    public FileIdentity Target { get; set; } = new();
    public FileIdentity Keeper { get; set; } = new();
    public string OriginalPath { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public string QuarantinePath { get; set; } = string.Empty;
    public string StagingPath { get; set; } = string.Empty;
    public string SourceStagingPath { get; set; } = string.Empty;
    public string RestoringPath { get; set; } = string.Empty;
    public FileIdentity? QuarantineIdentity { get; set; }
    public FileIdentity? RestoredIdentity { get; set; }
    public FileIdentity? ProtectionRefreshClaim { get; set; }
    public QuarantineState State { get; set; }
    public bool ByteIdentical { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public List<string> SidecarsLeftInPlace { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class QuarantinePreview {
    public FileIdentity Target { get; init; } = new();
    public FileIdentity Keeper { get; init; } = new();
    public string DestinationDirectory { get; init; } = string.Empty;
    public bool DestinationWillBeCreated { get; init; }
    public bool ByteIdentical { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<string> SidecarsLeftInPlace { get; init; } = Array.Empty<string>();
}

internal sealed class QuarantineEnvelope {
	internal const int CurrentVersion = 2;
    [JsonRequired]
    public int Version { get; set; } = CurrentVersion;
    [JsonRequired]
    public List<QuarantineRecord> Operations { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(QuarantineEnvelope))]
internal partial class CleanupJsonContext : JsonSerializerContext { }

internal enum QuarantineCheckpoint {
    Prepared, CopyFlushed, CopyCommitted, SourceClaimed, BeforeSourceDelete,
    RestoreCopied, RestoreCommitted
}

internal static class CleanupOperationGate {
    // Also shared by legacy safe removal, preventing two operations in this
    // process from removing each other's keeper at the same time.
    internal static readonly SemaphoreSlim Instance = new(1, 1);
}
