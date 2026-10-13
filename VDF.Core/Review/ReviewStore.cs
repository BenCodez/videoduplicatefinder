using System.Linq;
using System.Text.Json.Serialization;
using VDF.Core.Selection;
using VDF.Core.Utils;

namespace VDF.Core.Review;

public sealed class ReviewRecord {
    public FileIdentity Identity { get; set; } = new();
    public ReviewChoice Choice { get; set; }
    public bool ManuallyProtected { get; set; }
    public bool AutomaticallySelected { get; set; }
    public bool IsKeeper { get; set; }
    public string Profile { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Independent of the scan database. Path changes deliberately invalidate the
/// previous decision; neither hash-based relinking nor group-ID replay is allowed.
/// Offline identities remain stored but Find never returns an unvalidated decision.
/// </summary>
public sealed class ReviewStore {
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ReviewRecord> _records = Array.Empty<ReviewRecord>();
    private Dictionary<string, ReviewRecord> _byPath = new(PathComparer.ForCurrentPlatform);

    public ReviewStore(string path) => _path = SafePaths.Normalize(path);
    public IReadOnlyList<ReviewRecord> Records => Volatile.Read(ref _records);

    public async Task LoadAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            var envelope = await DurableJsonFile.ReadAsync(_path, ReviewJsonContext.Default.ReviewEnvelope,
                ValidateEnvelope).ConfigureAwait(false);
            ReplaceRecords(envelope?.Records ?? new());
        }
        finally { _gate.Release(); }
    }

    public ReviewRecord? Find(string path) {
        string full;
        try { full = SafePaths.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        return Volatile.Read(ref _byPath).TryGetValue(full, out var record) && record.Identity.MatchesCurrent()
            ? record : null;
    }

    public async Task SaveAsync(IEnumerable<ReviewRecord> records, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(records);
        // Freeze mutable GUI records before awaiting. No available-file requirement:
        // deliberate protection must survive a temporarily offline media mount.
        var snapshot = records.Select(Clone).GroupBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform)
            .Select(g => g.OrderByDescending(r => r.UpdatedUtc).ThenByDescending(r => r.ManuallyProtected).First())
            .OrderBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform).ToList();
        var envelope = new ReviewEnvelope { Records = snapshot };
        if (!ValidateEnvelope(envelope)) throw new InvalidDataException("A review record has an invalid identity or choice.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await DurableJsonFile.WriteAsync(_path, envelope, ReviewJsonContext.Default.ReviewEnvelope,
                ValidateEnvelope, cancellationToken).ConfigureAwait(false);
            ReplaceRecords(snapshot);
        }
        finally { _gate.Release(); }
    }

    private void ReplaceRecords(List<ReviewRecord> records) {
        var byPath = records.GroupBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedUtc)
                .ThenByDescending(r => r.ManuallyProtected).First(), PathComparer.ForCurrentPlatform);
        Volatile.Write(ref _byPath, byPath);
        Volatile.Write(ref _records, byPath.Values.OrderBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform)
            .ToList().AsReadOnly());
    }

    private static ReviewRecord Clone(ReviewRecord record) {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Identity is null) throw new InvalidDataException("A review identity is missing.");
        return new() {
            Identity = record.Identity, Choice = record.Choice, ManuallyProtected = record.ManuallyProtected,
            AutomaticallySelected = record.AutomaticallySelected, IsKeeper = record.IsKeeper, Profile = record.Profile,
            Reason = record.Reason, UpdatedUtc = record.UpdatedUtc
        };
    }

    private static bool ValidateEnvelope(ReviewEnvelope envelope) {
        if (envelope.Version != ReviewEnvelope.CurrentVersion)
            throw new NotSupportedException("The review schema version is unsupported; the journal was preserved.");
        return envelope.Records is not null && envelope.Records.All(r => r is not null &&
            r.Identity is not null && r.Identity.IsWellFormed() && Enum.IsDefined(r.Choice) &&
            r.Profile is not null && r.Reason is not null && r.UpdatedUtc.Kind == DateTimeKind.Utc);
    }
}

internal sealed class ReviewEnvelope {
    internal const int CurrentVersion = 1;
    [JsonRequired]
    public int Version { get; set; } = CurrentVersion;
    [JsonRequired]
    public List<ReviewRecord> Records { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReviewEnvelope))]
[JsonSerializable(typeof(SelectionPlanEnvelope))]
internal partial class ReviewJsonContext : JsonSerializerContext { }
