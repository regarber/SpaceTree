using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SpaceTree.App.ViewModels;

/// <summary>
/// The flattened list of visible tree rows.
///
/// WPF's CollectionView cannot represent a multi-item add, so a bulk change has
/// to be announced as a Reset. A Reset is fast — the virtualising panel only
/// rebuilds the few dozen containers actually on screen — but it drops the
/// scroll offset and the selection, which is jarring when the user expands one
/// small folder.
///
/// So this collection uses whichever is less disruptive: item-by-item
/// notifications for small changes (scroll and selection survive), and a single
/// Reset once a change is big enough that per-item churn would cost more than
/// restoring the viewport afterwards. <see cref="Resetting"/> lets the view
/// capture what to restore.
/// </summary>
public sealed class RowCollection : ObservableCollection<TreeRowViewModel>
{
    /// <summary>
    /// Above this many items a single Reset beats per-item notifications.
    /// Chosen by measurement: individual inserts run at roughly 20 µs each, so a
    /// thousand of them is about one dropped frame — the point where the user
    /// starts to feel it.
    /// </summary>
    public const int BatchThreshold = 1000;

    private static readonly PropertyChangedEventArgs CountArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs =
        new(NotifyCollectionChangedAction.Reset);

    /// <summary>Raised immediately before a Reset, so the view can save scroll and selection.</summary>
    public event EventHandler? Resetting;

    /// <summary>Inserts a run of rows at <paramref name="index"/>.</summary>
    public void InsertRange(int index, IReadOnlyList<TreeRowViewModel> rows)
    {
        if (rows.Count == 0)
            return;

        if (rows.Count < BatchThreshold)
        {
            for (int i = 0; i < rows.Count; i++)
                Insert(index + i, rows[i]);
            return;
        }

        for (int i = 0; i < rows.Count; i++)
            Items.Insert(index + i, rows[i]);

        NotifyReset();
    }

    /// <summary>Removes <paramref name="count"/> rows starting at <paramref name="index"/>.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;

        if (count < BatchThreshold)
        {
            for (int i = 0; i < count; i++)
                RemoveAt(index);
            return;
        }

        for (int i = 0; i < count; i++)
            Items.RemoveAt(index);

        NotifyReset();
    }

    /// <summary>
    /// Brings the collection in line with <paramref name="target"/> using the
    /// smallest set of notifications.
    ///
    /// A live scan re-sorts the visible rows several times a second as folder
    /// totals grow. Announcing that as a Reset would throw the user back to the
    /// top of the list every tick, so instead each changed position is announced
    /// as a Replace, which regenerates one row container and leaves the scroll
    /// offset alone. Only a change too large for that to pay off falls back to a
    /// single Reset.
    /// </summary>
    public void Patch(IReadOnlyList<TreeRowViewModel> target)
    {
        int common = Math.Min(Count, target.Count);

        int changed = 0;
        for (int i = 0; i < common; i++)
            if (!ReferenceEquals(Items[i], target[i]))
                changed++;

        if (changed + Math.Abs(Count - target.Count) >= BatchThreshold)
        {
            ReplaceAll(target);
            return;
        }

        for (int i = 0; i < common; i++)
            if (!ReferenceEquals(Items[i], target[i]))
                this[i] = target[i];

        while (Count > target.Count)
            RemoveAt(Count - 1);

        for (int i = Count; i < target.Count; i++)
            Add(target[i]);
    }

    /// <summary>Replaces the entire contents in one notification.</summary>
    public void ReplaceAll(IReadOnlyList<TreeRowViewModel> rows)
    {
        Items.Clear();
        for (int i = 0; i < rows.Count; i++)
            Items.Add(rows[i]);

        NotifyReset();
    }

    private void NotifyReset()
    {
        Resetting?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(CountArgs);
        OnPropertyChanged(IndexerArgs);
        OnCollectionChanged(ResetArgs);
    }
}
