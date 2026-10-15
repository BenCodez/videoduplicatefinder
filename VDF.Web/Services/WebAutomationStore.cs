// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Security.Cryptography;
using VDF.Core.Cleanup;
using VDF.Core.Review;
using VDF.Core.Scheduling;
using VDF.Core.Selection;
using VDF.Core.Utils;

namespace VDF.Web.Services;

internal sealed class WebAutomationStore(string path) {
    internal string Path { get; } = System.IO.Path.GetFullPath(path);
    internal static WebAutomationConfiguration Clone(WebAutomationConfiguration config) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(config, WebAutomationJsonContext.Default.WebAutomationConfiguration),
            WebAutomationJsonContext.Default.WebAutomationConfiguration)!;

    internal async Task<WebAutomationConfiguration> LoadAsync() {
        var before = SafePaths.Exists(Path) ? await FileIdentity.CaptureAsync(Path) : null;
        var value = await DurableJsonFile.ReadPrimaryAsync(Path, WebAutomationJsonContext.Default.WebAutomationConfiguration, Validate);
        if (value is null && SafePaths.Exists(Path + ".bak"))
            throw new InvalidDataException("The current Web automation settings are missing; restore them before continuing.");
        if (value is not null) {
            RequireProtectionFields(Path);
            if (before is null || !before.MatchesCurrent(true)) throw new IOException("Web automation settings changed while being read.");
            // Include content edits made outside the application, even when an
            // editor retains the serialized generation field.
            value.Revision = ContentRevision(value);
        }
        return value ?? new();
    }
    static string ContentRevision(WebAutomationConfiguration config) {
        var copy = Clone(config);
        copy.Revision = string.Empty;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy,
            WebAutomationJsonContext.Default.WebAutomationConfiguration)));
    }

    internal async Task<WebAutomationConfiguration> SaveAsync(WebAutomationConfiguration config, string folder) {
        var snapshot = Clone(config);
        SafePaths.EnsureParentDirectory(Path);
        using var lease = JournalWriteLease.Acquire(Path);
        var current = await LoadAsync();
        if (snapshot.Revision != current.Revision)
            throw new InvalidOperationException("Newer Web automation settings were saved. Reload before reapplying your changes.");
        await RequirePolicyOwnerAsync(folder);
        snapshot.Revision = Guid.NewGuid().ToString("N");
        await DurableJsonFile.WriteAsync(Path, snapshot, WebAutomationJsonContext.Default.WebAutomationConfiguration, Validate);
        var committed = await LoadAsync();
        await ProjectAsync(committed, folder);
        return Clone(committed);
    }

    internal async Task ProjectAsync(WebAutomationConfiguration config, string folder) {
        if (config.Revision.Length == 0) throw new InvalidOperationException("Save the Web automation settings first.");
        await RequirePolicyOwnerAsync(folder);
        var identity = await FileIdentity.CaptureAsync(Path) ?? throw new IOException("Web automation settings could not be verified.");
        // The projection and its hash must describe the same generation, including
        // when another process commits settings between Load and this capture.
        var current = await LoadAsync();
        if (current.Revision != config.Revision || !identity.MatchesCurrent(true))
            throw new IOException("Web automation settings changed while being loaded. Reload before continuing.");
        await CleanupProtectionPolicyStore.SaveAsync(folder, new() {
            NfoOptions = current.Nfo, FolderRules = current.Selection.FolderRules,
            OverlapPolicy = current.Selection.OverlapPolicy
        }, identity);
    }

    async Task RequirePolicyOwnerAsync(string folder) {
        string policy = System.IO.Path.Combine(folder, CleanupProtectionPolicyStore.FileName);
        var envelope = await DurableJsonFile.ReadPrimaryAsync(policy,
            ProtectionPolicyJsonContext.Default.ProtectionPolicyEnvelope, e => e.Version == 1);
        if (envelope?.SourceSettings is { } source && !PathComparer.ForCurrentPlatform.Equals(source.Path, Path))
            throw new InvalidOperationException("This state folder uses another application's protection settings. Use a separate state folder for the Web app.");
    }

    static bool Validate(WebAutomationConfiguration c) {
        if (c.Version != 1) throw new NotSupportedException("The Web automation settings version is unsupported; the file was preserved.");
        static bool Strings(List<string>? values, int count = 128, int length = 256) => values is not null &&
            values.Count <= count && values.All(v => v is not null && v.Length <= length);
        static bool Options(SelectionOptions? s) => s is not null && Enum.IsDefined(s.Profile) && Enum.IsDefined(s.Mode) &&
            Enum.IsDefined(s.DateSource) && Enum.IsDefined(s.OverlapPolicy) && double.IsFinite(s.FolderWeight) &&
            s.FolderWeight >= 0 && s.FolderRules is not null && s.FolderRules.Count <= 10_000 &&
            s.FolderRules.All(r => r is not null && r.Directory is not null && r.Directory.Length <= 32768 &&
                r.Pattern is not null && r.Pattern.Length <= 4096 && (r.Directory.Length == 0 || System.IO.Path.IsPathFullyQualified(r.Directory))) &&
            s.Criteria is not null && s.Criteria.Count <= 128 && s.Criteria.All(r => r is not null &&
                r.Name is not null && r.Name.Length <= 128 && double.IsFinite(r.Weight) && r.Weight >= 0) &&
            Strings(s.PreferredCodecs) && Strings(s.PreferredAudioLanguages) && Strings(s.PreferredContainers);
        return c.Revision is not null && (c.Revision.Length == 0 || Guid.TryParseExact(c.Revision, "N", out _) ||
            c.Revision.Length == 64 && c.Revision.All(Uri.IsHexDigit)) &&
            Options(c.Selection) && c.Nfo is not null && Strings(c.Nfo.BooleanElements) && Strings(c.Nfo.TagElements) && Strings(c.Nfo.TagValues) &&
            c.Profiles is not null && c.Profiles.Count <= 128 && c.Profiles.All(p => p is not null &&
                !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 128 && Options(p.Options)) &&
            c.Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == c.Profiles.Count &&
            c.Schedule is not null && ScanSchedulePolicy.Evaluate(c.Schedule with { Enabled = true }, DateTimeOffset.UtcNow, null, TimeZoneInfo.Local).Error is null &&
            Enum.IsDefined(c.ScheduledScanProfile) && c.QuarantineDirectory is not null && c.QuarantineDirectory.Length <= 32768 &&
            (c.QuarantineDirectory.Length == 0 || System.IO.Path.IsPathFullyQualified(c.QuarantineDirectory));
    }

    // Core UI models retain backwards-compatible defaults. This new document has
    // no legacy generations: a truncated protection object must never reanchor
    // a permissive default policy with a fresh source hash.
    static void RequireProtectionFields(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32L * 1024 * 1024) throw new InvalidDataException("Web automation settings exceed the supported size.");
        // Read only the bounded opening length; growth/truncation also invalidates
        // the surrounding file identity check before any projection is written.
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes);
        static JsonElement Field(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? value :
            throw new InvalidDataException($"Web automation protection setting '{name}' is missing.");
        var nfo = Field(json.RootElement, "nfo");
        foreach (var name in new[] { "enabled", "booleanElements", "tagElements", "tagValues", "inheritSeries", "inheritSeason" }) Field(nfo, name);
        var selection = Field(json.RootElement, "selection");
        Field(selection, "overlapPolicy");
        foreach (var rule in Field(selection, "folderRules").EnumerateArray())
            foreach (var name in new[] { "directory", "priority", "enabled", "recursive", "protectAll", "pattern" }) Field(rule, name);
    }
}
