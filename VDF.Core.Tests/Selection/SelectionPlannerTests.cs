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

    [Theory]
    [InlineData(SelectionProfile.NewestFirst, 1, true)]
    [InlineData(SelectionProfile.NewestFirst, -1, false)]
    [InlineData(SelectionProfile.OldestFirst, 1, false)]
    [InlineData(SelectionProfile.OldestFirst, -1, true)]
    public void DateProfilesPreserveSingleTickDifferencesInStrictAndWeightedModes(SelectionProfile profile, int tickOffset, bool keepChanged) {
        var first = Candidate("a-first.mp4"); var changed = Candidate("z-changed.mp4");
        changed.Item.DateModified = first.Item.DateModified.AddTicks(tickOffset);
        changed.Item.DateCreated = first.Item.DateCreated.AddTicks(tickOffset);
        first.EmbeddedDateUtc = first.Item.DateModified;
        changed.EmbeddedDateUtc = changed.Item.DateModified;
        foreach (var mode in Enum.GetValues<SelectionMode>()) {
            foreach (var source in Enum.GetValues<SelectionDateSource>()) {
                var options = new SelectionOptions { Profile = profile, Mode = mode, DateSource = source, FolderWeight = 0, Criteria = [] };
                Assert.Equal(keepChanged ? changed.Item.Path : first.Item.Path, Keeper([changed, first], options));
            }
        }
    }

    [Theory]
    [InlineData("ModificationDate", false)]
    [InlineData("ModificationDate", true)]
    [InlineData("CreationDate", false)]
    [InlineData("CreationDate", true)]
    public void ExplicitDateCriteriaPreserveSingleTickDifferencesAndMissingDateAvailability(string criterion, bool preferLower) {
        var first = Candidate("a-first.mp4"); var preferred = Candidate("z-preferred.mp4");
        var missing = Candidate("0-missing.mp4");
        preferred.Item.DateModified = first.Item.DateModified.AddTicks(preferLower ? -1 : 1);
        preferred.Item.DateCreated = first.Item.DateCreated.AddTicks(preferLower ? -1 : 1);
        missing.Item.DateModified = missing.Item.DateCreated = default;
        foreach (var mode in Enum.GetValues<SelectionMode>()) {
            foreach (var profile in new[] { SelectionProfile.Custom, SelectionProfile.QualityFirst }) {
                var options = new SelectionOptions {
                    Profile = profile, Mode = mode, FolderWeight = 0,
                    Criteria = [new() { Name = criterion, PreferLower = preferLower }]
                };
                Assert.Equal(preferred.Item.Path, Keeper([missing, first, preferred], options));
            }
        }
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
        foreach (var mode in Enum.GetValues<SelectionMode>())
            Assert.Equal(known.Item.Path, Keeper(new[] { missing, known }, new() { Profile = profile, Mode = mode, FolderWeight = 0, Criteria = [] }));
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
        foreach (var mode in Enum.GetValues<SelectionMode>()) {
            var options = new SelectionOptions { Profile = SelectionProfile.NewestFirst, Mode = mode, FolderWeight = 0, Criteria = [] };
            Assert.Equal(a.Item.Path, Keeper(new[] { b, a }, options));
            Assert.Equal(a.Item.Path, Keeper(new[] { a, b }, options));
        }
    }

    [Theory]
    [InlineData(SelectionProfile.FolderPriorityFirst)]
    [InlineData(SelectionProfile.NewestFirst)]
    [InlineData(SelectionProfile.OldestFirst)]
    [InlineData(SelectionProfile.Custom)]
    public void StrictFolderPriorityWinsEvenAgainstBetterQuality(SelectionProfile profile) {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", profile == SelectionProfile.OldestFirst ? 1 : 3, priorityResolution: 3840);
        archive.Item.DateModified = archive.Item.DateModified.AddDays(1);
        var options = new SelectionOptions { Profile = profile,
            FolderRules = new() { new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 } } };
        Assert.Equal(archive.Item.Path, Keeper(new[] { download, archive }, options));
    }

    [Fact]
    public void QualityFirstRanksQualityAcrossFoldersWhileDefaultPreservesFolderPriority() {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", priorityResolution: 3840);
        var options = new SelectionOptions {
            FolderRules = [new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 }]
        };
        Assert.Equal(SelectionProfile.FolderPriorityFirst, options.Profile);
        Assert.Equal(archive.Item.Path, Keeper([download, archive], options));
        options.Profile = SelectionProfile.QualityFirst;
        var plan = SelectionPlanner.BuildPlan([archive, download], options);
        Assert.Equal(download.Item.Path, plan.Decisions.Single(d => d.IsKeeper).Path);
        Assert.Contains("Keeper chosen by Resolution.", plan.Decisions.Single(d => d.IsKeeper).Reason);
        Assert.True(plan.Decisions.Single(d => d.Path == archive.Item.Path).RemoveCandidate);
    }

    [Fact]
    public void QualityFirstUsesConfiguredCriterionOrderRatherThanWeights() {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", priorityResolution: 3840);
        archive.Item.BitRateKbs = 2000;
        var options = new SelectionOptions {
            Profile = SelectionProfile.QualityFirst,
            Criteria = [new() { Name = "Resolution", Weight = 0 }, new() { Name = "VideoBitrate", Weight = 100 }],
            FolderRules = [new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 }]
        };
        Assert.Equal(download.Item.Path, Keeper([archive, download], options));
        options.Criteria.Reverse();
        Assert.Equal(archive.Item.Path, Keeper([download, archive], options));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QualityFirstFallsBackToFolderPriorityThenStablePathOrder(bool includeQualityCriteria) {
        var archiveA = Candidate("archive/a.mp4");
        var archiveB = Candidate("archive/b.mp4");
        var download = Candidate("0-downloads/a.mp4");
        var options = new SelectionOptions {
            Profile = SelectionProfile.QualityFirst,
            Criteria = includeQualityCriteria ? [new() { Name = "Resolution" }] : [],
            FolderRules = [new() { Directory = Path.GetDirectoryName(archiveA.Item.Path)!, Priority = 500 }]
        };
        var priorityPlan = SelectionPlanner.BuildPlan([download, archiveB], options);
        Assert.Equal(archiveB.Item.Path, priorityPlan.Decisions.Single(d => d.IsKeeper).Path);
        Assert.Contains("Keeper chosen by folder priority.", priorityPlan.Decisions.Single(d => d.IsKeeper).Reason);
        Assert.Equal(archiveA.Item.Path, Keeper([download, archiveB, archiveA], options));
        var tiedPlan = SelectionPlanner.BuildPlan([archiveA, archiveB, download], options);
        Assert.Equal(archiveA.Item.Path, tiedPlan.Decisions.Single(d => d.IsKeeper).Path);
        Assert.Contains("Tied criteria; path order broke the tie.", tiedPlan.Decisions.Single(d => d.IsKeeper).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualityFirstPreservesPreferLowerAndMissingCriterionAvailability(bool preferLower) {
        var preferred = Candidate("downloads/z-preferred.mp4", priorityResolution: preferLower ? 1280 : 3840);
        var other = Candidate("archive/a-other.mp4");
        var missing = Candidate("archive/0-missing.mp4");
        missing.Item.FrameSize = string.Empty;
        var options = new SelectionOptions {
            Profile = SelectionProfile.QualityFirst,
            Criteria = [new() { Name = "Resolution", PreferLower = preferLower }],
            FolderRules = [new() { Directory = Path.GetDirectoryName(other.Item.Path)!, Priority = 500 }]
        };
        Assert.Equal(preferred.Item.Path, Keeper([missing, other, preferred], options));
        Assert.Equal(other.Item.Path, Keeper([missing, other], options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualityFirstPreservesSingleTickDateDifferencesAcrossFolderPriorities(bool preferLower) {
        var archive = Candidate("archive/a.mp4");
        var download = Candidate("downloads/z.mp4");
        download.Item.DateModified = archive.Item.DateModified.AddTicks(preferLower ? -1 : 1);
        download.Item.DateCreated = archive.Item.DateCreated.AddTicks(preferLower ? -1 : 1);
        archive.EmbeddedDateUtc = archive.Item.DateModified;
        download.EmbeddedDateUtc = download.Item.DateModified;
        foreach (var source in Enum.GetValues<SelectionDateSource>()) {
            var options = new SelectionOptions {
                Profile = SelectionProfile.QualityFirst, DateSource = source,
                Criteria = [new() { Name = "Date", PreferLower = preferLower }],
                FolderRules = [new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 }]
            };
            Assert.Equal(download.Item.Path, Keeper([archive, download], options));
        }
    }

    [Theory]
    [InlineData("ManualKeep")]
    [InlineData("ManualProtection")]
    [InlineData("NfoProtection")]
    [InlineData("UncertainProtection")]
    [InlineData("FolderProtection")]
    public void QualityFirstCannotOverrideKeeperOrProtectionGuards(string guard) {
        var retained = Candidate("protected/a.mp4", priorityResolution: 1280);
        var better = Candidate("downloads/a.mp4", priorityResolution: 3840);
        var options = new SelectionOptions { Profile = SelectionProfile.QualityFirst };
        switch (guard) {
            case "ManualKeep": retained.ManualChoice = ReviewChoice.Keep; break;
            case "ManualProtection": retained.ManuallyProtected = true; break;
            case "NfoProtection": retained.NfoProtected = true; break;
            case "UncertainProtection": retained.ProtectionUncertain = true; break;
            case "FolderProtection": options.FolderRules = [new() {
                Directory = Path.GetDirectoryName(retained.Item.Path)!, ProtectAll = true
            }]; break;
        }
        var plan = SelectionPlanner.BuildPlan([better, retained], options);
        Assert.Equal(retained.Item.Path, plan.Decisions.Single(d => d.IsKeeper).Path);
        Assert.False(plan.Decisions.Single(d => d.Path == retained.Item.Path).RemoveCandidate);
        Assert.True(plan.Decisions.Single(d => d.Path == better.Item.Path).RemoveCandidate);
    }

    [Theory]
    [InlineData(ReviewChoice.Ignore)]
    [InlineData(ReviewChoice.RejectGroup)]
    public void QualityFirstPreservesIgnoredOrRejectedGroups(ReviewChoice choice) {
        var ignored = Candidate("a.mp4", priorityResolution: 1280);
        ignored.ManualChoice = choice;
        var better = Candidate("b.mp4", priorityResolution: 3840);
        var plan = SelectionPlanner.BuildPlan([better, ignored], new() { Profile = SelectionProfile.QualityFirst });
        Assert.All(plan.Decisions, decision => Assert.False(decision.RemoveCandidate));
    }

    [Theory]
    [InlineData(SelectionProfile.FolderPriorityFirst)]
    [InlineData(SelectionProfile.QualityFirst)]
    public void WeightedModeCombinesFolderAndQualityWeightsRegardlessOfProfile(SelectionProfile profile) {
        var archive = Candidate("archive/a.mp4", priorityResolution: 1280);
        var download = Candidate("downloads/a.mp4", priorityResolution: 3840);
        var options = new SelectionOptions {
            Mode = SelectionMode.Weighted, Profile = profile, FolderWeight = 20,
            Criteria = [new() { Name = "Resolution", Weight = 10 }],
            FolderRules = [new() { Directory = Path.GetDirectoryName(archive.Item.Path)!, Priority = 500 }]
        };
        Assert.Equal(archive.Item.Path, Keeper([download, archive], options));
        options.FolderWeight = 1;
        Assert.Equal(download.Item.Path, Keeper([archive, download], options));
        archive.NfoProtected = true;
        Assert.Equal(archive.Item.Path, Keeper([download, archive], options));
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
