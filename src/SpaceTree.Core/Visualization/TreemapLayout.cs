namespace SpaceTree.Core.Visualization;

/// <summary>A laid-out tile. <see cref="Index"/> refers back to the caller's weight list.</summary>
public readonly record struct TreemapTile(int Index, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;

    public bool Contains(double px, double py) =>
        px >= X && px < X + Width && py >= Y && py < Y + Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk, 2000).
///
/// The greedy algorithm fills the rectangle row by row along its shorter edge,
/// extending the current row while doing so improves the worst aspect ratio in
/// that row. The result keeps tiles close to square, which is what makes a
/// treemap readable and clickable rather than a smear of slivers.
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lays out <paramref name="weights"/> inside the given rectangle.
    /// Weights should be sorted descending for the best result; the algorithm is
    /// still correct if they are not. Non-positive weights are skipped.
    /// </summary>
    public static IReadOnlyList<TreemapTile> Squarify(
        IReadOnlyList<double> weights, double x, double y, double width, double height)
    {
        var output = new List<TreemapTile>(weights.Count);
        Squarify(weights, x, y, width, height, output);
        return output;
    }

    /// <summary>Allocation-friendly overload that appends into a caller-owned list.</summary>
    public static void Squarify(
        IReadOnlyList<double> weights, double x, double y, double width, double height, List<TreemapTile> output)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(output);

        if (width <= 0 || height <= 0 || weights.Count == 0)
            return;

        double total = 0;
        int positiveCount = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] > 0)
            {
                total += weights[i];
                positiveCount++;
            }
        }

        if (total <= 0 || positiveCount == 0)
            return;

        // Indices of the positive weights, largest first.
        var order = new int[positiveCount];
        int n = 0;
        for (int i = 0; i < weights.Count; i++)
            if (weights[i] > 0)
                order[n++] = i;
        Array.Sort(order, (a, b) => weights[b].CompareTo(weights[a]));

        // Scale weights into areas that exactly fill the rectangle.
        double scale = (width * height) / total;
        var areas = new double[positiveCount];
        for (int i = 0; i < positiveCount; i++)
            areas[i] = weights[order[i]] * scale;

        LayoutRange(areas, order, 0, positiveCount, x, y, width, height, output);
    }

    private static void LayoutRange(
        double[] areas, int[] order, int start, int end,
        double x, double y, double width, double height, List<TreemapTile> output)
    {
        while (start < end)
        {
            if (width <= 0 || height <= 0)
                return;

            // Rows are laid out along the shorter side so tiles stay square-ish.
            double shortSide = Math.Min(width, height);

            int rowEnd = start + 1;
            double rowArea = areas[start];
            double worst = WorstAspect(areas, start, rowEnd, rowArea, shortSide);

            while (rowEnd < end)
            {
                double nextArea = rowArea + areas[rowEnd];
                double nextWorst = WorstAspect(areas, start, rowEnd + 1, nextArea, shortSide);
                if (nextWorst > worst)
                    break;

                rowArea = nextArea;
                worst = nextWorst;
                rowEnd++;
            }

            // Place the row, then continue in the leftover rectangle.
            if (width >= height)
            {
                double rowWidth = rowArea / height;
                double cursor = y;
                for (int i = start; i < rowEnd; i++)
                {
                    double tileHeight = rowWidth > 0 ? areas[i] / rowWidth : 0;
                    // Snap the last tile to the row edge to avoid sub-pixel gaps.
                    if (i == rowEnd - 1)
                        tileHeight = y + height - cursor;
                    output.Add(new TreemapTile(order[i], x, cursor, rowWidth, Math.Max(0, tileHeight)));
                    cursor += tileHeight;
                }
                x += rowWidth;
                width -= rowWidth;
            }
            else
            {
                double rowHeight = rowArea / width;
                double cursor = x;
                for (int i = start; i < rowEnd; i++)
                {
                    double tileWidth = rowHeight > 0 ? areas[i] / rowHeight : 0;
                    if (i == rowEnd - 1)
                        tileWidth = x + width - cursor;
                    output.Add(new TreemapTile(order[i], cursor, y, Math.Max(0, tileWidth), rowHeight));
                    cursor += tileWidth;
                }
                y += rowHeight;
                height -= rowHeight;
            }

            start = rowEnd;
        }
    }

    /// <summary>Worst (largest) width:height ratio among the tiles of a candidate row.</summary>
    private static double WorstAspect(double[] areas, int start, int end, double rowArea, double shortSide)
    {
        if (rowArea <= 0 || shortSide <= 0)
            return double.MaxValue;

        double min = double.MaxValue, max = 0;
        for (int i = start; i < end; i++)
        {
            double a = areas[i];
            if (a < min) min = a;
            if (a > max) max = a;
        }

        if (min <= 0)
            return double.MaxValue;

        double side2 = shortSide * shortSide;
        double rowArea2 = rowArea * rowArea;
        return Math.Max((side2 * max) / rowArea2, rowArea2 / (side2 * min));
    }
}
