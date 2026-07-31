using System.Text;

namespace SpaceTree.Core.Native;

/// <summary>
/// Helpers for building paths that survive the legacy MAX_PATH (260 char) limit.
/// The scanner always talks to Win32 through the extended-length "\\?\" prefix,
/// which removes the limit and disables path normalisation.
/// </summary>
public static class LongPath
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>Returns a path safe to hand to the Win32 wide APIs.</summary>
    public static string ToExtended(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        // UNC: \\server\share -> \\?\UNC\server\share
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return ExtendedUncPrefix + path.Substring(2);

        // Only rooted paths can be extended; relative paths must be resolved first.
        if (!IsRooted(path))
        {
            try { path = Path.GetFullPath(path); }
            catch { return path; }
        }

        return ExtendedPrefix + path;
    }

    /// <summary>Strips any extended-length prefix so a path is presentable to the user.</summary>
    public static string ToDisplay(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
            return @"\\" + path.Substring(ExtendedUncPrefix.Length);
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path.Substring(ExtendedPrefix.Length);
        return path;
    }

    private static bool IsRooted(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);

    /// <summary>Joins a directory and a child name without allocating through Path.Combine's checks.</summary>
    public static string Combine(string directory, string name)
    {
        if (directory.Length == 0)
            return name;

        char last = directory[directory.Length - 1];
        if (last == '\\' || last == '/')
            return string.Concat(directory, name);

        return string.Concat(directory, "\\", name);
    }

    /// <summary>Builds the search pattern ("dir\*") used by FindFirstFileEx.</summary>
    public static string SearchPattern(string directory) => Combine(directory, "*");

    /// <summary>Normalises a user-supplied path for use as a scan root.</summary>
    public static string NormalizeRoot(string path)
    {
        path = ToDisplay(path.Trim().Trim('"'));

        if (path.Length == 2 && path[1] == ':')
            path += "\\";

        if (path.Length > 3 && (path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)))
            path = path.TrimEnd('\\', '/');

        return path.Replace('/', '\\');
    }

    /// <summary>Reconstructs a full path from a chain of names (root first).</summary>
    public static string Build(IReadOnlyList<string> segments)
    {
        var sb = new StringBuilder(64);
        for (int i = 0; i < segments.Count; i++)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '\\')
                sb.Append('\\');
            sb.Append(segments[i]);
        }
        return sb.ToString();
    }
}
