using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;

namespace SpaceTree.Core.Export;

/// <summary>One flattened line of the tree, ready for any writer.</summary>
public readonly record struct ExportRow(
    int Depth,
    string Name,
    string FullPath,
    bool IsFile,
    long Size,
    long Allocated,
    double PercentOfParent,
    long FileCount,
    long FolderCount,
    DateTime LastModified)
{
    /// <summary>
    /// True for the synthetic "and N more items" row that <see cref="ReportRowBuilder"/>
    /// emits to account for children it did not list. Such a row stands for a
    /// group rather than a real entry, so <see cref="FullPath"/> points at the
    /// containing folder and writers should not present it as something on disk.
    /// </summary>
    public bool IsSummary { get; init; }
}

public sealed class ExportOptions
{
    /// <summary>How many levels below the exported root to include. 0 means unlimited.</summary>
    public int MaxDepth { get; init; }

    /// <summary>Include individual files, not just folders.</summary>
    public bool IncludeFiles { get; init; }

    /// <summary>Optional name/size filter, matching what the user sees on screen.</summary>
    public NodeFilter Filter { get; init; } = NodeFilter.None;

    /// <summary>Sort each sibling group by descending size (matching the default view).</summary>
    public bool SortBySizeDescending { get; init; } = true;

    /// <summary>Cap on emitted rows, so an accidental full-drive CSV cannot fill the disk.</summary>
    public int MaxRows { get; init; } = 1_000_000;
}

/// <summary>Flattens a <see cref="DirectoryNode"/> subtree into <see cref="ExportRow"/> values.</summary>
public static class ExportRowBuilder
{
    public static IEnumerable<ExportRow> Build(DirectoryNode root, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);

        int emitted = 0;
        foreach (var row in Walk(root, root.FullPath, 0, options))
        {
            if (++emitted > options.MaxRows)
                yield break;
            yield return row;
        }
    }

    private static IEnumerable<ExportRow> Walk(DirectoryNode node, string path, int depth, ExportOptions options)
    {
        yield return new ExportRow(
            depth,
            depth == 0 ? node.FullPath : node.Name,
            path,
            IsFile: false,
            node.TotalSize,
            node.TotalAllocated,
            node.PercentOfParent,
            node.TotalFileCount,
            node.TotalDirectoryCount,
            FileTimes.ToDateTimeLocal(node.LastWriteFileTime));

        if (options.MaxDepth > 0 && depth >= options.MaxDepth)
            yield break;

        var filter = options.Filter;

        var dirs = node.Directories;
        if (dirs.Length > 0)
        {
            var ordered = options.SortBySizeDescending
                ? dirs.OrderByDescending(d => d.TotalSize).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                : dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var child in ordered)
            {
                if (!filter.MatchesSize(child.TotalSize, isFolder: true))
                    continue;
                if (filter.HasNameFilter && !SubtreeMatches(child, filter))
                    continue;

                foreach (var row in Walk(child, Native.LongPath.Combine(path, child.Name), depth + 1, options))
                    yield return row;
            }
        }

        if (!options.IncludeFiles)
            yield break;

        var files = node.Files;
        if (files.Length == 0)
            yield break;

        IEnumerable<FileEntry> fileSeq = options.SortBySizeDescending
            ? files.OrderByDescending(f => f.Size).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            : files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        long parentTotal = node.TotalSize;
        foreach (var file in fileSeq)
        {
            if (!filter.MatchesSize(file.Size, isFolder: false))
                continue;
            if (!filter.MatchesName(file.Name))
                continue;

            yield return new ExportRow(
                depth + 1,
                file.Name,
                Native.LongPath.Combine(path, file.Name),
                IsFile: true,
                file.Size,
                file.Allocated,
                parentTotal > 0 ? (double)file.Size / parentTotal : 0,
                1,
                0,
                FileTimes.ToDateTimeLocal(file.LastWriteFileTime));
        }
    }

    /// <summary>True when the folder itself or anything under it matches the name filter.</summary>
    private static bool SubtreeMatches(DirectoryNode node, NodeFilter filter)
    {
        if (filter.MatchesName(node.Name))
            return true;

        foreach (var descendant in node.DescendantsAndSelf())
        {
            if (filter.MatchesName(descendant.Name))
                return true;
            foreach (var file in descendant.Files)
                if (filter.MatchesName(file.Name))
                    return true;
        }
        return false;
    }
}
