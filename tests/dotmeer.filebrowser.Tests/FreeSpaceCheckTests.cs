using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class FreeSpaceCheckTests
{
    [Fact]
    public async Task BuildPlan_CopyWithInsufficientSpaceFailsPreflight()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "big.bin"), new string('x', 4096));
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        var (repository, engine) = CreateEngine(workspace, destinationRoot, availableFreeSpace: 1024);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("source", "big.bin")], new FileRef("destination", "inbox"));

        var plan = engine.BuildPlan(task, CancellationToken.None);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(FileTaskErrorCodes.InsufficientSpace, error.Code);
        Assert.Null(error.Source);
    }

    [Fact]
    public async Task Execute_CopyWithInsufficientSpaceDoesNotCopyAnything()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "big.bin"), new string('x', 4096));
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, engine) = CreateEngine(workspace, destinationRoot, availableFreeSpace: 1024);
        var executor = new CopyTaskExecutor(engine);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("source", "big.bin")], new FileRef("destination", "inbox"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        task = (await repository.GetAsync(task.Id))!;

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "big.bin")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "big.bin")));
        Assert.Equal(FileTaskErrorCodes.PreflightFailed, Assert.Single((await repository.GetAsync(task.Id))!.Items).Error);
    }

    [Fact]
    public async Task Execute_CopyWithEnoughSpaceProceeds()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "small.txt"), "content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, engine) = CreateEngine(workspace, destinationRoot, availableFreeSpace: 1024 * 1024);
        var executor = new CopyTaskExecutor(engine);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("source", "small.txt")], new FileRef("destination", "inbox"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        task = (await repository.GetAsync(task.Id))!;

        await executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationDirectory, "small.txt")));
    }

    [Fact]
    public async Task BuildPlan_MoveWithinSameVolumeSkipsSpaceCheck()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        // Оба корня теста на одном томе: проверка для move пропускается,
        // несмотря на "нулевое" доступное место.
        var (repository, engine) = CreateEngine(workspace, destinationRoot, availableFreeSpace: 0);
        var task = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));

        var plan = engine.BuildPlan(task, CancellationToken.None);

        Assert.DoesNotContain(plan.Errors, error => error.Code == FileTaskErrorCodes.InsufficientSpace);
    }

    [Fact]
    public void Volume_ResolvesActualMountInsteadOfStringRootOnLinux()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        {
            return;
        }

        var regularDirectory = Path.GetTempPath();
        var shmDirectory = "/dev/shm";
        var shmNested = Path.Combine(shmDirectory, "filebrowser-volume-test");

        Assert.True(FileSystemVolume.AreOnSameVolume(regularDirectory, Path.Combine(regularDirectory, "nested")));
        Assert.True(FileSystemVolume.AreOnSameVolume(shmDirectory, shmNested));
        Assert.False(FileSystemVolume.AreOnSameVolume(regularDirectory, shmDirectory));
        Assert.True(FileSystemVolume.GetAvailableFreeSpace(shmDirectory) >= 0);
    }

    [Fact]
    public async Task BuildPlan_MoveAcrossVolumesWithInsufficientSpaceFailsPreflight()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        var (repository, engine) = CreateEngine(workspace, destinationRoot, availableFreeSpace: 0, forceSpaceCheck: true);
        var task = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));

        var plan = engine.BuildPlan(task, CancellationToken.None);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(FileTaskErrorCodes.InsufficientSpace, error.Code);
    }

    private static (TaskRepository Repository, FileTransferEngine Engine) CreateEngine(
        TestWorkspace workspace,
        string destinationRoot,
        long availableFreeSpace,
        bool forceSpaceCheck = false)
    {
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: source
                path: '{workspace.RootPath}'
              - name: destination
                path: '{destinationRoot}'
            """);
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        var repository = new TaskRepository(database);
        var resolver = new PathResolver(FileBrowserConfigurationLoader.Load(configPath));
        var engine = new ProbeEngine(resolver, repository, new TaskProgressService(), availableFreeSpace, forceSpaceCheck);
        return (repository, engine);
    }

    private static string CreateDestinationRoot(TestWorkspace workspace)
    {
        var path = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "destination");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ProbeEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        long availableFreeSpace,
        bool forceSpaceCheck) : FileTransferEngine(resolver, repository, progress)
    {
        internal override bool IsFreeSpaceCheckRequired(FileTask task, ResolvedPath source, ResolvedPath destination) =>
            forceSpaceCheck || base.IsFreeSpaceCheckRequired(task, source, destination);

        internal override long GetAvailableFreeSpace(string physicalPath) => availableFreeSpace;
    }
}
