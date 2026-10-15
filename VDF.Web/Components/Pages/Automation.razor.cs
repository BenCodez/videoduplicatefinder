using VDF.Core.Cleanup;
using VDF.Core.Nfo;
using VDF.Core.Scheduling;
using VDF.Core.Selection;
using VDF.Web.Services;
using VDF.Web.Components.Layout;

namespace VDF.Web.Components.Pages;

public partial class Automation {
    static readonly string[] CriterionNames = [
        "FolderPriority", "Resolution", "VideoBitrate", "AudioBitrate", "AudioChannels",
        "Codec", "Hdr", "AudioLanguage", "Subtitles", "Date", "ModificationDate",
        "CreationDate", "Duration", "Size", "Container", "Metadata"
    ];

    WebAutomationConfiguration? _draft;
    IReadOnlyList<WebReviewRow> _rows = Array.Empty<WebReviewRow>();
    IReadOnlyList<QuarantineRecord> _history = Array.Empty<QuarantineRecord>();
    readonly HashSet<Guid> _previewGroups = new();
    SelectionPlan? _plan;
    string _profileName = string.Empty;
    string? _restoreOperation;
    string? _error;
    string? _notice;
    bool _working;
    bool _refreshing;
    bool _refreshAgain;
    bool _draftDirty;
    bool _disposed;
    FolderBrowser? _ruleBrowser;
    FolderPriorityRule? _browsedRule;
    long _refreshedGeneration = -1;

    bool Busy => _working || _refreshing || Scan.IsBusy;
    bool ReviewBusy => Busy || _refreshing;

    protected override async Task OnInitializedAsync() {
        Scan.StateChanged += OnStateChanged;
        await ReloadAsync();
    }

    async Task RunAsync(Func<Task> action) {
        if (_working || _disposed) return;
        _working = true;
        _error = null;
        _notice = null;
        try {
            await action();
        }
        catch (Exception ex) {
            _error = ex.Message + " Reload saved settings or refresh review data before trying again.";
        }
        finally { _working = false; }
    }

    Task ReloadAsync() => RunAsync(async () => {
        var loaded = await Scan.LoadAutomationAsync();
        _draft = Clone(loaded);
        _draftDirty = false;
        _plan = null;
        await RefreshDataAsync();
        _notice = "Saved automation settings loaded. Local draft changes were discarded.";
    });

    Task SaveAsync() => RunAsync(async () => {
        if (_draft is null) return;
        var saved = await Scan.SaveAutomationAsync(Clone(_draft));
        _draft = Clone(saved);
        _draftDirty = false;
        _plan = null;
        await RefreshDataAsync();
        _notice = "Automation settings saved.";
    });

    Task PreviewAsync() => RunAsync(async () => {
        if (_draft is null || _draftDirty) return;
        _plan = null;
        var groups = _draft.Selection.HighlightedGroupsOnly ? _previewGroups.ToArray() : null;
        if (groups is { Length: 0 }) throw new InvalidOperationException("Highlight at least one group to preview.");
        _plan = await Scan.PreviewSelectionAsync(null, groups);
        await RefreshDataAsync();
        _notice = "Selection preview prepared. No files were removed.";
    });

    async Task CheckScheduleAsync() {
        await RunAsync(async () => {
            if (await Scan.CheckScheduleAsync(DateTimeOffset.UtcNow) is { } error)
                throw new InvalidOperationException(error);
            // A due check returns once scanning starts. Its completion notification
            // will refresh the view after the scan releases its data reservation.
            if (!Scan.IsBusy) await RefreshDataAsync();
            _notice = "Saved schedule checked.";
        });
        // A fast scan can finish while RunAsync still suppresses refresh events.
        // Recheck after releasing that flag so its final view is not missed.
        if (!_disposed && !Scan.IsBusy && _refreshedGeneration != Scan.AutomationGeneration)
            await RefreshFromStateAsync(true);
    }

    Task RefreshAsync() => RunAsync(async () => {
        await RefreshDataAsync();
        _notice = "Review and quarantine history refreshed.";
    });

    Task SetReviewAsync(WebReviewRow row, ReviewChoice choice, bool? protectedFile = null) => RunAsync(async () => {
        // A queued callback from a replaced render must not reuse an old row's
        // revision. The service also checks the journal revision atomically.
        if (!_rows.Any(current => ReferenceEquals(current, row)))
            throw new InvalidOperationException("Review data changed. Refresh and choose the action again.");
        await Scan.SetReviewAsync(row.Item, choice, protectedFile, row.Revision);
        _plan = null;
        await RefreshDataAsync();
        _notice = "Review decision saved.";
    });

    Task RestoreAsync(string operationId) => RunAsync(async () => {
        if (_restoreOperation != operationId || !_history.Any(record => record.OperationId == operationId))
            throw new InvalidOperationException("Choose a current quarantine operation and confirm the restore.");
        _restoreOperation = null;
        await Scan.RestoreQuarantineAsync(operationId);
        _plan = null;
        await RefreshDataAsync();
        _notice = "Quarantine copy restored.";
    });

    async Task RefreshDataAsync() {
        if (_refreshing) {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try {
            do {
                _refreshAgain = false;
                long generation = Scan.AutomationGeneration;
                var view = await Scan.GetAutomationViewAsync();
                var rows = view.Rows;
                if (_disposed) return;
                _rows = rows;
                _history = view.History;
                _refreshedGeneration = generation;
                _refreshAgain = generation != Scan.AutomationGeneration;
                _plan = _draftDirty ? null : Scan.LastSelectionPlan;
                var currentGroups = rows.Select(row => row.Item.GroupId).ToHashSet();
                _previewGroups.IntersectWith(currentGroups);
                if (!_history.Any(record => record.OperationId == _restoreOperation)) _restoreOperation = null;
            } while (_refreshAgain && !_disposed);
        }
        finally { _refreshing = false; }
    }

    void OnStateChanged() {
        // Decide at notification time. A notification from our own journal read
        // can reach the renderer after that read has completed, when the local
        // busy flags no longer identify it as our own operation.
        bool refresh = !_working && !_refreshing && !Scan.IsBusy && _refreshedGeneration != Scan.AutomationGeneration;
        _ = RefreshFromStateAsync(refresh);
    }

    async Task RefreshFromStateAsync(bool refresh) {
        try {
            await InvokeAsync(async () => {
                if (_disposed) return;
                // Progress events need only a render. Refresh once a scan or
                // durable operation finishes without replacing the local draft.
                // Read operations also emit StateChanged. Do not recursively
                // refresh in response to our own reads or in-flight actions.
                if (refresh && !_working && !_refreshing && !Scan.IsBusy && _refreshedGeneration != Scan.AutomationGeneration) {
                    try { await RefreshDataAsync(); }
                    catch (Exception ex) { _error = "Unable to refresh automation data: " + ex.Message; }
                }
                if (!_disposed) StateHasChanged();
            });
        }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (InvalidOperationException) when (_disposed) { }
    }

    void MarkDraftChanged() {
        _draftDirty = true;
        _notice = null;
        _plan = null;
    }

    void AddFolderRule() {
        _draft!.Selection.FolderRules.Add(new());
        MarkDraftChanged();
    }
    void BrowseFolderRule(FolderPriorityRule rule) {
        _browsedRule = rule;
        _ruleBrowser?.Open(rule.Directory);
    }
    void SelectRuleFolder(string path) {
        // Reloading settings or profiles can replace the rule while browsing.
        if (Busy || _browsedRule is null || _draft?.Selection.FolderRules.Contains(_browsedRule) != true) return;
        _browsedRule.Directory = path;
        _browsedRule = null;
        MarkDraftChanged();
    }
    void RemoveFolderRule(FolderPriorityRule rule) {
        _draft!.Selection.FolderRules.Remove(rule);
        MarkDraftChanged();
    }
    void AddCriterion() {
        _draft!.Selection.Criteria.Add(new());
        MarkDraftChanged();
    }
    void RemoveCriterion(SelectionCriterion criterion) {
        _draft!.Selection.Criteria.Remove(criterion);
        MarkDraftChanged();
    }
    void MoveCriterion(SelectionCriterion criterion, int offset) {
        var criteria = _draft!.Selection.Criteria;
        var index = criteria.IndexOf(criterion);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= criteria.Count) return;
        criteria.RemoveAt(index);
        criteria.Insert(destination, criterion);
        MarkDraftChanged();
    }
    void SaveNamedProfile() {
        var name = _profileName.Trim();
        if (name.Length == 0 || _draft is null) return;
        var existing = _draft.Profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _draft.Profiles.Add(new() { Name = name, Options = CloneSelection(_draft.Selection) });
        else existing.Options = CloneSelection(_draft.Selection);
        MarkDraftChanged();
    }
    void LoadNamedProfile(NamedSelectionProfile profile) {
        _draft!.Selection = CloneSelection(profile.Options);
        _profileName = profile.Name;
        MarkDraftChanged();
    }
    void RemoveNamedProfile(NamedSelectionProfile profile) {
        _draft!.Profiles.Remove(profile);
        MarkDraftChanged();
    }
    void SetLines(List<string> target, object? value) {
        target.Clear();
        target.AddRange((value?.ToString() ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        MarkDraftChanged();
    }

    bool ScheduleEnabled {
        get => _draft!.Schedule.Enabled;
        set => _draft!.Schedule = _draft.Schedule with { Enabled = value };
    }
    ScanScheduleKind ScheduleKind {
        get => _draft!.Schedule.Kind;
        set => _draft!.Schedule = _draft.Schedule with { Kind = value };
    }
    string ScheduleLocalTime {
        get => _draft!.Schedule.LocalTime;
        set => _draft!.Schedule = _draft.Schedule with { LocalTime = value };
    }
    int IntervalMinutes {
        get => _draft!.Schedule.IntervalMinutes;
        set => _draft!.Schedule = _draft.Schedule with { IntervalMinutes = value };
    }
    int CatchUpHours {
        get => _draft!.Schedule.CatchUpHours;
        set => _draft!.Schedule = _draft.Schedule with { CatchUpHours = value };
    }
    bool IncludesDay(DayOfWeek day) => (_draft!.Schedule.Weekdays & (1 << (int)day)) != 0;
    void SetDay(DayOfWeek day, bool enabled) {
        var mask = 1 << (int)day;
        _draft!.Schedule = _draft.Schedule with { Weekdays = enabled ? _draft.Schedule.Weekdays | mask : _draft.Schedule.Weekdays & ~mask };
        MarkDraftChanged();
    }
    void SetPreviewGroup(Guid group, bool enabled) {
        if (enabled) _previewGroups.Add(group);
        else _previewGroups.Remove(group);
        _plan = null;
    }

    static string FormatTime(DateTimeOffset? time) => time?.ToUniversalTime().ToString("u") ?? "never";
    static string DecisionLabel(SelectionDecision decision) => decision.IsProtected ? "Protected"
        : decision.RemoveCandidate ? "Remove candidate — review required"
        : decision.RequiresReview ? "Needs review" : decision.IsKeeper ? "Keep" : "Leave unchanged";

    static WebAutomationConfiguration Clone(WebAutomationConfiguration config) => new() {
        Version = config.Version,
        Revision = config.Revision,
        Selection = CloneSelection(config.Selection),
        Nfo = new NfoOptions {
            Enabled = config.Nfo.Enabled,
            BooleanElements = new(config.Nfo.BooleanElements),
            TagElements = new(config.Nfo.TagElements),
            TagValues = new(config.Nfo.TagValues),
            InheritSeries = config.Nfo.InheritSeries,
            InheritSeason = config.Nfo.InheritSeason
        },
        Profiles = config.Profiles.Select(profile => new NamedSelectionProfile { Name = profile.Name, Options = CloneSelection(profile.Options) }).ToList(),
        Schedule = config.Schedule with { },
        ScheduledScanProfile = config.ScheduledScanProfile,
        ScanAtStartup = config.ScanAtStartup,
        QuarantineDirectory = config.QuarantineDirectory
    };

    static SelectionOptions CloneSelection(SelectionOptions options) => new() {
        AutoAfterScan = options.AutoAfterScan,
        HighlightedGroupsOnly = options.HighlightedGroupsOnly,
        Profile = options.Profile,
        Mode = options.Mode,
        DateSource = options.DateSource,
        OverlapPolicy = options.OverlapPolicy,
        FolderWeight = options.FolderWeight,
        FolderRules = options.FolderRules.Select(rule => new FolderPriorityRule {
            Directory = rule.Directory, Priority = rule.Priority, Enabled = rule.Enabled,
            Recursive = rule.Recursive, ProtectAll = rule.ProtectAll, Pattern = rule.Pattern
        }).ToList(),
        Criteria = options.Criteria.Select(criterion => new SelectionCriterion {
            Name = criterion.Name, Weight = criterion.Weight, PreferLower = criterion.PreferLower
        }).ToList(),
        PreferredCodecs = new(options.PreferredCodecs),
        PreferredAudioLanguages = new(options.PreferredAudioLanguages),
        PreferredContainers = new(options.PreferredContainers)
    };

    public void Dispose() {
        _disposed = true;
        Scan.StateChanged -= OnStateChanged;
    }
}
