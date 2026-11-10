using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class FileSystemWriteTests
{
    [Theory]
    [InlineData("nested/name")]
    [InlineData("nested\\name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".filebrowser-temp")]
    public void CreateDirectory_RejectsNameThatIsNotASingleUserName(string name)
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() => writer.CreateDirectory(new FileRef("files", string.Empty), name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.RootPath));
    }

    [Fact]
    public void PublicServices_RejectDirectAccessToTemporaryArtifacts()
    {
        using var workspace = new TestWorkspace();
        var temporaryPath = Path.Combine(workspace.RootPath, ".filebrowser-temp");
        File.WriteAllText(temporaryPath, "temporary");
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        var resolver = new PathResolver(configuration);
        var reader = new FileSystemReadService(configuration, resolver);
        var writer = new FileSystemWriteService(resolver);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            reader.List(new FileRef("files", ".filebrowser-temp")));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            writer.Rename(new FileRef("files", ".filebrowser-temp"), "renamed"));
        Assert.Throws<InvalidFileSystemRequestException>(() =>
            writer.Delete(new FileRef("files", ".filebrowser-temp")));

        Assert.True(File.Exists(temporaryPath));
        Assert.Equal("temporary", File.ReadAllText(temporaryPath));
    }

    [Fact]
    public void Rename_RefusesExistingDestinationWithoutOverwritingIt()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.RootPath, "source.txt");
        var destination = Path.Combine(workspace.RootPath, "destination.txt");
        File.WriteAllText(source, "source");
        File.WriteAllText(destination, "keep");
        var writer = CreateWriter(workspace);

        Assert.Throws<FileSystemNameConflictException>(() => writer.Rename(new FileRef("files", "source.txt"), "destination.txt"));

        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("keep", File.ReadAllText(destination));
    }

    [Fact]
    public async Task CreateDirectory_ConcurrentAttemptsCreateExactlyOneDirectory()
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);
        var results = await RunConcurrentAttemptsAsync(8, attempt =>
        {
            try
            {
                writer.CreateDirectory(new FileRef("files", string.Empty), "created");
                return true;
            }
            catch (FileSystemNameConflictException)
            {
                return false;
            }
        });

        Assert.Equal(1, results.Count(success => success));
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "created")));
    }

    [Fact]
    public async Task Rename_ConcurrentAttemptsPublishExactlyOneDestination()
    {
        using var workspace = new TestWorkspace();
        var firstSourcePath = Path.Combine(workspace.RootPath, "first.txt");
        var secondSourcePath = Path.Combine(workspace.RootPath, "second.txt");
        var destinationPath = Path.Combine(workspace.RootPath, "destination.txt");
        File.WriteAllText(firstSourcePath, "first");
        File.WriteAllText(secondSourcePath, "second");
        var writer = CreateWriter(workspace);
        var results = await RunConcurrentAttemptsAsync(2, attempt =>
        {
            var sourceName = attempt % 2 == 0 ? "first.txt" : "second.txt";
            try
            {
                writer.Rename(new FileRef("files", sourceName), "destination.txt");
                return true;
            }
            catch (FileSystemNameConflictException)
            {
                return false;
            }
        });

        Assert.Equal(1, results.Count(success => success));
        Assert.True(File.Exists(destinationPath));
        var destinationContent = File.ReadAllText(destinationPath);
        Assert.True(destinationContent is "first" or "second", $"Unexpected destination content: {destinationContent}");
        Assert.Equal(1, new[] { firstSourcePath, secondSourcePath }.Count(File.Exists));
    }

    [Fact]
    public void Delete_RemovesExactlyTheRequestedObject()
    {
        using var workspace = new TestWorkspace();
        var removable = Path.Combine(workspace.RootPath, "removable");
        Directory.CreateDirectory(removable);
        File.WriteAllText(Path.Combine(removable, "inside.txt"), "content");
        var survivor = Path.Combine(workspace.RootPath, "survivor.txt");
        File.WriteAllText(survivor, "keep");
        var writer = CreateWriter(workspace);

        writer.Delete(new FileRef("files", "removable"));

        Assert.False(Directory.Exists(removable));
        Assert.Equal("keep", File.ReadAllText(survivor));
    }

    [Fact]
    public void Delete_RemovesDeepDirectoryTree()
    {
        using var workspace = new TestWorkspace();
        var removable = Path.Combine(workspace.RootPath, "removable");
        var currentPath = removable;
        var depth = OperatingSystem.IsWindows() ? 48 : 256;
        for (var index = 0; index < depth; index++)
        {
            currentPath = Path.Combine(currentPath, "d");
            Directory.CreateDirectory(currentPath);
        }

        File.WriteAllText(Path.Combine(currentPath, "leaf.txt"), "content");
        var writer = CreateWriter(workspace);

        writer.Delete(new FileRef("files", "removable"));

        Assert.False(Directory.Exists(removable));
    }

    [Fact]
    public void RenameAndDelete_RejectRoot()
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() => writer.Rename(new FileRef("files", string.Empty), "other"));
        Assert.Throws<InvalidFileSystemRequestException>(() => writer.Delete(new FileRef("files", string.Empty)));
        Assert.True(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public void Delete_RejectsSymlink()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        var link = Path.Combine(workspace.RootPath, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var writer = CreateWriter(workspace);

        Assert.Throws<UnsupportedFileSystemObjectException>(() => writer.Delete(new FileRef("files", "link.txt")));
        Assert.True(File.Exists(link));
        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public void Delete_RejectsDirectoryContainingSymlink()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "directory");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        var link = Path.Combine(directory, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var writer = CreateWriter(workspace);

        Assert.Throws<UnsupportedFileSystemObjectException>(() => writer.Delete(new FileRef("files", "directory")));
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(link));
        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public void CreateDirectory_ReturnsPublicReferenceToCreatedDirectory()
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);

        var created = writer.CreateDirectory(new FileRef("files", string.Empty), "created");

        Assert.Equal("files", created.Root);
        Assert.Equal("created", created.Path);
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "created")));
    }

    private static async Task<bool[]> RunConcurrentAttemptsAsync(int attempts, Func<int, bool> attempt)
    {
        using var ready = new CountdownEvent(attempts);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, attempts)
            .Select(index => Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                return attempt(index);
            }))
            .ToArray();

        ready.Wait();
        start.Set();
        return await Task.WhenAll(tasks);
    }

    private static FileSystemWriteService CreateWriter(TestWorkspace workspace)
    {
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        return new FileSystemWriteService(new PathResolver(configuration));
    }
}
