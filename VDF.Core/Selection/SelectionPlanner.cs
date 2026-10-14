using System.Globalization;
using System.Linq;
using VDF.Core.Utils;

namespace VDF.Core.Selection;

/// <summary>Pure, repeatable selection planning. Checking marks a removal candidate, never a delete.</summary>
public static class SelectionPlanner {
    sealed record Evaluated(SelectionCandidate Candidate, FolderMatch Folder, DateTime? Date);

    public static SelectionPlan BuildPlan(IEnumerable<SelectionCandidate> source, SelectionOptions options) {
        var matcher = new FolderRuleMatcher(options.FolderRules, options.OverlapPolicy);
        var plan = new SelectionPlan { Profile = options.Profile };
        foreach (var group in source.GroupBy(c => c.Item.GroupId)) {
            var all = group.Select(c => new Evaluated(c, matcher.Match(c.Item.Path), DateOf(c, options.DateSource)))
                .OrderBy(c => c.Candidate.Item.Path, StringComparer.Ordinal).ToList();
            bool ignored = all.Any(c => c.Candidate.ManualChoice is ReviewChoice.Ignore or ReviewChoice.RejectGroup);
            var available = all.Where(c => c.Candidate.IsAvailable).ToList();
            var retained = available.Where(c => Protected(c) || c.Candidate.ManualChoice is ReviewChoice.Keep or ReviewChoice.Ignore).ToList();
            var eligible = retained.Count > 0 ? retained : available.Where(c => c.Candidate.ManualChoice != ReviewChoice.Remove).ToList();
            bool forcedKeeper = eligible.Count == 0 && available.Count > 0;
            if (forcedKeeper) eligible = available;
            Evaluated? keeper = null;
            string decidedBy = "no available keeper";
            bool tied = false;
            if (eligible.Count > 0) (keeper, decidedBy, tied) = Pick(eligible, options);

            foreach (var item in all) {
                var c = item.Candidate;
                bool protectedFile = Protected(item);
                bool keep = ReferenceEquals(item, keeper);
                bool remove = !ignored && c.IsAvailable && !protectedFile && !keep &&
                    c.ManualChoice is ReviewChoice.None or ReviewChoice.Remove && keeper is not null;
                string action = protectedFile ? "PROTECTED" : ignored || c.ManualChoice == ReviewChoice.Ignore ? "IGNORED"
                    : !c.IsAvailable ? "UNAVAILABLE" : remove ? "REMOVE CANDIDATE" : "KEEP";
                string date = item.Date?.ToString("O", CultureInfo.InvariantCulture) ?? "missing/invalid";
                string detail = protectedFile ? ProtectionReason(item) : c.ManualChoice == ReviewChoice.Keep ? "Manual keeper."
                    : forcedKeeper && keep ? "Final available copy retained despite removal request."
                    : c.ManualChoice == ReviewChoice.Remove && remove ? "Manual removal decision preserved."
                    : $"Keeper chosen by {decidedBy}.";
                // Even a 100% thumbnail match is perceptual, not a verified byte-identical file.
                bool review = remove || tied || forcedKeeper || c.ProtectionUncertain || item.Folder.Uncertain || !c.IsAvailable;
                string warning = remove ? " Match is not byte-verified; review before cleanup." : tied ? " Tied criteria; path order broke the tie." : string.Empty;
                if (c.Item.Flags.HasFlag(DuplicateFlags.PartialClip) || c.Item.Flags.HasFlag(DuplicateFlags.AiMatched)) warning += " Partial/AI match requires manual review.";
                plan.Decisions.Add(new SelectionDecision {
                    Path = c.Item.Path, GroupId = c.Item.GroupId, RemoveCandidate = remove,
                    IsKeeper = keep, IsProtected = protectedFile, RequiresReview = review,
                    FolderPriority = item.Folder.Priority, DateUtc = item.Date,
                    Reason = $"{action}: {detail} Priority {item.Folder.Priority}; {options.DateSource} date {date}.{warning}"
                });
                if (remove && c.Item.SizeLong > 0) plan.EstimatedSavings = checked(plan.EstimatedSavings + c.Item.SizeLong);
            }
        }
        return plan;
    }

    static bool Protected(Evaluated e) => e.Candidate.ManuallyProtected || e.Candidate.NfoProtected || e.Candidate.ProtectionUncertain || e.Folder.Protected || e.Folder.Uncertain;
    static string ProtectionReason(Evaluated e) => e.Candidate.ManuallyProtected ? "Manually protected."
        : e.Candidate.NfoProtected || e.Candidate.ProtectionUncertain ? e.Candidate.ProtectionReason
        : e.Folder.Reason;

    public static DateTime? DateOf(SelectionCandidate candidate, SelectionDateSource source) {
        var value = source switch {
            SelectionDateSource.Creation => candidate.Item.DateCreated,
            SelectionDateSource.Embedded => candidate.EmbeddedDateUtc ?? default,
            _ => candidate.Item.DateModified
        };
        // Missing is deliberately not the oldest date. File timestamps already use UTC;
        // legacy unspecified values follow the same stored-UTC convention.
        if (value == default || value.Year <= 1601) return null;
        return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    static (Evaluated Keeper, string Reason, bool Tied) Pick(List<Evaluated> items, SelectionOptions options) {
        if (options.Mode == SelectionMode.Weighted) {
            var scored = items.Select(e => (Item: e, Score: 0d)).ToArray();
            AddScore("FolderPriority", options.FolderWeight, false);
            foreach (var c in Criteria(options)) AddScore(c.Name, c.Weight, c.PreferLower);
            var ordered = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Candidate.Item.Path, StringComparer.Ordinal).ToArray();
            return (ordered[0].Item, $"weighted score {ordered[0].Score:0.###}", ordered.Length > 1 && Math.Abs(ordered[0].Score - ordered[1].Score) < 1e-10);
            void AddScore(string name, double weight, bool lower) {
                if (!double.IsFinite(weight) || weight <= 0) return;
                var values = items.Select(e => Value(e, name, options)).ToArray();
                var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                if (known.Length == 0) return;
                decimal min = known.Min(), max = known.Max();
                for (int i = 0; i < scored.Length; i++) {
                    decimal normalized = values[i] is not { } value ? 0 : max == min ? 1 : lower ? (max - value) / (max - min) : (value - min) / (max - min);
                    scored[i].Score += weight * (double)normalized;
                }
            }
        }
        var criteria = new List<QualityRanker.Criterion<Evaluated>>();
        var folderPriority = new QualityRanker.Criterion<Evaluated>("folder priority", e => e.Folder.Priority, false);
        // QualityFirst is an explicit opt-in to ranking quality across folders.
        // Other strict profiles preserve folder priority ahead of quality/dates.
        if (options.Profile != SelectionProfile.QualityFirst) criteria.Add(folderPriority);
        foreach (var c in Criteria(options)) {
            // Availability of a date/metadata beats missing values in both directions.
            criteria.Add(new(c.Name + " availability", e => Value(e, c.Name, options).HasValue ? 1 : 0, false));
            criteria.Add(new(c.Name, e => Value(e, c.Name, options) ?? 0, false, c.PreferLower));
        }
        if (options.Profile == SelectionProfile.QualityFirst) criteria.Add(folderPriority);
        var result = QualityRanker.PickKeeperWithReason(items, criteria, e => e.Candidate.Item.IsImage);
        return (result.Keeper, result.DecidedBy?.Name ?? "deterministic path order", result.DecidedBy is null && items.Count > 1);
    }

    static IEnumerable<SelectionCriterion> Criteria(SelectionOptions options) {
        if (options.Profile is SelectionProfile.NewestFirst or SelectionProfile.OldestFirst)
            yield return new() { Name = "Date", Weight = 10, PreferLower = options.Profile == SelectionProfile.OldestFirst };
        foreach (var c in options.Criteria) {
            if (options.Profile is SelectionProfile.NewestFirst or SelectionProfile.OldestFirst && c.Name == "Date") continue;
            yield return c;
        }
    }

    // Keep integer timestamps exact in strict comparisons and subtract before
    // weighted normalization; converting absolute ticks to double loses close dates.
    static decimal? Value(Evaluated e, string name, SelectionOptions options) {
        var item = e.Candidate.Item;
        return name switch {
            "FolderPriority" => e.Folder.Priority,
            "Resolution" => Pixels(item.FrameSize),
            "VideoBitrate" => item.BitRateKbs > 0 ? item.BitRateKbs : null,
            "AudioBitrate" => item.AudioBitRateKbs > 0 ? item.AudioBitRateKbs : null,
            "AudioChannels" => item.AudioChannels > 0 ? item.AudioChannels : null,
            "Codec" => Preference(item.Format, options.PreferredCodecs),
            "Hdr" => item.HdrFormatRank,
            "AudioLanguage" => (item.AudioLanguages ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(language => Preference(language, options.PreferredAudioLanguages)).DefaultIfEmpty(0).Max(),
            "Subtitles" => string.IsNullOrWhiteSpace(item.SubtitleLanguages) ? 0 : 1,
            "Date" => e.Date?.Ticks,
            "ModificationDate" => DateOf(e.Candidate, SelectionDateSource.Modification)?.Ticks,
            "CreationDate" => DateOf(e.Candidate, SelectionDateSource.Creation)?.Ticks,
            "Duration" => item.Duration > TimeSpan.Zero ? (decimal)item.Duration.TotalSeconds : null,
            "Size" => item.SizeLong > 0 ? item.SizeLong : null,
            "Container" => Preference(Path.GetExtension(item.Path).TrimStart('.'), options.PreferredContainers),
            "Metadata" => new[] { item.FrameSize, item.Format, item.AudioFormat, item.AudioLanguages, item.SubtitleLanguages, item.HdrFormat }.Count(s => !string.IsNullOrWhiteSpace(s)),
            _ => null
        };
    }

    static decimal? Pixels(string? dimensions) {
        var parts = dimensions?.Split('x');
        return parts?.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0 ? (decimal)w * h : null;
    }

    static decimal Preference(string? value, List<string> preferences) {
        int index = preferences.FindIndex(p => p.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : preferences.Count - index;
    }
}
