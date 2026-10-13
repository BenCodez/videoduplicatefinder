using System.Diagnostics;
using VDF.Core.Selection;
using VDF.Core.ViewModels;

namespace VDF.Core.Tests.Selection;

public class SelectionPlannerTests {
    static readonly Guid Group = Guid.NewGuid();
    static SelectionCandidate Candidate(string name, int day = 1, int priorityResolution = 1920) => new(new DuplicateItem {
        Path = Path.Combine(Path.GetTempPath(), "vdf-selection", name), GroupId = Group,
        DateModified = new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc),
        DateCreated = new DateTime(2025, 1, day, 0, 0, 0, DateTimeKind.Utc),
        FrameSize = $"{priorityResolution}x1080", BitRateKbs = 1000, SizeLong = 123,
        AudioChannels = 2, Duration = TimeSpan.FromMinutes(5)
    });

    static string Keeper(IEnumerable<SelectionCandidate> candidates, SelectionOptions options) =>
        SelectionPlanner.BuildPlan(candidates, options).Decisions.Single(d => d.IsKeeper).Path;

    [Theory]
    [InlineData(SelectionProfile.NewestFirst, "new.mp4")]
    [InlineData(SelectionProfile.OldestFirst, "old.mp4")]
    public void ExplicitDateProfilesKeepIntendedCopy(SelectionProfile profile, string expected) {
        var old = Candidate("old.mp4", 1); var newer = Candidate("new.mp4", 3);
        Assert.EndsWith(expected, Keeper(new[] { old, newer }, new() { Profile = profile }));
    }

    [Fact]
    public void DefaultUsesModifiedTimeInsteadOfCopiedCreationTime() {
        var old = Candidate("old.mp4", 1); var newer = Candidate("new.mp4", 2);
        old.Item.DateCreated = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(newer.Item.Path, Keeper(new[] { old, newer }, new() { Profile = SelectionProfile.NewestFirst }));
        Assert.Equal(old.Item.Path, Keeper(new[] { old, newer }, new() { Profile = SelectionProfile.NewestFirst, DateSource = SelectionDateSource.Creation }));
    }

    [Theory]
    [InlineData(SelectionProfile.NewestFirst)]
    [InlineData(SelectionProfile.OldestFirst)]
    public void MissingDatesNeverWinAsExtremeTimestamp(SelectionProfile profile) {
        var missing = Candidate("a.mp4"); missing.Item.DateModified = default;
        var known = Candidate("b.mp4", 2);
        Assert.Equal(known.Item.Path, Keeper(new[] { missing, known }, new() { Profile = profile }));
    }

    [Fact]
    public void EmbeddedDatesHandleOffsetNormalization() {
        var a = Candidate("a.mp4"); var b = Candidate("b.mp4");
        a.EmbeddedDateUtc = DateTimeOffset.Parse("2026-01-01T10:00:00+02:00").UtcDateTime;
        b.EmbeddedDateUtc = DateTimeOffset.Parse("2026-01-01T09:00:00Z").UtcDateTime;
        Assert.Equal(b.Item.Path, Keeper(new[] { a, b }, new() { Profile = SelectionProfile.NewestFirst, DateSource = SelectionDateSource.Embedded }));
    }

    [Fact]
    public void EqualDatesUseStablePathOrderRegardlessOfInputOrder() {
        var a = Candidate("a.mp4"); var b = Candidate("b.mp4");
        var options = new SelectionOptions { Profile = SelectionProfile.NewestFirst };
        Assert.Equal(a.Item.Path, Keeper(new[] { b, a }, options));
        Assert.Equal(a.Item.Path, Keeper(new[] { a, b }, options));
    }

    [Fact]
    public void StrictFolderPriorityWinsEvenAgainstBetterQuality() {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", priorityResolution: 3840);
        var options = new SelectionOptions { FolderRules = new() { new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 } } };
        Assert.Equal(archive.Item.Path, Keeper(new[] { download, archive }, options));
    }

    [Fact]
    public void WeightedSelectionCanTradeFolderPriorityForQualityButNotProtection() {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", priorityResolution: 3840);
        var options = new SelectionOptions { Mode = SelectionMode.Weighted, FolderWeight = 1,
            Criteria = new() { new() { Name = "Resolution", Weight = 10 } },
            FolderRules = new() { new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 } } };
        Assert.Equal(download.Item.Path, Keeper(new[] { archive, download }, options));
        archive.NfoProtected = true;
        var protectedPlan = SelectionPlanner.BuildPlan(new[] { archive, download }, options);
        Assert.False(protectedPlan.Decisions.Single(d => d.Path == archive.Item.Path).RemoveCandidate);
    }

    [Fact]
    public void AllProtectedCopiesAreRetained() {
        var a = Candidate("a.mp4"); a.ManuallyProtected = true;
        var b = Candidate("b.mp4"); b.NfoProtected = true;
        var c = Candidate("c.mp4"); c.ProtectionUncertain = true;
        Assert.All(SelectionPlanner.BuildPlan(new[] { a, b, c }, new()).Decisions, d => Assert.False(d.RemoveCandidate));
    }

    [Fact]
    public void OfflineProtectedKeeperDoesNotJustifyRemovingLastOnlineCopy() {
        var a = Candidate("offline.mp4"); a.NfoProtected = true; a.IsAvailable = false;
        var b = Candidate("online.mp4");
        var plan = SelectionPlanner.BuildPlan(new[] { a, b }, new());
        Assert.All(plan.Decisions, d => Assert.False(d.RemoveCandidate));
        Assert.Equal(b.Item.Path, plan.Decisions.Single(d => d.IsKeeper).Path);
    }

    [Fact]
    public void ManualKeeperAndIgnoredDecisionsSurviveAutomaticRerun() {
        var a = Candidate("a.mp4", 1); a.ManualChoice = ReviewChoice.Keep;
        var b = Candidate("b.mp4", 2); b.ManualChoice = ReviewChoice.Ignore;
        var c = Candidate("c.mp4", 3);
        var plan = SelectionPlanner.BuildPlan(new[] { a, b, c }, new() { Profile = SelectionProfile.NewestFirst });
        Assert.False(plan.Decisions[0].RemoveCandidate); Assert.False(plan.Decisions[1].RemoveCandidate);
        Assert.False(plan.Decisions.Single(d => d.Path == c.Item.Path).RemoveCandidate);
        b.ManualChoice = ReviewChoice.None;
        plan = SelectionPlanner.BuildPlan(new[] { a, b, c }, new() { Profile = SelectionProfile.NewestFirst });
        Assert.True(plan.Decisions.Single(d => d.Path == c.Item.Path).RemoveCandidate);
    }

    [Fact]
    public void RejectingGroupRetainsEveryMemberAndAllSelectedStillHasKeeper() {
        var a = Candidate("a.mp4"); var b = Candidate("b.mp4");
        a.ManualChoice = ReviewChoice.RejectGroup;
        Assert.All(SelectionPlanner.BuildPlan(new[] { a, b }, new()).Decisions, d => Assert.False(d.RemoveCandidate));
        a.ManualChoice = b.ManualChoice = ReviewChoice.Remove;
        Assert.Single(SelectionPlanner.BuildPlan(new[] { a, b }, new()).Decisions, d => !d.RemoveCandidate);
    }

    [Fact]
    public void RerunReplacesAutomaticDecisionsWhenDateChanges() {
        var a = Candidate("a.mp4", 1); var b = Candidate("b.mp4", 2);
        var options = new SelectionOptions { Profile = SelectionProfile.NewestFirst };
        Assert.Equal(b.Item.Path, Keeper(new[] { a, b }, options));
        a.Item.DateModified = a.Item.DateModified.AddDays(10);
        var next = SelectionPlanner.BuildPlan(new[] { a, b }, options);
        Assert.Equal(a.Item.Path, next.Decisions.Single(d => d.IsKeeper).Path);
        Assert.False(next.Decisions.Single(d => d.Path == a.Item.Path).RemoveCandidate);
        Assert.True(next.Decisions.Single(d => d.Path == b.Item.Path).RemoveCandidate);
    }

    [Fact]
    public void SizeDoesNotImplyQualityAndPerceptualRemovalNeedsReview() {
        var a = Candidate("a.mp4", priorityResolution: 3840); a.Item.SizeLong = 10;
        var b = Candidate("b.mp4", priorityResolution: 1280); b.Item.SizeLong = 10000;
        var plan = SelectionPlanner.BuildPlan(new[] { b, a }, new());
        Assert.Equal(a.Item.Path, plan.Decisions.Single(d => d.IsKeeper).Path);
        Assert.True(plan.Decisions.Single(d => d.RemoveCandidate).RequiresReview);
        Assert.Contains("not byte-verified", plan.Decisions.Single(d => d.RemoveCandidate).Reason);
    }

    [Fact]
    public void OrderedAudioLanguagePreferencesChooseTheBestAvailableTrackInStrictAndWeightedModes() {
        var german = Candidate("a-german.mp4"); german.Item.AudioLanguages = "ger";
        var english = Candidate("z-english.mp4"); english.Item.AudioLanguages = "fra, ENG";
        foreach (var mode in new[] { SelectionMode.StrictFolderPriority, SelectionMode.Weighted }) {
            var options = new SelectionOptions {
                Mode = mode, Profile = SelectionProfile.QualityFirst, FolderWeight = 0,
                PreferredAudioLanguages = ["eng", "ger"], Criteria = [new() { Name = "AudioLanguage", Weight = 1 }]
            };
            Assert.Equal(english.Item.Path, Keeper(new[] { german, english }, options));
            options.PreferredAudioLanguages.Reverse();
            Assert.Equal(german.Item.Path, Keeper(new[] { english, german }, options));
        }
    }

    [Fact]
    public void LargeResultSetKeepsOnePerGroupWithinBoundedTime() {
        var items = Enumerable.Range(0, 10000).SelectMany(i => {
            var a = Candidate($"{i}/a.mp4"); var b = Candidate($"{i}/b.mp4", 2);
            a.Item.GroupId = b.Item.GroupId = new Guid(i, 0, 0, new byte[8]);
            return new[] { a, b };
        }).ToArray();
        var watch = Stopwatch.StartNew();
        var plan = SelectionPlanner.BuildPlan(items, new());
        Assert.Equal(10000, plan.Decisions.Count(d => d.IsKeeper));
        Assert.Equal(10000, plan.Decisions.Count(d => d.RemoveCandidate));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"20,000 results took {watch.Elapsed}.");
    }
}
