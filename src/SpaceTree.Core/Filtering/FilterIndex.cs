using SpaceTree.Core.Model;

namespace SpaceTree.Core.Filtering;

/// <summary>
/// Precomputed answer to "does anything under this folder match the filter?".
///
/// A quick-filter box has to keep showing the ancestors of a match, otherwise
/// typing "*.iso" empties the window instead of revealing where the ISOs live.
/// Answering that per folder on demand is O(subtree) and turns filtering of a
/// system drive into a multi-second stall, so the whole tree is swept once into
/// a set of folders that contain a match, and the view then asks in O(1).
///
/// A filter with no name pattern needs no index at all — the size rules are
/// evaluated directly and every folder passes the name test.
/// </summary>
public sealed class FilterIndex
{
    private readonly NodeFilter _filter;
    private readonly HashSet<DirectoryNode>? _matching;

    private FilterIndex(NodeFilter filter, HashSet<DirectoryNode>? matching)
    {
        _filter = filter;
        _matching = matching;
    }

    public NodeFilter Filter => _filter;

    /// <summary>True when the index actually restricts anything.</summary>
    public bool IsActive => _filter.IsActive;

    public static FilterIndex Build(DirectoryNode root, NodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(filter);

        if (!filter.HasNameFilter)
            return new FilterIndex(filter, null);

        // DescendantsAndSelf is pre-order, so a parent always precedes its
        // children. Walking the result backwards therefore visits every child
        // before its parent — a post-order sweep without the recursion, which
        // matters because directory trees can nest far deeper than the stack
        // would tolerate.
        var all = new List<DirectoryNode>();
        foreach (var node in root.DescendantsAndSelf())
            all.Add(node);

        var matching = new HashSet<DirectoryNode>();

        for (int i = all.Count - 1; i >= 0; i--)
        {
            var node = all[i];

            if (filter.MatchesName(node.Name) || AnyFileMatches(node, filter) || AnyChildMatches(node, matching))
                matching.Add(node);
        }

        // The scan root is the user's anchor: hiding it would leave an empty
        // window with no way back.
        matching.Add(root);

        return new FilterIndex(filter, matching);
    }

    /// <summary>True when a folder row should appear in the tree.</summary>
    public bool IsFolderVisible(DirectoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_filter.MatchesSize(node.TotalSize, isFolder: true))
            return false;

        return _matching is null || _matching.Contains(node);
    }

    /// <summary>True when a file row should appear in the tree.</summary>
    public bool IsFileVisible(in FileEntry file) =>
        _filter.MatchesSize(file.Size, isFolder: false) && _filter.MatchesName(file.Name);

    private static bool AnyFileMatches(DirectoryNode node, NodeFilter filter)
    {
        var files = node.Files;
        for (int i = 0; i < files.Length; i++)
            if (filter.MatchesName(files[i].Name))
                return true;
        return false;
    }

    private static bool AnyChildMatches(DirectoryNode node, HashSet<DirectoryNode> matching)
    {
        var dirs = node.Directories;
        for (int i = 0; i < dirs.Length; i++)
            if (matching.Contains(dirs[i]))
                return true;
        return false;
    }
}
