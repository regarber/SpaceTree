using SpaceTree.Core.Model;

namespace SpaceTree.Core.Sorting;

/// <summary>The columns a user can sort the tree by.</summary>
public enum SortColumn
{
    Name,
    Size,
    Allocated,
    PercentOfParent,
    Files,
    Folders,
    LastModified,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// The sortable projection of one tree row, whether it came from a directory or
/// a file. Folders and files share a sibling group on screen, so they have to be
/// comparable against each other; projecting both into one key is what makes a
/// single comparison function possible.
/// </summary>
public readonly struct RowKey
{
    public RowKey(string name, long size, long allocated, long files, long folders, long lastWriteFileTime, bool isFolder)
    {
        Name = name;
        Size = size;
        Allocated = allocated;
        Files = files;
        Folders = folders;
        LastWriteFileTime = lastWriteFileTime;
        IsFolder = isFolder;
    }

    public string Name { get; }
    public long Size { get; }
    public long Allocated { get; }
    public long Files { get; }
    public long Folders { get; }
    public long LastWriteFileTime { get; }
    public bool IsFolder { get; }

    public static RowKey FromDirectory(DirectoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new RowKey(node.Name, node.TotalSize, node.TotalAllocated,
            node.TotalFileCount, node.TotalDirectoryCount, node.LastWriteFileTime, isFolder: true);
    }

    public static RowKey FromFile(in FileEntry file) =>
        new(file.Name, file.Size, file.Allocated, 0, 0, file.LastWriteFileTime, isFolder: false);
}

/// <summary>
/// Sibling ordering for the tree view.
///
/// Sorting always happens within a sibling group rather than across the whole
/// tree, because a hierarchy only stays meaningful if children remain under their
/// parent.
///
/// This lives in Core rather than the view layer purely so it can be tested
/// without spinning up WPF.
/// </summary>
public static class NodeSort
{
    /// <summary>
    /// Orders two sibling rows. Ties break on folder-before-file and then on
    /// name, which makes the ordering total — without that, two folders of equal
    /// size would swap places every time the user re-sorted.
    /// </summary>
    public static int Compare(in RowKey a, in RowKey b, SortColumn column, SortDirection direction)
    {
        int cmp = column switch
        {
            SortColumn.Name => CompareNames(a.Name, b.Name),
            // Siblings share a parent, so percent-of-parent ranks identically to
            // size while avoiding a division per comparison.
            SortColumn.Size or SortColumn.PercentOfParent => a.Size.CompareTo(b.Size),
            SortColumn.Allocated => a.Allocated.CompareTo(b.Allocated),
            SortColumn.Files => a.Files.CompareTo(b.Files),
            SortColumn.Folders => a.Folders.CompareTo(b.Folders),
            SortColumn.LastModified => a.LastWriteFileTime.CompareTo(b.LastWriteFileTime),
            _ => 0,
        };

        if (cmp != 0)
            return direction == SortDirection.Descending ? -cmp : cmp;

        // Tie-breakers keep their natural direction: flipping the sort should not
        // shuffle equal-sized items around.
        if (a.IsFolder != b.IsFolder)
            return a.IsFolder ? -1 : 1;

        return CompareNames(a.Name, b.Name);
    }

    /// <summary>
    /// Ordinal-ignore-case with digit awareness, so "img2" precedes "img10" the
    /// way Explorer orders them.
    /// </summary>
    public static int CompareNames(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];

            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                // Leading zeros carry no numeric weight, so "007" and "7" compare
                // equal here and fall through to the length tie-break below.
                var spanA = a.AsSpan(startA, i - startA).TrimStart('0');
                var spanB = b.AsSpan(startB, j - startB).TrimStart('0');

                if (spanA.Length != spanB.Length)
                    return spanA.Length - spanB.Length;

                int digits = spanA.SequenceCompareTo(spanB);
                if (digits != 0)
                    return digits;

                continue;
            }

            int cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (cmp != 0)
                return cmp;

            i++;
            j++;
        }

        return (a.Length - i) - (b.Length - j);
    }
}
