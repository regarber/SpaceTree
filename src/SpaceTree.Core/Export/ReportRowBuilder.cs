using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;

namespace SpaceTree.Core.Export;

/// <summary>Limits that keep a report readable. Defaults suit a whole-drive scan.</summary>
public sealed class ReportOptions
{
    /// <summary>Deepest level to descend to. The folder row at the cutoff still shows its subtree total.</summary>
    public int MaxDepth { get; init; } = 5;

    /// <summary>Largest children listed individually per folder; the rest collapse into one row.</summary>
    public int MaxChildrenPerFolder { get; init; } = 12;

    /// <summary>
    /// A folder is only opened up if it holds at least this share of the report
    /// root. Without it, a report of a system drive spends thousands of rows on
    /// branches that are collectively a rounding error.
    /// </summary>
    public double MinExpandFraction { get; init; } = 0.005;

    public bool IncludeFiles { get; init; } = true;

    /// <summary>Backstop against a pathological tree. Reaching it means the other limits were too loose.</summary>
    public int MaxRows { get; init; } = 4000;

    /// <summary>Optional precomputed filter, so the report matches what is on screen.</summary>
    public FilterIndex? Filter { get; init; }
}

/// <summary>
/// Builds a <em>summarised</em> view of a tree, as opposed to
/// <see cref="ExportRowBuilder"/>, which flattens every last entry.
///
/// The distinction matters. Flattening a home directory produced 161,688 rows,
/// a 61 MB HTML file and roughly 2.4 million DOM elements — enough to hang a
/// browser tab. A report is something you skim or mail to someone, so this keeps
/// the biggest items at each level and rolls everything else into a single
/// "… and N more" row.
///
/// The invariant that makes the result trustworthy: within any folder, the sizes
/// of the listed children plus the summary row always add up to that folder's
/// total. Nothing silently vanishes, so the numbers still reconcile even though
/// most rows are gone. Use <see cref="ExportRowBuilder"/> with CSV when the
/// complete listing is actually wanted.
/// </summary>
public static class ReportRowBuilder
{
    public static IReadOnlyList<ExportRow> Build(DirectoryNode root, ReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);

        var rows = new List<ExportRow>(256);
        long threshold = options.MinExpandFraction <= 0
            ? 0
            : (long)(root.TotalSize * Math.Clamp(options.MinExpandFraction, 0, 1));

        Walk(root, root.FullPath, 0, threshold, options, rows);
        return rows;
    }

    private static void Walk(DirectoryNode node, string path, int depth, long expandThreshold,
        ReportOptions options, List<ExportRow> rows)
    {
        if (rows.Count >= options.MaxRows)
            return;

        rows.Add(new ExportRow(
            depth,
            depth == 0 ? Native.LongPath.ToDisplay(node.FullPath) : node.Name,
            path,
            IsFile: false,
            node.TotalSize,
            node.TotalAllocated,
            node.PercentOfParent,
            node.TotalFileCount,
            node.TotalDirectoryCount,
            FileTimes.ToDateTimeLocal(node.LastWriteFileTime)));

        if (depth >= options.MaxDepth)
            return;

        var filter = options.Filter;

        // Folders and files compete for the same slots, ranked by size — a 4 GB
        // disk image deserves a line more than the folder holding 200 KB of text.
        var candidates = new List<Candidate>(node.Directories.Length + 4);

        foreach (var child in node.Directories)
        {
            if (filter is not null && !filter.IsFolderVisible(child))
                continue;
            candidates.Add(new Candidate(child, default, child.TotalSize));
        }

        if (options.IncludeFiles)
        {
            foreach (var file in node.Files)
            {
                if (filter is not null && !filter.IsFileVisible(file))
                    continue;
                candidates.Add(new Candidate(null, file, file.Size));
            }
        }

        candidates.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        int listed = Math.Min(candidates.Count, Math.Max(1, options.MaxChildrenPerFolder));
        long listedSize = 0;

        for (int i = 0; i < listed; i++)
        {
            if (rows.Count >= options.MaxRows)
                return;

            var candidate = candidates[i];
            listedSize += candidate.Size;

            if (candidate.Directory is { } dir)
            {
                string childPath = Native.LongPath.Combine(path, dir.Name);

                // Descend only into branches big enough to be worth the rows.
                if (dir.TotalSize >= expandThreshold)
                {
                    Walk(dir, childPath, depth + 1, expandThreshold, options, rows);
                }
                else
                {
                    rows.Add(new ExportRow(
                        depth + 1, dir.Name, childPath, IsFile: false,
                        dir.TotalSize, dir.TotalAllocated, dir.PercentOfParent,
                        dir.TotalFileCount, dir.TotalDirectoryCount,
                        FileTimes.ToDateTimeLocal(dir.LastWriteFileTime)));
                }
                continue;
            }

            var entry = candidate.File;
            rows.Add(new ExportRow(
                depth + 1, entry.Name, Native.LongPath.Combine(path, entry.Name), IsFile: true,
                entry.Size, entry.Allocated,
                node.TotalSize > 0 ? (double)entry.Size / node.TotalSize : 0,
                1, 0, FileTimes.ToDateTimeLocal(entry.LastWriteFileTime)));
        }

        AppendRemainder(node, path, depth, options, candidates, listed, listedSize, rows);
    }

    /// <summary>
    /// Emits the single row standing for everything not listed, sized so the
    /// folder's children always sum to its total.
    /// </summary>
    private static void AppendRemainder(DirectoryNode node, string path, int depth, ReportOptions options,
        List<Candidate> candidates, int listed, long listedSize, List<ExportRow> rows)
    {
        if (rows.Count >= options.MaxRows)
            return;

        // Deriving the remainder by subtraction rather than by adding up what was
        // skipped means it also absorbs anything the filter hid and, when files
        // are excluded, the bytes they hold.
        long remainderSize = Math.Max(0, node.TotalSize - listedSize);

        int hiddenCandidates = candidates.Count - listed;
        long hiddenFiles = options.IncludeFiles ? 0 : node.Files.Length;
        long remainderCount = hiddenCandidates + hiddenFiles;

        if (remainderCount <= 0 || remainderSize <= 0)
            return;

        long remainderAllocated = Math.Max(0, node.TotalAllocated - SumAllocated(candidates, listed));

        rows.Add(new ExportRow(
            depth + 1,
            remainderCount == 1 ? "and 1 more item" : $"and {remainderCount:N0} more items",
            path,
            IsFile: false,
            remainderSize,
            remainderAllocated,
            node.TotalSize > 0 ? (double)remainderSize / node.TotalSize : 0,
            0, 0,
            DateTime.MinValue)
        {
            IsSummary = true,
        });
    }

    private static long SumAllocated(List<Candidate> candidates, int count)
    {
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var candidate = candidates[i];
            total += candidate.Directory is { } dir ? dir.TotalAllocated : candidate.File.Allocated;
        }
        return total;
    }

    /// <summary>A child competing for a slot: either a directory or a file, never both.</summary>
    private readonly struct Candidate
    {
        public Candidate(DirectoryNode? directory, in FileEntry file, long size)
        {
            Directory = directory;
            File = file;
            Size = size;
        }

        public DirectoryNode? Directory { get; }
        public FileEntry File { get; }
        public long Size { get; }
    }
}
