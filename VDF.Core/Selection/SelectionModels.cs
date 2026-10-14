using System.Collections.Generic;
using VDF.Core.ViewModels;

namespace VDF.Core.Selection;

public enum SelectionProfile { FolderPriorityFirst, QualityFirst, NewestFirst, OldestFirst, Custom }
public enum SelectionMode { StrictFolderPriority, Weighted }
public enum SelectionDateSource { Modification, Creation, Embedded }
public enum FolderOverlapPolicy { MostSpecific, HighestPriority }
public enum ReviewChoice { None, Keep, Remove, Ignore, RejectGroup }

public sealed class FolderPriorityRule {
    public string Directory { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Recursive { get; set; } = true;
    public bool ProtectAll { get; set; }
    /// <summary>Optional simple wildcard matched against the path relative to Directory.</summary>
    public string Pattern { get; set; } = string.Empty;
}

public sealed class SelectionCriterion {
    public string Name { get; set; } = "Resolution";
    public double Weight { get; set; } = 1;
    public bool PreferLower { get; set; }
}

public sealed class SelectionOptions {
    public bool AutoAfterScan { get; set; }
    public bool HighlightedGroupsOnly { get; set; }
    public SelectionProfile Profile { get; set; } = SelectionProfile.FolderPriorityFirst;
    public SelectionMode Mode { get; set; } = SelectionMode.StrictFolderPriority;
    public SelectionDateSource DateSource { get; set; } = SelectionDateSource.Modification;
    public FolderOverlapPolicy OverlapPolicy { get; set; } = FolderOverlapPolicy.MostSpecific;
    public List<FolderPriorityRule> FolderRules { get; set; } = new();
    public List<SelectionCriterion> Criteria { get; set; } = new() {
        new() { Name = "Resolution" }, new() { Name = "VideoBitrate" },
        new() { Name = "AudioChannels" }, new() { Name = "Date" }
    };
    public double FolderWeight { get; set; } = 5;
    public List<string> PreferredCodecs { get; set; } = new();
    public List<string> PreferredAudioLanguages { get; set; } = new();
    public List<string> PreferredContainers { get; set; } = new();
}

public sealed class NamedSelectionProfile {
    public string Name { get; set; } = string.Empty;
    public SelectionOptions Options { get; set; } = new();
}

/// <summary>A scan result plus protection/review state; never used as proof of byte identity.</summary>
public sealed class SelectionCandidate {
    public SelectionCandidate(DuplicateItem item) => Item = item;
    public DuplicateItem Item { get; }
    public bool IsAvailable { get; set; } = true;
    public bool ManuallyProtected { get; set; }
    public bool NfoProtected { get; set; }
    public bool ProtectionUncertain { get; set; }
    public string ProtectionReason { get; set; } = string.Empty;
    public ReviewChoice ManualChoice { get; set; }
    public DateTime? EmbeddedDateUtc { get; set; }
}

public sealed class SelectionDecision {
    public string Path { get; set; } = string.Empty;
    public Guid GroupId { get; set; }
    public bool RemoveCandidate { get; set; }
    public bool IsKeeper { get; set; }
    public bool IsProtected { get; set; }
    public bool RequiresReview { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int FolderPriority { get; set; }
    public DateTime? DateUtc { get; set; }
}

public sealed class SelectionPlan {
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SelectionProfile Profile { get; set; }
    public List<SelectionDecision> Decisions { get; set; } = new();
    public long EstimatedSavings { get; set; }
}
