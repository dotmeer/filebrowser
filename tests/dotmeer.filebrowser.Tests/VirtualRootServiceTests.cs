using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;

namespace dotmeer.filebrowser.Tests;

public sealed class VirtualRootServiceTests
{
    [Fact]
    public void List_VirtualRootTopLevel_ReturnsConfiguredRootsAsDirectories()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);

        var listing = services.Reader.List(new FileRef("AllRoots", string.Empty));

        Assert.Equal("AllRoots", listing.Root);
        Assert.Equal(string.Empty, listing.Path);
        Assert.Equal(["alpha", "beta", "offline"], listing.Entries.Select(entry => entry.Name));
        Assert.Equal(["alpha", "beta", "offline"], listing.Entries.Select(entry => entry.Path));
        Assert.Equal(["directory", "directory", "unsupported"], listing.Entries.Select(entry => entry.Kind));
        Assert.All(listing.Entries, entry => Assert.Null(entry.Size));
        Assert.NotNull(listing.Entries.Single(entry => entry.Name == "alpha").ModifiedAtUtc);
        Assert.Null(listing.Entries.Single(entry => entry.Name == "offline").ModifiedAtUtc);
    }

    [Fact]
    public void List_VirtualRootUnderRealRoot_PrefixesPathsWithRootNameOnly()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "note.txt"), "note");
        File.WriteAllText(Path.Combine(workspace.RootPath, "root.txt"), "root");

        var listing = services.Reader.List(new FileRef("AllRoots", "alpha/sub"));
        var entry = Assert.Single(listing.Entries);
        Assert.Equal("note.txt", entry.Name);
        Assert.Equal("alpha/sub/note.txt", entry.Path);

        var rootListing = services.Reader.List(new FileRef("AllRoots", "alpha"));
        Assert.Contains(rootListing.Entries, item => item.Name == "sub" && item.Path == "alpha/sub");
        Assert.Contains(rootListing.Entries, item => item.Name == "root.txt" && item.Path == "alpha/root.txt");
    }

    [Fact]
    public void List_VirtualRootUnknownFirstSegment_ThrowsNotFound()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);

        Assert.Throws<FileSystemObjectNotFoundException>(() =>
            services.Reader.List(new FileRef("AllRoots", "missing/dir")));
    }

    [Fact]
    public void List_VirtualRootFileAsDirectory_ThrowsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "note");
        var services = CreateServices(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Reader.List(new FileRef("AllRoots", "alpha/note.txt")));
    }

    [Fact]
    public void OpenFile_VirtualRoot_ReturnsSameFileAsDirectDownload()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "shared.txt"), "payload");
        var services = CreateServices(workspace);

        var direct = services.Reader.OpenFile(new FileRef("alpha", "shared.txt"));
        var viaVirtual = services.Reader.OpenFile(new FileRef("AllRoots", "alpha/shared.txt"));

        Assert.Equal("shared.txt", viaVirtual.Name);
        using (direct.Stream)
        using (viaVirtual.Stream)
        {
            Assert.Equal(direct.Name, viaVirtual.Name);
            using var reader = new StreamReader(viaVirtual.Stream);
            Assert.Equal("payload", reader.ReadToEnd());
        }
    }

    [Fact]
    public void Search_VirtualRootSearchesEveryAvailableRootWithPrefixedPaths()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "hit-dir"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");
        File.WriteAllText(Path.Combine(services.SecondRootPath, "hit-b.txt"), "b");

        var result = services.Search.Search(
            new FileRef("AllRoots", string.Empty), "hit", null, null, 500, CancellationToken.None);

        Assert.Equal("AllRoots", result.Root);
        Assert.Equal(string.Empty, result.Path);
        Assert.False(result.Truncated);
        Assert.Equal(["offline"], result.UnavailableRoots);
        Assert.Equal(
            ["alpha/hit-dir", "alpha/hit-a.txt", "beta/hit-b.txt"],
            result.Results.Select(entry => entry.Path));
        Assert.Equal(
            ["hit-dir", "hit-a.txt", "hit-b.txt"],
            result.Results.Select(entry => entry.Name));
    }

    [Fact]
    public void Search_VirtualRootReportsUnavailableRootsAndSkipsThem()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");

        var result = services.Search.Search(
            new FileRef("AllRoots", string.Empty), "hit", null, null, 500, CancellationToken.None);

        Assert.Equal(["offline"], result.UnavailableRoots);
        var entry = Assert.Single(result.Results);
        Assert.Equal("alpha/hit-a.txt", entry.Path);
    }

    [Fact]
    public void Search_VirtualRootAppliesSharedLimitAcrossRoots()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        for (var index = 0; index < 300; index++)
        {
            File.WriteAllText(Path.Combine(workspace.RootPath, $"match-a-{index:000}.txt"), "a");
            File.WriteAllText(Path.Combine(services.SecondRootPath, $"match-b-{index:000}.txt"), "b");
        }

        var result = services.Search.Search(
            new FileRef("AllRoots", string.Empty), "match", null, null, 500, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(500, result.Results.Count);
        Assert.Contains(result.Results, entry => entry.Path.StartsWith("alpha/", StringComparison.Ordinal));
        Assert.Contains(result.Results, entry => entry.Path.StartsWith("beta/", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_VirtualRootPathUnderSingleRoot_SearchesOnlyThatRoot()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");
        File.WriteAllText(Path.Combine(services.SecondRootPath, "hit-b.txt"), "b");

        var result = services.Search.Search(
            new FileRef("AllRoots", "beta"), "hit", null, null, 500, CancellationToken.None);

        Assert.Equal("AllRoots", result.Root);
        Assert.Equal("beta", result.Path);
        Assert.Empty(result.UnavailableRoots);
        var entry = Assert.Single(result.Results);
        Assert.Equal("beta/hit-b.txt", entry.Path);
    }

    [Fact]
    public void Search_RealRoot_HasEmptyUnavailableRoots()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");

        var result = services.Search.Search(
            new FileRef("alpha", string.Empty), "hit", null, null, 500, CancellationToken.None);

        Assert.Equal("alpha", result.Root);
        Assert.Empty(result.UnavailableRoots);
    }

    [Fact]
    public void Mutations_VirtualRootRejectedWithInvalidRequestAndDoNotChangeFileSystem()
    {
        using var workspace = new TestWorkspace();
        var notePath = Path.Combine(workspace.RootPath, "note.txt");
        File.WriteAllText(notePath, "note");
        var services = CreateServices(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Writer.CreateDirectory(new FileRef("AllRoots", string.Empty), "new-dir"));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Writer.CreateDirectory(new FileRef("AllRoots", "alpha"), "new-dir"));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Writer.Rename(new FileRef("AllRoots", "alpha/note.txt"), "renamed.txt"));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Writer.Delete(new FileRef("AllRoots", "alpha/note.txt")));

        Assert.True(File.Exists(notePath));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "new-dir")));
    }

    [Fact]
    public void CopyMoveResolution_VirtualRootRejectedByResolverForSourcesAndDestination()
    {
        using var workspace = new TestWorkspace();
        var services = CreateServices(workspace);

        // FileTransferEngine разрешает источники и назначение через ResolveExisting,
        // публикация имени назначения — через ResolveNewChild: виртуальный корень
        // отклоняется resolver-ом для всех этих операций.
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Resolver.ResolveExisting(new FileRef("AllRoots", string.Empty)));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Resolver.ResolveExisting(new FileRef("AllRoots", "alpha/note.txt")));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Resolver.ResolveNewChild(new FileRef("AllRoots", string.Empty), "name"));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            services.Resolver.ResolveNewChild(new FileRef("AllRoots", "alpha"), "name"));
    }

    private static Services CreateServices(TestWorkspace workspace)
    {
        var secondRootPath = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "beta");
        Directory.CreateDirectory(secondRootPath);
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(
            $"roots:\n  - name: alpha\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRootPath}'\n  - name: offline\n    path: '{workspace.RootPath}-missing'\n"));
        var resolver = new PathResolver(configuration);
        return new Services(
            secondRootPath,
            new FileSystemReadService(configuration, resolver),
            new FileSystemSearchService(configuration, resolver, new SearchMetrics()),
            new FileSystemWriteService(resolver),
            resolver);
    }

    private sealed record Services(
        string SecondRootPath,
        FileSystemReadService Reader,
        FileSystemSearchService Search,
        FileSystemWriteService Writer,
        PathResolver Resolver);
}
