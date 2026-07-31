using System.Text;
using System.Text.RegularExpressions;

namespace SpaceTree.Core.Filtering;

public enum FilterMode
{
    /// <summary>DOS-style wildcards. A pattern with no wildcard is treated as "contains".</summary>
    Wildcard,
    /// <summary>.NET regular expression, matched against the item name.</summary>
    Regex,
}

/// <summary>
/// Name / size predicate used by the quick filter box. Immutable and cheap to
/// evaluate: the pattern is compiled once and reused across the whole tree walk.
/// </summary>
public sealed class NodeFilter
{
    public static readonly NodeFilter None = new(null, FilterMode.Wildcard, 0, false, false);

    private readonly Regex? _regex;

    private NodeFilter(Regex? regex, FilterMode mode, long minSize, bool hideEmpty, bool invalidPattern)
    {
        _regex = regex;
        Mode = mode;
        MinimumSize = minSize;
        HideEmptyFolders = hideEmpty;
        HasInvalidPattern = invalidPattern;
    }

    public FilterMode Mode { get; }

    /// <summary>Items whose total size is below this are hidden. 0 disables the check.</summary>
    public long MinimumSize { get; }

    /// <summary>Hide folders that hold no bytes at all.</summary>
    public bool HideEmptyFolders { get; }

    /// <summary>True when the user typed a regex that does not compile; the name test then passes everything.</summary>
    public bool HasInvalidPattern { get; }

    public bool HasNameFilter => _regex is not null;

    public bool IsActive => HasNameFilter || MinimumSize > 0 || HideEmptyFolders;

    public static NodeFilter Create(string? pattern, FilterMode mode = FilterMode.Wildcard,
        long minimumSize = 0, bool hideEmptyFolders = false)
    {
        pattern = pattern?.Trim();
        if (string.IsNullOrEmpty(pattern))
            return new NodeFilter(null, mode, minimumSize, hideEmptyFolders, false);

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        try
        {
            Regex regex = mode == FilterMode.Regex
                ? new Regex(pattern, options)
                : new Regex(WildcardToRegex(pattern), options);
            return new NodeFilter(regex, mode, minimumSize, hideEmptyFolders, false);
        }
        catch (ArgumentException)
        {
            // Half-typed regex: keep the size filters working rather than showing nothing.
            return new NodeFilter(null, mode, minimumSize, hideEmptyFolders, true);
        }
        catch (RegexMatchTimeoutException)
        {
            return new NodeFilter(null, mode, minimumSize, hideEmptyFolders, true);
        }
    }

    /// <summary>Name test only. Size rules are applied separately because they differ for files and folders.</summary>
    public bool MatchesName(string name)
    {
        if (_regex is null)
            return true;
        try
        {
            return _regex.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Applies the size rules to an item of the given total size.</summary>
    public bool MatchesSize(long totalSize, bool isFolder)
    {
        if (MinimumSize > 0 && totalSize < MinimumSize)
            return false;
        if (HideEmptyFolders && isFolder && totalSize <= 0)
            return false;
        return true;
    }

    public bool Matches(string name, long totalSize, bool isFolder) =>
        MatchesSize(totalSize, isFolder) && MatchesName(name);

    /// <summary>
    /// Translates DOS wildcards to a regex. A pattern containing no wildcard is
    /// treated as a substring search, which is what people expect from a filter
    /// box: typing "log" should find "setup.log.1".
    /// </summary>
    internal static string WildcardToRegex(string pattern)
    {
        // Semicolons separate independent patterns: "*.log;*.tmp".
        string[] alternatives = pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (alternatives.Length == 0)
            alternatives = new[] { pattern };

        var sb = new StringBuilder(pattern.Length * 2 + 8);
        sb.Append("^(?:");

        for (int i = 0; i < alternatives.Length; i++)
        {
            if (i > 0)
                sb.Append('|');
            AppendAlternative(sb, alternatives[i]);
        }

        sb.Append(")$");
        return sb.ToString();
    }

    private static void AppendAlternative(StringBuilder sb, string pattern)
    {
        // No wildcard at all means "contains", which is what a quick filter box
        // should do: typing "log" must find "setup.log.1".
        bool hasWildcard = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;

        if (!hasWildcard)
            sb.Append(".*");

        foreach (char c in pattern)
        {
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        if (!hasWildcard)
            sb.Append(".*");
    }
}
