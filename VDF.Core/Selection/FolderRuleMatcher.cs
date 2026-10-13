using System.Linq;
using System.IO.Enumeration;
using VDF.Core.Utils;

namespace VDF.Core.Selection;

public sealed record FolderMatch(int Priority, bool Protected, bool Uncertain, string Reason);

/// <summary>Directory-boundary matching. Protection is the union of all matching rules.</summary>
public sealed class FolderRuleMatcher {
    readonly List<(FolderPriorityRule Rule, string Directory)> rules = new();
    readonly FolderOverlapPolicy policy;
    readonly bool invalidProtection;
    static bool IgnoreCase => PathComparer.ForCurrentPlatform == StringComparer.OrdinalIgnoreCase;
    static StringComparison Comparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FolderRuleMatcher(IEnumerable<FolderPriorityRule> source, FolderOverlapPolicy policy) {
        this.policy = policy;
        foreach (var rule in source.Where(r => r.Enabled)) {
            try {
                if (!Path.IsPathFullyQualified(rule.Directory)) throw new ArgumentException("Folder rule must be an absolute path.");
                rules.Add((rule, Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.Directory))));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
                invalidProtection |= rule.ProtectAll;
            }
        }
    }

    public FolderMatch Match(string path) {
        try {
            if (!Path.IsPathFullyQualified(path)) return new(0, false, true, "File path is not absolute.");
            path = Path.GetFullPath(path);
            (FolderPriorityRule Rule, string Directory)? chosen = null;
            bool protect = false;
            foreach (var entry in rules) {
                string prefix = Path.EndsInDirectorySeparator(entry.Directory) ? entry.Directory : entry.Directory + Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, Comparison)) continue;
                string relative = path[prefix.Length..];
                if (!entry.Rule.Recursive && relative.IndexOf(Path.DirectorySeparatorChar) >= 0) continue;
                if (entry.Rule.Pattern.Length > 0 && !FileSystemName.MatchesSimpleExpression(entry.Rule.Pattern, relative, IgnoreCase)) continue;
                protect |= entry.Rule.ProtectAll;
                if (chosen is not { } old || (policy == FolderOverlapPolicy.MostSpecific
                    ? entry.Directory.Length > old.Directory.Length || entry.Directory.Length == old.Directory.Length && entry.Rule.Priority > old.Rule.Priority
                    : entry.Rule.Priority > old.Rule.Priority || entry.Rule.Priority == old.Rule.Priority && entry.Directory.Length > old.Directory.Length)) chosen = entry;
            }
            return new(chosen?.Rule.Priority ?? 0, protect, invalidProtection,
                invalidProtection ? "An enabled protected folder rule is invalid; repair it before cleanup."
                : protect ? "Protected folder rule." : chosen is null ? "No matching folder rule; priority 0." : $"Folder priority {chosen.Value.Rule.Priority}.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return new(0, false, true, "Invalid file path; review required.");
        }
    }
}
