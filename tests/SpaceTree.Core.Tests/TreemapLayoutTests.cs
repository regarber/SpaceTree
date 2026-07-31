using SpaceTree.Core.Visualization;
using Xunit;

namespace SpaceTree.Core.Tests;

public class TreemapLayoutTests
{
    [Fact]
    public void SingleItem_FillsTheWholeRectangle()
    {
        var tiles = TreemapLayout.Squarify(new[] { 1d }, 0, 0, 100, 50);

        var tile = Assert.Single(tiles);
        Assert.Equal(0, tile.X);
        Assert.Equal(0, tile.Y);
        Assert.Equal(100, tile.Width, 6);
        Assert.Equal(50, tile.Height, 6);
    }

    [Fact]
    public void Areas_AreProportionalToWeights()
    {
        double[] weights = { 6, 3, 2, 1 };
        var tiles = TreemapLayout.Squarify(weights, 0, 0, 120, 80);

        double totalArea = 120 * 80;
        double weightSum = weights.Sum();

        Assert.Equal(4, tiles.Count);
        foreach (var tile in tiles)
        {
            double expected = weights[tile.Index] / weightSum * totalArea;
            Assert.Equal(expected, tile.Area, expected * 0.02); // within 2%
        }
    }

    [Fact]
    public void Tiles_StayInsideTheRectangleAndDoNotOverlap()
    {
        double[] weights = Enumerable.Range(1, 25).Select(i => (double)(i * i)).ToArray();
        var tiles = TreemapLayout.Squarify(weights, 10, 20, 300, 200);

        foreach (var tile in tiles)
        {
            Assert.True(tile.X >= 10 - 1e-6 && tile.Right <= 310 + 1e-6, $"x out of bounds: {tile}");
            Assert.True(tile.Y >= 20 - 1e-6 && tile.Bottom <= 220 + 1e-6, $"y out of bounds: {tile}");
        }

        for (int i = 0; i < tiles.Count; i++)
        for (int j = i + 1; j < tiles.Count; j++)
            Assert.False(Overlaps(tiles[i], tiles[j]), $"tiles {i} and {j} overlap");
    }

    [Fact]
    public void Tiles_CoverTheRectangle()
    {
        double[] weights = { 5, 4, 3, 2, 1 };
        var tiles = TreemapLayout.Squarify(weights, 0, 0, 200, 100);

        double covered = tiles.Sum(t => t.Area);
        Assert.Equal(200 * 100, covered, 200 * 100 * 0.001);
    }

    [Fact]
    public void SquarifiedTiles_BeatNaiveSlicing()
    {
        // The point of squarification is aspect ratio; assert it actually delivers.
        double[] weights = { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var tiles = TreemapLayout.Squarify(weights, 0, 0, 400, 300);

        double worst = tiles.Max(t => Math.Max(t.Width / t.Height, t.Height / t.Width));
        Assert.True(worst < 4, $"worst aspect ratio was {worst:F2}, expected < 4");
    }

    [Fact]
    public void NonPositiveWeights_AreSkipped()
    {
        var tiles = TreemapLayout.Squarify(new[] { 5d, 0d, -3d, 5d }, 0, 0, 100, 100);

        Assert.Equal(2, tiles.Count);
        Assert.All(tiles, t => Assert.True(t.Index is 0 or 3));
    }

    [Fact]
    public void DegenerateInput_ReturnsNothing()
    {
        Assert.Empty(TreemapLayout.Squarify(Array.Empty<double>(), 0, 0, 100, 100));
        Assert.Empty(TreemapLayout.Squarify(new[] { 1d }, 0, 0, 0, 100));
        Assert.Empty(TreemapLayout.Squarify(new[] { 0d }, 0, 0, 100, 100));
    }

    [Fact]
    public void Contains_HitTestsCorrectly()
    {
        var tile = new TreemapTile(0, 10, 20, 30, 40);

        Assert.True(tile.Contains(10, 20));
        Assert.True(tile.Contains(39.9, 59.9));
        Assert.False(tile.Contains(40, 60));
        Assert.False(tile.Contains(9, 20));
    }

    private static bool Overlaps(TreemapTile a, TreemapTile b)
    {
        const double eps = 1e-6;
        return a.X + eps < b.Right && b.X + eps < a.Right &&
               a.Y + eps < b.Bottom && b.Y + eps < a.Bottom;
    }
}
