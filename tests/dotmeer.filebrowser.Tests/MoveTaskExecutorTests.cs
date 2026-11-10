using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class MoveTaskExecutorTests
{
    [Fact]
    public async Task Execute_MovesFileAndDirectoryWithinSameFileSystem()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "file content");
        var sourceDirectory = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty"));
        File.WriteAllText(Path.Combine(sourceDirectory, "track.txt"), "track content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "note.txt"), new FileRef("source", "album")],
            new FileRef("destination", "inbox"));

        await executor.ExecuteAsync(task, CancellationToken.None);
        var running = await repository.GetAsync(task.Id);

        Assert.NotNull(running);
        Assert.Equal(FileTaskStatus.Running, running.Status);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.Equal("file content", File.ReadAllText(Path.Combine(destinationDirectory, "note.txt")));
        Assert.Equal("track content", File.ReadAllText(Path.Combine(destinationDirectory, "album", "track.txt")));
        Assert.True(Directory.Exists(Path.Combine(destinationDirectory, "album", "empty")));
        Assert.Equal(running.BytesTotal, running.BytesDone);
        Assert.Equal(running.ItemsTotal, running.ItemsDone);
        Assert.All(running.Items, item => Assert.Equal("completed", item.Status));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_ConflictLeavesSourceAndDestinationUnchanged()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "source");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(destinationDirectory, "note.txt"), "existing");
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));
        var scanned = await repository.GetAsync(task.Id);

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.Equal("source", File.ReadAllText(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destinationDirectory, "note.txt")));
        Assert.NotNull(scanned);
        Assert.Equal(FileTaskStatus.Scanning, scanned.Status);
        Assert.Equal("failed", Assert.Single(scanned.Items).Status);
        Assert.Equal(FileTaskErrorCodes.NameConflict, Assert.Single(scanned.Items).Error);
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_RejectsDestinationInsideSourceDirectoryBeforeMoving()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var sourceDirectory = Path.Combine(workspace.RootPath, "source-directory");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        File.WriteAllText(Path.Combine(sourceDirectory, "note.txt"), "content");
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "source-directory")],
            new FileRef("source", "source-directory/nested"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        var scanned = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.DestinationInsideSource, Assert.Single(scanned!.Items).Error);
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "note.txt")));
        Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "nested", "source-directory")));
    }

    [Fact]
    public async Task Execute_CollectsPreflightErrorsAndDoesNotMoveAnything()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "first.txt"), "first");
        var other = Path.Combine(workspace.RootPath, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "second.txt"), "second");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(destinationDirectory, "first.txt"), "existing");
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "first.txt"), new FileRef("source", "other/second.txt")],
            new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        var scanned = await repository.GetAsync(task.Id);
        Assert.NotNull(scanned);
        Assert.Equal(
            [FileTaskErrorCodes.NameConflict, FileTaskErrorCodes.SourcesNotSameParent],
            scanned.Items.OrderBy(item => item.Source.Path, StringComparer.Ordinal).Select(item => item.Error!).ToArray());
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "first.txt")));
        Assert.True(File.Exists(Path.Combine(other, "second.txt")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "second.txt")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destinationDirectory, "first.txt")));
    }

    [Fact]
    public async Task Execute_StopsAtFirstFailureAndMarksRemainingItemsNotProcessed()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            File.WriteAllText(Path.Combine(workspace.RootPath, name), name);
        }

        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new SelectiveFailureEngine(resolver, repository, new TaskProgressService(), "b.txt"));
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "a.txt"), new FileRef("source", "b.txt"), new FileRef("source", "c.txt")],
            new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));
        var running = await repository.GetAsync(task.Id);

        Assert.Equal(FileTaskErrorCodes.IoError, exception.SafeCode);
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "b.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "c.txt")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "b.txt")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "c.txt")));
        Assert.NotNull(running);
        Assert.Equal(["completed", "failed", "not_processed"], running.Items.Select(item => item.Status));
    }

    [Fact]
    public async Task Execute_CrossDeviceCopyFailureDoesNotDeleteSourceAndCleansArtifact()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FailingCopyEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.IoError, exception.SafeCode);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "note.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_CrossDeviceFallbackPublishesThenDeletesSource()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        var sourceDirectory = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty"));
        File.WriteAllText(Path.Combine(sourceDirectory, "track.txt"), "track");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new CrossDeviceEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "note.txt"), new FileRef("source", "album")],
            new FileRef("destination", "inbox"));

        await executor.ExecuteAsync(task, CancellationToken.None);
        var running = await repository.GetAsync(task.Id);

        Assert.NotNull(running);
        Assert.Equal(FileTaskStatus.Running, running.Status);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationDirectory, "note.txt")));
        Assert.Equal("track", File.ReadAllText(Path.Combine(destinationDirectory, "album", "track.txt")));
        Assert.All(running.Items, item => Assert.Equal("completed", item.Status));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_DeletionFailureKeepsPublishedDestinationAndReportsPartialResult()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
        var executor = new MoveTaskExecutor(new FailingDeleteEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));
        var running = await repository.GetAsync(task.Id);

        Assert.Equal(FileTaskErrorCodes.MoveSourceNotDeleted, exception.SafeCode);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationDirectory, "note.txt")));
        Assert.NotNull(running);
        Assert.Equal("failed", Assert.Single(running.Items).Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_RealCrossDeviceMove_WhenSecondFileSystemIsProvided()
    {
        var secondaryRoot = Environment.GetEnvironmentVariable("FILEBROWSER_CROSS_DEVICE_ROOT");
        if (string.IsNullOrWhiteSpace(secondaryRoot)
            || !Path.IsPathFullyQualified(secondaryRoot)
            || !Directory.Exists(secondaryRoot))
        {
            return;
        }

        var destinationRoot = Path.Combine(secondaryRoot, $"filebrowser-cross-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationRoot);
        try
        {
            using var workspace = new TestWorkspace();
            var probeSource = Path.Combine(workspace.RootPath, "probe.txt");
            var probeDestination = Path.Combine(destinationRoot, "probe.txt");
            File.WriteAllText(probeSource, "probe");
            try
            {
                Directory.Move(probeSource, probeDestination);
                File.Delete(probeDestination);
                return;
            }
            catch (IOException exception) when (exception.HResult == 18)
            {
                // Cross-device confirmed.
            }
            catch
            {
                return;
            }

            File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "cross-device");
            var sourceDirectory = Path.Combine(workspace.RootPath, "album");
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty"));
            File.WriteAllText(Path.Combine(sourceDirectory, "track.txt"), "track");
            var (repository, resolver) = CreateRepositoryAndResolver(workspace, destinationRoot);
            var executor = new MoveTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
            var task = await CreateScanningTaskAsync(repository,
                [new FileRef("source", "note.txt"), new FileRef("source", "album")],
                new FileRef("destination", ""));

            await executor.ExecuteAsync(task, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
            Assert.False(Directory.Exists(sourceDirectory));
            Assert.Equal("cross-device", File.ReadAllText(Path.Combine(destinationRoot, "note.txt")));
            Assert.Equal("track", File.ReadAllText(Path.Combine(destinationRoot, "album", "track.txt")));
            Assert.True(Directory.Exists(Path.Combine(destinationRoot, "album", "empty")));
            Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        }
        finally
        {
            Directory.Delete(destinationRoot, recursive: true);
        }
    }

    private static async Task<FileTask> CreateScanningTaskAsync(TaskRepository repository, IReadOnlyList<FileRef> sources, FileRef destination)
    {
        var task = await repository.CreateQueuedAsync(FileTaskType.Move, sources, destination);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        return (await repository.GetAsync(task.Id))!;
    }

    private static (TaskRepository Repository, PathResolver Resolver) CreateRepositoryAndResolver(TestWorkspace workspace, string destinationRoot)
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
        return (new TaskRepository(database), new PathResolver(FileBrowserConfigurationLoader.Load(configPath)));
    }

    private static string CreateDestinationRoot(TestWorkspace workspace)
    {
        var path = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "destination");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid taskId, FileTaskStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var task = await repository.GetAsync(taskId, timeout.Token);
            if (task?.Status == expected)
            {
                return task;
            }
            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException($"Задача {taskId} не достигла состояния {expected}.");
    }

    private class CrossDeviceEngine(PathResolver resolver, TaskRepository repository, TaskProgressService progress)
        : FileTransferEngine(resolver, repository, progress)
    {
        internal override bool TryRenameEntry(FileTransferPlanEntry entry) => false;
    }

    private sealed class FailingCopyEngine(PathResolver resolver, TaskRepository repository, TaskProgressService progress)
        : CrossDeviceEngine(resolver, repository, progress)
    {
        internal override Task CopyEntryAsync(FileTransferPlanEntry entry, string destinationPath, TransferProgress progress, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("copy failed"));
    }

    private sealed class FailingDeleteEngine(PathResolver resolver, TaskRepository repository, TaskProgressService progress)
        : CrossDeviceEngine(resolver, repository, progress)
    {
        internal override void DeleteSource(ResolvedPath sourcePath) =>
            throw new UnauthorizedAccessException("delete failed");
    }

    private sealed class SelectiveFailureEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        string failingPath) : FileTransferEngine(resolver, repository, progress)
    {
        internal override bool TryRenameEntry(FileTransferPlanEntry entry)
        {
            if (entry.Source.Path == failingPath)
            {
                throw new IOException("rename failed");
            }

            return base.TryRenameEntry(entry);
        }
    }
}
