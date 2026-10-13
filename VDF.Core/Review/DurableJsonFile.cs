using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VDF.Core.Utils;

namespace VDF.Core.Review;

/// <summary>Versioned source-generated JSON with two flushed copies of the current generation.</summary>
internal static class DurableJsonFile {
    private const long MaximumJournalBytes = 32L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer.ForCurrentPlatform);

    internal static async Task<T?> ReadAsync<T>(string path, JsonTypeInfo<T> type,
        Func<T, bool> validate, CancellationToken ct = default) where T : class {
        SafePaths.RequireNoLinks(path, true);
        Exception? primaryError = null;
        if (SafePaths.Exists(path)) {
            try { return await ReadOneAsync(path, type, validate, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { primaryError = ex; }
        }
        string backup = path + ".bak";
        if (SafePaths.Exists(backup)) {
            try { return await ReadOneAsync(backup, type, validate, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) {
                throw new InvalidDataException("The journal and its backup are unreadable; both were preserved.", ex);
            }
        }
        if (primaryError is not null)
            throw new InvalidDataException("The journal is unreadable and no valid backup is available; it was preserved.", primaryError);
        return null;
    }

    private static async Task<T> ReadOneAsync<T>(string path, JsonTypeInfo<T> type,
        Func<T, bool> validate, CancellationToken ct) where T : class {
        SafePaths.RequireRegularFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumJournalBytes) throw new InvalidDataException("The journal exceeds the supported size limit.");
        T? data = await JsonSerializer.DeserializeAsync(stream, type, ct).ConfigureAwait(false);
        if (data is null || !validate(data)) throw new InvalidDataException("The journal schema is invalid.");
        return data;
    }

    internal static async Task WriteAsync<T>(string path, T data, JsonTypeInfo<T> type,
        Func<T, bool> validate, CancellationToken ct = default) where T : class {
        path = SafePaths.Normalize(path);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string backupTemporary = path + ".bak.tmp-" + Guid.NewGuid().ToString("N");
        try {
            if (!validate(data)) throw new InvalidDataException("The journal schema is invalid.");
            SafePaths.EnsureParentDirectory(path);
            SafePaths.RequireNoLinks(path, true);
            SafePaths.RequireNoLinks(path + ".bak", true);
            // Validate any existing generation before replacing it; do not silently
            // downgrade future schemas or discard unrecoverable corruption.
            await ReadAsync(path, type, validate, ct).ConfigureAwait(false);
            await WriteTemporaryAsync(temporary, data, type, ct).ConfigureAwait(false);
            // A last-generation backup can omit a newly prepared cleanup intent.
            // Both durable copies must contain this generation before a caller is
            // allowed to move or remove media after the awaited save completes.
            await WriteTemporaryAsync(backupTemporary, data, type, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            SafePaths.RequireNoLinks(path, true);
            SafePaths.RequireNoLinks(path + ".bak", true);
            DurableFileOperations.Move(backupTemporary, path + ".bak", true);
            // The first namespace commit is the cancellation boundary. Finish the
            // mirrored commit rather than return cancellation with mismatched data.
            DurableFileOperations.Move(temporary, path, true);
        }
        finally {
            TryRemoveTemporary(temporary);
            TryRemoveTemporary(backupTemporary);
            gate.Release();
        }
    }

    private static async Task WriteTemporaryAsync<T>(string path, T data, JsonTypeInfo<T> type,
        CancellationToken ct) where T : class {
        SafePaths.RequireNoLinks(path, true);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, data, type, ct).ConfigureAwait(false);
        if (stream.Length > MaximumJournalBytes) throw new InvalidDataException("The journal exceeds the supported size limit.");
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(true);
    }

    private static void TryRemoveTemporary(string path) {
        try { SafePaths.RequireNoLinks(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
