using VDF.Core.Selection;

namespace VDF.Core.Tests.Selection;

public class FolderRuleMatcherTests {
    static readonly string Root = Path.Combine(Path.GetTempPath(), "vdf-folders");
    static string At(string relative) => Path.Combine(Root, relative);
    [Fact]
    public void SimilarPrefixDoesNotMatchAndLinuxCaseIsSensitive() {
        var matcher = new FolderRuleMatcher(new[] { new FolderPriorityRule { Directory = At("Movies"), Priority = 500 } }, FolderOverlapPolicy.MostSpecific);
        Assert.Equal(0, matcher.Match(At("Movies-extra/a.mp4")).Priority);
        Assert.Equal(500, matcher.Match(At("Movies/a.mp4")).Priority);
        if (OperatingSystem.IsLinux()) Assert.Equal(0, matcher.Match(At("movies/a.mp4")).Priority);
        else if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.Equal(500, matcher.Match(At("movies/a.mp4")).Priority);
    }
    [Fact]
    public void NestedSpecificRuleNeverUndoesParentProtection() {
        var matcher = new FolderRuleMatcher(new[] {
            new FolderPriorityRule { Directory = At("Movies"), Priority = 500, ProtectAll = true },
            new FolderPriorityRule { Directory = At("Movies/Archive"), Priority = 10 }
        }, FolderOverlapPolicy.MostSpecific);
        var match = matcher.Match(At("Movies/Archive/a.mp4"));
        Assert.Equal(10, match.Priority); Assert.True(match.Protected);
    }
    [Fact]
    public void HighestPriorityOverlapAndNonRecursivePatternAreConfigurable() {
        var matcher = new FolderRuleMatcher(new[] {
            new FolderPriorityRule { Directory = Root, Priority = 500 },
            new FolderPriorityRule { Directory = At("Movies"), Priority = 10 },
            new FolderPriorityRule { Directory = At("Movies"), Priority = 1000, Recursive = false, Pattern = "*.mkv" }
        }, FolderOverlapPolicy.HighestPriority);
        Assert.Equal(1000, matcher.Match(At("Movies/a.mkv")).Priority);
        Assert.Equal(500, matcher.Match(At("Movies/sub/a.mkv")).Priority);
        Assert.Equal(500, matcher.Match(At("Movies/a.mp4")).Priority);
    }
    [Fact]
    public void InvalidProtectedRulesFailClosedAndDotSegmentsNormalize() {
        var invalid = new FolderRuleMatcher(new[] { new FolderPriorityRule { Directory = "relative", ProtectAll = true } }, FolderOverlapPolicy.MostSpecific);
        Assert.True(invalid.Match(At("a.mp4")).Uncertain);
        var matcher = new FolderRuleMatcher(new[] { new FolderPriorityRule { Directory = At("Movies"), Priority = 2 } }, FolderOverlapPolicy.MostSpecific);
        Assert.Equal(2, matcher.Match(At("Movies/sub/../a.mp4")).Priority);
    }
}
