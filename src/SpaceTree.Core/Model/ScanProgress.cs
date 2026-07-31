namespace SpaceTree.Core.Model;

/// <summary>Immutable snapshot of a running scan, published on a timer rather than per item.</summary>
public readonly record struct ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesScanned,
    long DirectoriesPending,
    string CurrentPath,
    TimeSpan Elapsed,
    int Errors)
{
    public double FilesPerSecond => Elapsed.TotalSeconds > 0.1 ? FilesScanned / Elapsed.TotalSeconds : 0;

    /// <summary>
    /// Rough completion estimate. There is no way to know the total item count up
    /// front, so we use the ratio of finished directories to discovered ones —
    /// it is monotonic enough to drive a progress bar honestly.
    /// </summary>
    public double EstimatedFraction
    {
        get
        {
            long known = DirectoriesScanned + DirectoriesPending;
            if (known <= 0)
                return 0;
            return Math.Clamp((double)DirectoriesScanned / known, 0d, 1d);
        }
    }

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            double f = EstimatedFraction;
            if (f <= 0.01 || Elapsed.TotalSeconds < 1)
                return null;
            double total = Elapsed.TotalSeconds / f;
            double remaining = total - Elapsed.TotalSeconds;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(remaining, TimeSpan.FromDays(1).TotalSeconds));
        }
    }
}

/// <summary>A directory that could not be read.</summary>
public sealed record ScanError(string Path, int ErrorCode, string Message);
