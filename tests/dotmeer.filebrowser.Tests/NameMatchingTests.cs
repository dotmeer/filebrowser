using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.FileSystem.Search;

namespace dotmeer.filebrowser.Tests;

public sealed class NameMatchingTests
{
    [Theory]
    [InlineData("AbC", "bC")]
    [InlineData("report.TXT", ".txt")]
    [InlineData("needle", "needle")]
    public void ContainsOrdinalIgnoreCase_MatchesIgnoringCase(string name, string substring)
    {
        Assert.True(NameMatching.ContainsOrdinalIgnoreCase(name, substring));
    }

    [Theory]
    [InlineData("abc", "d")]
    [InlineData("abc", "abcd")]
    public void ContainsOrdinalIgnoreCase_DoesNotMatch(string name, string substring)
    {
        Assert.False(NameMatching.ContainsOrdinalIgnoreCase(name, substring));
    }

    [Fact]
    public void ContainsOrdinalIgnoreCase_RejectsEmptySubstring()
    {
        Assert.Throws<ArgumentException>(() => NameMatching.ContainsOrdinalIgnoreCase("name", string.Empty));
    }

    [Theory]
    [InlineData("*.mkv", "a.mkv")]
    [InlineData("*.mkv", "A.MKV")]
    [InlineData("*.mkv", ".mkv")]
    [InlineData("a?c", "abc")]
    [InlineData("a?c", "aXc")]
    [InlineData("a*c", "ac")]
    [InlineData("a*c", "abbbc")]
    [InlineData("file[0-9].txt", "file5.txt")]
    [InlineData("file[0-9].txt", "FILE7.TXT")]
    [InlineData("data[a-z]x", "dataMx")]
    [InlineData("data[A-Z]x", "datamx")]
    [InlineData("[abc]", "b")]
    [InlineData("[abc]", "C")]
    [InlineData("[a-c]at", "bat")]
    [InlineData("note[1-3][ab]", "note2b")]
    public void Glob_IsMatch_MatchesFullName(string pattern, string name)
    {
        var matcher = GlobMatcher.Parse(pattern);

        Assert.True(matcher.IsMatch(name));
    }

    [Theory]
    [InlineData("*.mkv", "a.mkv.bak")]
    [InlineData("*.mkv", "mkv")]
    [InlineData("a?c", "ac")]
    [InlineData("a?c", "abbc")]
    [InlineData("file[0-9].txt", "filea.txt")]
    [InlineData("file[0-9].txt", "file55.txt")]
    [InlineData("[abc]", "d")]
    [InlineData("[a-c]at", "dat")]
    public void Glob_IsMatch_DoesNotMatch(string pattern, string name)
    {
        var matcher = GlobMatcher.Parse(pattern);

        Assert.False(matcher.IsMatch(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("a[")]
    [InlineData("[]")]
    [InlineData("a[]")]
    [InlineData("[z-a]")]
    [InlineData("a[b-c")]
    public void Glob_Parse_RejectsInvalidPattern(string pattern)
    {
        Assert.Throws<InvalidFileSystemRequestException>(() => GlobMatcher.Parse(pattern));
    }

    [Fact]
    public void Glob_Parse_KeepsOriginalPattern()
    {
        var matcher = GlobMatcher.Parse("*.TXT");

        Assert.Equal("*.TXT", matcher.Pattern);
    }
}
