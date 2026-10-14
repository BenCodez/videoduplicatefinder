using System.Linq;
using System.Text.Json.Serialization;
using VDF.Core.Selection;
using VDF.Core.Utils;

namespace VDF.Core.Review;

public sealed class ReviewRecord {
	[JsonRequired]
    public FileIdentity Identity { get; set; } = new();
	[JsonRequired]
    public ReviewChoice Choice { get; set; }
	[JsonRequired]
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
    // This gate covers the read/transform/write transaction across store instances.
    // It is deliberately separate from DurableJsonFile's write-only gate.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer.ForCurrentPlatform);
    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private IReadOnlyList<ReviewRecord> _records = Array.Empty<ReviewRecord>();
    private Dictionary<string, ReviewRecord> _byPath = new(PathComparer.ForCurrentPlatform);

    public ReviewStore(string path) {
        _path = SafePaths.Normalize(path);
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }
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

	internal async Task LoadForCleanupAsync() {
		await _gate.WaitAsync().ConfigureAwait(false);
		try {
			var envelope = await DurableJsonFile.ReadPrimaryAsync(_path, ReviewJsonContext.Default.ReviewEnvelope,
				ValidateEnvelope).ConfigureAwait(false) ?? throw new InvalidDataException("The current review journal is missing; restore it before cleanup.");
			ReplaceRecords(envelope.Records);
		}
		finally { _gate.Release(); }
	}

    public async Task SaveAsync(IEnumerable<ReviewRecord> records, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(records);
        // Freeze mutable GUI records before awaiting. No available-file requirement:
        // deliberate protection must survive a temporarily offline media mount.
        var snapshot = Freeze(records);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = JournalWriteLease.Acquire(_path);
            await WriteRecordsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Applies a pure synchronous transform to the current primary journal in one
    /// transaction. The transform must not call this store or perform journal I/O.
    /// A missing or invalid primary requires explicit initialization or recovery.
    /// </summary>
    public async Task UpdateAsync(Func<IReadOnlyList<ReviewRecord>, IEnumerable<ReviewRecord>> update,
        CancellationToken ct = default) {
        await UpdateWithSnapshotAsync(update, ct).ConfigureAwait(false);
    }

    // Return this transaction's own generation, independent of later cache reloads.
    // GUI callers must not use Records after an await to identify what they committed.
    internal async Task<IReadOnlyList<ReviewRecord>> UpdateWithSnapshotAsync(
        Func<IReadOnlyList<ReviewRecord>, IEnumerable<ReviewRecord>> update, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            ct.ThrowIfCancellationRequested();
            using var lease = JournalWriteLease.Acquire(_path);
            var envelope = await DurableJsonFile.ReadPrimaryAsync(_path, ReviewJsonContext.Default.ReviewEnvelope,
                ValidateEnvelope, ct).ConfigureAwait(false) ??
                throw new InvalidDataException("The current review journal is missing; restore or initialize it before updating.");
            // Neither a retained callback input nor its mutable output may change
            // the generation while serialization awaits filesystem operations.
            var current = envelope.Records.Select(Clone).ToList().AsReadOnly();
            var updated = update(current) ?? throw new InvalidDataException("The review update returned no records.");
            var snapshot = Freeze(updated);
            await WriteRecordsAsync(snapshot, ct).ConfigureAwait(false);
            return snapshot.Select(Clone).ToList().AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates an empty first generation only when no journal generation exists.</summary>
    internal async Task InitializeAsync(CancellationToken ct = default) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            ct.ThrowIfCancellationRequested();
            using var lease = JournalWriteLease.Acquire(_path);
            var envelope = await DurableJsonFile.ReadPrimaryAsync(_path, ReviewJsonContext.Default.ReviewEnvelope,
                ValidateEnvelope, ct).ConfigureAwait(false);
            if (envelope is not null) {
                ReplaceRecords(envelope.Records);
                return;
            }
            if (SafePaths.Exists(_path + ".bak"))
                throw new InvalidDataException("The current review journal is missing but a backup exists; restore it before initialization.");
            await WriteRecordsAsync(new(), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteRecordsAsync(List<ReviewRecord> snapshot, CancellationToken ct) {
        await DurableJsonFile.WriteAsync(_path, new ReviewEnvelope { Records = snapshot },
            ReviewJsonContext.Default.ReviewEnvelope, ValidateEnvelope, ct).ConfigureAwait(false);
        ReplaceRecords(snapshot);
    }

    private static List<ReviewRecord> Freeze(IEnumerable<ReviewRecord> records) {
        var snapshot = records.Select(Clone).GroupBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform)
            .Select(g => g.OrderByDescending(r => r.UpdatedUtc).ThenByDescending(r => r.ManuallyProtected).First())
            .OrderBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform).ToList();
        if (!ValidateEnvelope(new ReviewEnvelope { Records = snapshot }))
            throw new InvalidDataException("A review record has an invalid identity or choice.");
        return snapshot;
    }

    private void ReplaceRecords(List<ReviewRecord> records) {
        var byPath = records.Select(Clone).GroupBy(r => r.Identity.Path, PathComparer.ForCurrentPlatform)
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
