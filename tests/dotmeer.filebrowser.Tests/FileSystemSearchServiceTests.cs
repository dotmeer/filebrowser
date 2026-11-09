using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.FileSystem.Search;
using dotmeer.filebrowser.Observability;

namespace dotmeer.filebrowser.Tests;

public sealed class FileSystemSearchServiceTests
{
    [Fact]
    public void Search_FindsNestedMatchesAndSortsLikeDirectoryListing()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "match-z-dir"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "match-a-dir"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "nested"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "nested", "match-b.txt"), "b");
        File.WriteAllText(Path.Combine(workspace.RootPath, "match-a.txt"), "a");

        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "match", null, null, 500, CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Equal("files", result.Root);
        Assert.Equal(string.Empty, result.Path);
        Assert.Equal("match", result.Query);
        Assert.Null(result.Ext);
        Assert.Equal(
            ["match-a-dir", "match-z-dir", "match-a.txt", "match-b.txt"],
            result.Results.Select(entry => entry.Name));
        Assert.Equal(
            ["match-a-dir", "match-z-dir", "match-a.txt", "nested/match-b.txt"],
            result.Results.Select(entry => entry.Path));
    }

    [Fact]
    public void Search_MatchesNameIgnoringCase()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "RePoRt.TXT"), "x");

        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "report", null, null, 500, CancellationToken.None);

        var entry = Assert.Single(result.Results);
        Assert.Equal("RePoRt.TXT", entry.Name);
        Assert.Equal("file", entry.Kind);
        Assert.Equal(1, entry.Size);
    }

    [Fact]
    public void Search_ExcludesStartNodeItselfFromResults()
    {
        using var workspace = new TestWorkspace();
        var start = Path.Combine(workspace.RootPath, "match-start");
        Directory.CreateDirectory(start);
        File.WriteAllText(Path.Combine(start, "match-inside.txt"), "x");

        var result = CreateService(workspace).Search(
            new FileRef("files", "match-start"), "match", null, null, 500, CancellationToken.None);

        Assert.Equal("match-start", result.Path);
        Assert.DoesNotContain(result.Results, entry => entry.Name == "match-start");
        Assert.Contains(result.Results, entry => entry.Name == "match-inside.txt");
    }

    [Fact]
    public void Search_StartsInsideStartDirectoryOnly()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "root-match.txt"), "x");
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "sub-match.txt"), "x");

        var result = CreateService(workspace).Search(
            new FileRef("files", "sub"), "match", null, null, 500, CancellationToken.None);

        var entry = Assert.Single(result.Results);
        Assert.Equal("sub/sub-match.txt", entry.Path);
        Assert.DoesNotContain(result.Results, entry => entry.Name == "root-match.txt");
    }

    [Fact]
    public void Search_StartIsFile_ThrowsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "file.txt"), "x");

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            CreateService(workspace).Search(new FileRef("files", "file.txt"), "file", null, null, 500, CancellationToken.None));
    }

    [Fact]
    public void Search_ExcludesTemporaryArtifacts()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, ".filebrowser-match"), "hidden");
        var hiddenDir = Path.Combine(workspace.RootPath, ".filebrowser-dir");
        Directory.CreateDirectory(hiddenDir);
        File.WriteAllText(Path.Combine(hiddenDir, "match-inside.txt"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, "visible-match.txt"), "x");

        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "match", null, null, 500, CancellationToken.None);

        var entry = Assert.Single(result.Results);
        Assert.Equal("visible-match.txt", entry.Name);
    }

    [Fact]
    public void Search_SkipsSymlinkWithoutFollowingIt()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "match-secret.txt"), "secret");
        var link = Path.Combine(workspace.RootPath, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "match", null, null, 500, CancellationToken.None);

        Assert.Empty(result.Results);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmptyResult()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "other.txt"), "x");

        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "absent", null, null, 500, CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Search_TruncatesWhenLimitIsReached()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(workspace.RootPath, $"match-{index}.txt"), "x");
        }

        var limited = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "match", null, null, 2, CancellationToken.None);

        Assert.True(limited.Truncated);
        Assert.Equal(2, limited.Results.Count);

        var enough = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "match", null, null, 5, CancellationToken.None);

        Assert.False(enough.Truncated);
        Assert.Equal(5, enough.Results.Count);
    }

    [Fact]
    public void Search_RejectsInvalidArguments()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Search(new FileRef("files", string.Empty), " ", null, null, 500, CancellationToken.None));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Search(new FileRef("files", string.Empty), "x", null, null, 0, CancellationToken.None));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Search(new FileRef("files", string.Empty), "x", null, null, 501, CancellationToken.None));
    }

    [Fact]
    public void Search_ExtGlobsFilterMatchesByFullName()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "clip.mkv"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, "clip.mp4"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, "notes.txt"), "x");

        var globs = new[] { GlobMatcher.Parse("*.mkv"), GlobMatcher.Parse("*.MP4") };
        var result = CreateService(workspace).Search(
            new FileRef("files", string.Empty), "clip", "*.mkv,*.MP4", globs, 500, CancellationToken.None);

        Assert.Equal("*.mkv,*.MP4", result.Ext);
        Assert.Equal(["clip.mkv", "clip.mp4"], result.Results.Select(entry => entry.Name));
    }

    private static FileSystemSearchService CreateService(TestWorkspace workspace)
    {
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        return new FileSystemSearchService(configuration, new PathResolver(configuration), new SearchMetrics());
    }
}
