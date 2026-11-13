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
    public void CreateDirectory_ExistingFileOrDirectoryReturnsConflictAndKeepsIt()
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);
        var existingFile = Path.Combine(workspace.RootPath, "existing-file");
        File.WriteAllText(existingFile, "keep");
        var existingDirectory = Path.Combine(workspace.RootPath, "existing-directory");
        Directory.CreateDirectory(existingDirectory);
        File.WriteAllText(Path.Combine(existingDirectory, "inside.txt"), "inside");

        Assert.Throws<FileSystemNameConflictException>(() =>
            writer.CreateDirectory(new FileRef("files", string.Empty), "existing-file"));
        Assert.Throws<FileSystemNameConflictException>(() =>
            writer.CreateDirectory(new FileRef("files", string.Empty), "existing-directory"));

        Assert.Equal("keep", File.ReadAllText(existingFile));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(existingDirectory, "inside.txt")));
    }

    [Fact]
    public async Task AtomicDirectory_ExternalRaceCreatesOneDirectoryWithoutTouchingContent()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.RootPath, "external");
        var results = await RunConcurrentAttemptsAsync(8, _ =>
        {
            try
            {
                AtomicDirectory.Create(path);
                return true;
            }
            catch (FileSystemNameConflictException)
            {
                return false;
            }
        });

        Assert.Equal(1, results.Count(success => success));
        File.WriteAllText(Path.Combine(path, "keep.txt"), "keep");
        Assert.Throws<FileSystemNameConflictException>(() => AtomicDirectory.Create(path));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "keep.txt")));
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
    public void Rename_RejectsRoot()
    {
        using var workspace = new TestWorkspace();
        var writer = CreateWriter(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() => writer.Rename(new FileRef("files", string.Empty), "other"));
        Assert.True(Directory.Exists(workspace.RootPath));
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
