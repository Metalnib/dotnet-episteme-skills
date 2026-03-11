using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class PathsTests
{
    [Theory]
    [InlineData("/Users/hgg/work/project", "/Users/hgg/work", true)]
    [InlineData("/Users/hgg/work", "/Users/hgg/work", true)]
    [InlineData("/Users/hgg/other", "/Users/hgg/work", false)]
    [InlineData("/Users/hgg/workspace", "/Users/hgg/work", false)] // prefix but not path segment
    public void IsUnder_MatchesCorrectly(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, Paths.IsUnder(candidate, root));
    }

    [Fact]
    public void IsExcluded_MatchesRelativePattern()
    {
        var excludes = new List<string> { "bin", "obj", "node_modules" };
        Assert.True(Paths.IsExcluded("/root/project/bin", "/root", excludes));
        Assert.True(Paths.IsExcluded("/root/project/obj/Debug", "/root", excludes));
        Assert.False(Paths.IsExcluded("/root/project/src", "/root", excludes));
    }

    [Fact]
    public void IsExcluded_MatchesNestedPattern()
    {
        var excludes = new List<string> { "repo-catalog" };
        Assert.True(Paths.IsExcluded("/root/repos/repo-catalog", "/root", excludes));
        Assert.True(Paths.IsExcluded("/root/repos/repo-catalog/src/file.cs", "/root", excludes));
        Assert.False(Paths.IsExcluded("/root/repos/repo-orders", "/root", excludes));
    }

    [Fact]
    public void IsExcluded_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(Paths.IsExcluded("/root/anything", "/root", null));
        Assert.False(Paths.IsExcluded("/root/anything", "/root", []));
    }

    [Fact]
    public void Normalize_TrimsTrailingSeparator()
    {
        var result = Paths.Normalize("/some/path/");
        Assert.False(result.EndsWith('/'));
        Assert.False(result.EndsWith('\\'));
    }

    [Fact]
    public void ToRelative_ProducesForwardSlashPath()
    {
        var result = Paths.ToRelative("/root", "/root/src/file.cs");
        Assert.Equal("src/file.cs", result);
        Assert.DoesNotContain("\\", result);
    }
}
