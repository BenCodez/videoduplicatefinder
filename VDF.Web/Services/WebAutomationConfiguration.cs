// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using VDF.Core.Nfo;
using VDF.Core.Scheduling;
using VDF.Core.Selection;
using VDF.Core.ViewModels;

namespace VDF.Web.Services;

public enum WebScanProfile { Custom, ExactAndNear, EditedAndAltered, AiScan, DeepClean }

/// <summary>Web-only settings. A browser must save the generation it actually loaded.</summary>
public sealed class WebAutomationConfiguration {
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string Revision { get; set; } = string.Empty;
    [JsonRequired] public SelectionOptions Selection { get; set; } = new();
    [JsonRequired] public NfoOptions Nfo { get; set; } = new();
    [JsonRequired] public List<NamedSelectionProfile> Profiles { get; set; } = new();
    [JsonRequired] public ScanSchedule Schedule { get; set; } = new();
    [JsonRequired] public WebScanProfile ScheduledScanProfile { get; set; }
    [JsonRequired] public bool ScanAtStartup { get; set; }
    [JsonRequired] public string QuarantineDirectory { get; set; } = string.Empty;
}

public sealed class WebScheduleState {
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public DateTimeOffset? LastClaimedSlotUtc { get; set; }
    [JsonRequired] public DateTimeOffset? LastStartedUtc { get; set; }
    [JsonRequired] public DateTimeOffset? LastCompletedUtc { get; set; }
    [JsonRequired] public string Status { get; set; } = "Not run";
    [JsonRequired] public double DurationSeconds { get; set; }
}

public sealed record WebReviewRow(DuplicateItem Item, ReviewChoice Choice, bool ManuallyProtected,
    string Revision, string Reason, bool IsProtected);
public sealed record WebAutomationView(IReadOnlyList<WebReviewRow> Rows,
    IReadOnlyList<VDF.Core.Cleanup.QuarantineRecord> History);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, IncludeFields = true,
    WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebAutomationConfiguration))]
[JsonSerializable(typeof(WebScheduleState))]
[JsonSerializable(typeof(SelectionOptions))]
[JsonSerializable(typeof(VDF.Core.Settings))]
[JsonSerializable(typeof(WebResultsEnvelope))]
[JsonSerializable(typeof(WebSelectionPlanEnvelope))]
[JsonSerializable(typeof(WebJournalVersion))]
internal partial class WebAutomationJsonContext : JsonSerializerContext { }

// Read the version independently of a future version's payload shape.
internal sealed class WebJournalVersion {
    [JsonRequired] public int Version { get; set; }
}

internal sealed class WebResultsEnvelope {
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public List<WebSavedResult> Rows { get; set; } = new();
    // Optional for existing version 1 files. Legacy empty snapshots do not
    // establish that a scan completed; nonempty legacy results still restore.
    public bool? Completed { get; set; }
    public string? Generation { get; set; }
}
internal sealed class WebSelectionPlanEnvelope {
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public SelectionPlan Plan { get; set; } = new();
    public string? ResultGeneration { get; set; }
}
internal sealed class WebSavedResult {
    [JsonRequired] public DuplicateItem Item { get; set; } = new();
    [JsonRequired] public VDF.Core.Review.FileSnapshot? Snapshot { get; set; }
}
