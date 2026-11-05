using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tests;

public class PathResolverTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("folder/../outside")]
    [InlineData("/outside")]
    [InlineData("folder\\child")]
    [InlineData("folder//child")]
    public void ResolveExisting_RejectsInvalidPublicPath(string path)
    {
        using var workspace = new TestWorkspace();
        var resolver = CreateResolver(workspace);

        Assert.ThrowsAny<FileSystemResolutionException>(() => resolver.ResolveExisting(new FileRef("files", path)));
    }

    [Fact]
    public void ResolveExisting_RejectsPrefixConfusionAttempt()
    {
        using var workspace = new TestWorkspace();
        var resolver = CreateResolver(workspace);

        Assert.ThrowsAny<FileSystemResolutionException>(() =>
            resolver.ResolveExisting(new FileRef("files", "../root-other/document.txt")));
    }

    [Theory]
    [InlineData(".filebrowser-private")]
    [InlineData("folder/.filebrowser-private")]
    public void ResolveExisting_RejectsTemporaryArtifactComponents(string path)
    {
        using var workspace = new TestWorkspace();
        var physicalPath = Path.Combine(workspace.RootPath, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        File.WriteAllText(physicalPath, "temporary");
        var resolver = CreateResolver(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            resolver.ResolveExisting(new FileRef("files", path)));
    }

    [Fact]
    public void ResolveExisting_ResolvesConfiguredRoot()
    {
        using var workspace = new TestWorkspace();
        var resolver = CreateResolver(workspace);

        var result = resolver.ResolveExisting(new FileRef("files", string.Empty));

        Assert.Equal(Path.GetFullPath(workspace.RootPath), result.PhysicalPath);
        Assert.True(result.IsDirectory);
        Assert.Equal(string.Empty, result.RelativePath);
    }

    [Fact]
    public void ResolveNewChild_UsesValidatedParentAndName()
    {
        using var workspace = new TestWorkspace();
        var resolver = CreateResolver(workspace);

        var result = resolver.ResolveNewChild(new FileRef("files", string.Empty), "new-folder");

        Assert.Equal(Path.Combine(workspace.RootPath, "new-folder"), result.PhysicalPath);
        Assert.False(File.Exists(result.PhysicalPath));
        Assert.False(Directory.Exists(result.PhysicalPath));
        Assert.ThrowsAny<FileSystemResolutionException>(() =>
            resolver.ResolveNewChild(new FileRef("files", string.Empty), ".filebrowser-tmp"));

        File.WriteAllText(Path.Combine(workspace.RootPath, "already-exists"), "content");
        Assert.ThrowsAny<FileSystemResolutionException>(() =>
            resolver.ResolveNewChild(new FileRef("files", string.Empty), "already-exists"));
    }

    [Fact]
    public void ResolveExisting_RejectsIntermediateSymlink()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(workspace.RootPath, "linked");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        var linkedRootYaml = $"roots:\n  - name: linked\n    path: '{link}'\n";
        Assert.Throws<InvalidOperationException>(() =>
            FileBrowserConfigurationLoader.Load(workspace.WriteRawConfiguration(linkedRootYaml)));

        var resolver = CreateResolver(workspace);
        Assert.ThrowsAny<FileSystemResolutionException>(() =>
            resolver.ResolveExisting(new FileRef("files", "linked/secret.txt")));
    }

    [Fact]
    public void UnavailableRoot_DoesNotPreventConfigurationLoading()
    {
        using var workspace = new TestWorkspace();
        var unavailablePath = Path.Combine(workspace.RootPath, "missing");
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(unavailablePath));
        var resolver = new PathResolver(configuration);

        Assert.Throws<RootUnavailableException>(() =>
            resolver.ResolveExisting(new FileRef("files", string.Empty)));
    }

    private static PathResolver CreateResolver(TestWorkspace workspace) =>
        new(FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath)));
}
