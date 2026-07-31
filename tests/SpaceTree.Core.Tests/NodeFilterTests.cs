using SpaceTree.Core.Filtering;
using Xunit;

namespace SpaceTree.Core.Tests;

public class NodeFilterTests
{
    [Theory]
    [InlineData("log", "setup.log", true)]
    [InlineData("log", "catalog.txt", true)]      // bare text is a "contains" search
    [InlineData("log", "readme.md", false)]
    [InlineData("*.log", "setup.log", true)]
    [InlineData("*.log", "setup.log.1", false)]
    [InlineData("*.log*", "setup.log.1", true)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("*.log;*.tmp", "a.tmp", true)]
    [InlineData("*.log;*.tmp", "a.log", true)]
    [InlineData("*.log;*.tmp", "a.txt", false)]
    [InlineData("REPORT", "quarterly report.pdf", true)] // case-insensitive
    public void Wildcard_MatchesAsExpected(string pattern, string name, bool expected) =>
        Assert.Equal(expected, NodeFilter.Create(pattern).MatchesName(name));

    [Theory]
    [InlineData(@"^\d{4}-", "2024-report.txt", true)]
    [InlineData(@"^\d{4}-", "report-2024.txt", false)]
    [InlineData(@"\.(mp4|mkv)$", "movie.mkv", true)]
    public void Regex_MatchesAsExpected(string pattern, string name, bool expected) =>
        Assert.Equal(expected, NodeFilter.Create(pattern, FilterMode.Regex).MatchesName(name));

    [Fact]
    public void WildcardSpecialCharacters_AreEscaped()
    {
        // A dot in a wildcard pattern is a literal dot, not "any character".
        var filter = NodeFilter.Create("a.b");
        Assert.True(filter.MatchesName("xa.by"));
        Assert.False(filter.MatchesName("axby"));
    }

    [Fact]
    public void InvalidRegex_DegradesGracefully()
    {
        var filter = NodeFilter.Create("([unclosed", FilterMode.Regex);

        Assert.True(filter.HasInvalidPattern);
        Assert.False(filter.HasNameFilter);
        Assert.True(filter.MatchesName("anything"));  // shows everything rather than nothing
    }

    [Fact]
    public void EmptyPattern_IsInactive()
    {
        var filter = NodeFilter.Create("   ");

        Assert.False(filter.IsActive);
        Assert.True(filter.MatchesName("whatever"));
    }

    [Fact]
    public void MinimumSize_HidesSmallItems()
    {
        var filter = NodeFilter.Create(null, minimumSize: 1024);

        Assert.False(filter.MatchesSize(1023, isFolder: false));
        Assert.True(filter.MatchesSize(1024, isFolder: false));
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void HideEmptyFolders_AppliesOnlyToFolders()
    {
        var filter = NodeFilter.Create(null, hideEmptyFolders: true);

        Assert.False(filter.MatchesSize(0, isFolder: true));
        Assert.True(filter.MatchesSize(0, isFolder: false));  // a 0-byte file is still a file
        Assert.True(filter.MatchesSize(1, isFolder: true));
    }

    [Fact]
    public void None_MatchesEverything()
    {
        Assert.False(NodeFilter.None.IsActive);
        Assert.True(NodeFilter.None.Matches("x", 0, isFolder: true));
    }
}
