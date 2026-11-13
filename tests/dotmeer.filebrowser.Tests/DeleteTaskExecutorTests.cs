using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class DeleteTaskExecutorTests
{
    [Fact]
    public async Task Execute_DeletesFileAndDirectoryAndLeavesNoArtifacts()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "file content");
        var album = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(Path.Combine(album, "empty"));
        File.WriteAllText(Path.Combine(album, "track.txt"), "track content");
        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "note.txt"), new FileRef("source", "album")]);

        await executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None);

        var running = await repository.GetAsync(task.Id);
        Assert.NotNull(running);
        Assert.Equal(FileTaskStatus.Running, running.Status);
        Assert.Equal(2, running.ItemsDone);
        Assert.Equal(2, running.ItemsTotal);
        Assert.Equal("file content".Length + "track content".Length, running.BytesDone);
        Assert.Null(running.BytesTotal);
        Assert.All(running.Items, item => Assert.Equal("completed", item.Status));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.False(Directory.Exists(album));
        // Пространство имён полностью очищено: скрытых артефактов не осталось.
        Assert.Empty(Directory.GetFileSystemEntries(workspace.RootPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_DeletesDeepDirectoryTree()
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
        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "removable")]);

        await executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None);

        Assert.False(Directory.Exists(removable));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_RejectsDirectSymlinkSource()
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

        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "link.txt")]);

        var failure = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, failure.SafeCode);
        Assert.True(File.Exists(link));
        Assert.Equal("content", File.ReadAllText(target));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.UnsupportedOrSymlink, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_RejectsDirectTemporaryArtifactPath()
    {
        using var workspace = new TestWorkspace();
        var artifactPath = Path.Combine(workspace.RootPath, ".filebrowser-other");
        File.WriteAllText(artifactPath, "temporary");
        var (repository, executor, _) = CreateExecutor(workspace);
        var otherTask = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef("source", "source.txt")],
            new FileRef("source", string.Empty));
        await repository.RegisterTemporaryArtifactAsync(otherTask.Id, new FileRef("source", ".filebrowser-other"));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", ".filebrowser-other")]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(File.Exists(artifactPath));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.SourceUnavailable, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_DeletesUnderVirtualRootButRejectsVirtualRootItself()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "note");
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: alpha
                path: '{workspace.RootPath}'
            """);
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        var repository = new TaskRepository(database);
        var paths = new ApplicationPaths(configPath, database.DatabasePath);
        var executor = new DeleteTaskExecutor(CreateEngine(repository, paths));

        var deletion = await CreateScanningTaskAsync(
            repository,
            [new FileRef("AllRoots", "alpha/note.txt")]);
        await executor.ExecuteAsync(deletion, CancellationToken.None, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));

        var rootTask = await CreateScanningTaskAsync(
            repository,
            [new FileRef("AllRoots", "alpha")]);
        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(rootTask, CancellationToken.None, CancellationToken.None));
        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public async Task Execute_RejectsReadOnlyObjectBeforeDeletingAnything()
    {
        using var workspace = new TestWorkspace();
        var readOnlyPath = Path.Combine(workspace.RootPath, "locked.txt");
        File.WriteAllText(readOnlyPath, "content");
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "locked.txt")]);

        try
        {
            var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
                () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

            Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
            Assert.True(File.Exists(readOnlyPath));
            var failed = await repository.GetAsync(task.Id);
            Assert.Equal("failed", Assert.Single(failed!.Items).Status);
            Assert.Equal(FileTaskErrorCodes.ReadOnlyObject, failed.Items[0].Error);
            Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        }
        finally
        {
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Execute_RejectsDirectoryContainingSymlink()
    {
        using var workspace = new TestWorkspace();
        var album = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(album);
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(album, "link.txt"), target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "album")]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(Directory.Exists(album));
        Assert.True(File.Exists(target));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.UnsupportedOrSymlink, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_RejectsTreeContainingApplicationServiceFiles()
    {
        using var workspace = new TestWorkspace();
        var dataDirectory = Path.Combine(workspace.RootPath, "data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "payload.txt"), "payload");
        var databasePath = Path.Combine(dataDirectory, "app.db");
        var (repository, _, paths) = CreateExecutor(workspace);
        var engineWithServiceFiles = CreateEngine(repository, paths with { DatabasePath = databasePath });
        var deleting = new DeleteTaskExecutor(engineWithServiceFiles);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "data")]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => deleting.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(Directory.Exists(dataDirectory));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.ServiceFileProtected, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_RejectsTreeContainingRegisteredTemporaryArtifact()
    {
        using var workspace = new TestWorkspace();
        var album = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "track.txt"), "track");
        var (repository, executor, _) = CreateExecutor(workspace);
        var otherTask = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef("source", "other.txt")],
            new FileRef("source", ""));
        Directory.CreateDirectory(Path.Combine(album, ".filebrowser-other"));
        await repository.RegisterTemporaryArtifactAsync(
            otherTask.Id,
            new FileRef("source", "album/.filebrowser-other/in-flight.bin"));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "album")]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(Directory.Exists(album));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.ArtifactInUse, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_RejectsRootAsSource()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "keep.txt"), "keep");
        var (repository, executor, _) = CreateExecutor(workspace);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", string.Empty)]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.PreflightFailed, exception.SafeCode);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "keep.txt")));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.RootAsSource, Assert.Single(failed!.Items).Error);
    }

    [Fact]
    public async Task Execute_CancelBetweenObjectsMarksCurrentAndRemainingItems()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "first");
        File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "second");
        var (repository, _, paths) = CreateExecutor(workspace);
        using var cancellation = new CancellationTokenSource();
        var engine = new CancelAfterFirstObjectEngine(
            new PathResolver(FileBrowserConfigurationLoader.Load(paths.ConfigPath)),
            repository,
            new TaskProgressService(),
            paths,
            cancellation);
        var executor = new DeleteTaskExecutor(engine);
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "a.txt"), new FileRef("source", "b.txt")]);

        await Assert.ThrowsAsync<FileTaskCancelledException>(
            () => executor.ExecuteAsync(task, cancellation.Token, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "b.txt")));
        var cancelled = await repository.GetAsync(task.Id);
        Assert.Equal("completed", cancelled!.Items[0].Status);
        Assert.Equal("not_processed", cancelled.Items[1].Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, cancelled.Items[1].Error);
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_IncompleteDeletionKeepsArtifactForStartupCleanup()
    {
        using var workspace = new TestWorkspace();
        var album = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "track.txt"), "track");
        var (repository, _, paths) = CreateExecutor(workspace);
        var engine = new FailingDeleteEngine(
            new PathResolver(FileBrowserConfigurationLoader.Load(paths.ConfigPath)),
            repository,
            new TaskProgressService(),
            paths);
        var executor = new DeleteTaskExecutor(engine);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "album")]);

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));

        Assert.Equal(FileTaskErrorCodes.DeleteIncomplete, exception.SafeCode);
        Assert.False(Directory.Exists(album));
        var failed = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskErrorCodes.DeleteIncomplete, Assert.Single(failed!.Items).Error);
        var artifact = Assert.Single(await repository.GetTemporaryArtifactsAsync());
        Assert.StartsWith(".filebrowser-", Path.GetFileName(artifact.File.Path), StringComparison.Ordinal);
        var artifactPath = Path.Combine(workspace.RootPath, Path.GetFileName(artifact.File.Path));
        Assert.True(Directory.Exists(artifactPath));

        var cleanup = new TemporaryArtifactCleanupService(
            repository,
            new PathResolver(FileBrowserConfigurationLoader.Load(paths.ConfigPath)),
            NullLogger<TemporaryArtifactCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);

        Assert.False(Directory.Exists(artifactPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.Empty(Directory.GetFileSystemEntries(workspace.RootPath));
    }

    [Fact]
    public void Presentation_DeletePercentUsesObjectCounters()
    {
        var now = DateTimeOffset.UtcNow;
        var task = new FileTask(
            Guid.NewGuid(),
            FileTaskType.Delete,
            [new FileRef("source", "a")],
            [],
            new FileRef("source", ""),
            FileTaskStatus.Running,
            "running",
            0,
            null,
            1,
            4,
            null,
            now,
            now,
            now,
            null);

        Assert.Equal(25, TaskPresentation.GetPercent(task));
    }

    private static async Task<FileTask> CreateScanningTaskAsync(TaskRepository repository, IReadOnlyList<FileRef> sources)
    {
        var task = await repository.CreateQueuedAsync(FileTaskType.Delete, sources, new FileRef("source", string.Empty));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        return (await repository.GetAsync(task.Id))!;
    }

    private static (TaskRepository Repository, DeleteTaskExecutor Executor, ApplicationPaths Paths) CreateExecutor(TestWorkspace workspace)
    {
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: source
                path: '{workspace.RootPath}'
            """);
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        var repository = new TaskRepository(database);
        var paths = new ApplicationPaths(configPath, database.DatabasePath);
        return (repository, new DeleteTaskExecutor(CreateEngine(repository, paths)), paths);
    }

    private static FileDeleteEngine CreateEngine(TaskRepository repository, ApplicationPaths paths) =>
        new(
            new PathResolver(FileBrowserConfigurationLoader.Load(paths.ConfigPath)),
            repository,
            new TaskProgressService(),
            paths);

    private sealed class CancelAfterFirstObjectEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        ApplicationPaths paths,
        CancellationTokenSource cancellation)
        : FileDeleteEngine(resolver, repository, progress, paths)
    {
        internal override Task DeleteArtifactAsync(DeleteArtifact artifact, CancellationToken cancellationToken)
        {
            var result = base.DeleteArtifactAsync(artifact, cancellationToken);
            cancellation.Cancel();
            return result;
        }
    }

    private sealed class FailingDeleteEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        ApplicationPaths paths)
        : FileDeleteEngine(resolver, repository, progress, paths)
    {
        internal override Task DeleteArtifactAsync(DeleteArtifact artifact, CancellationToken cancellationToken) =>
            Task.FromException(new FileDeleteIncompleteException(new IOException("delete failed")));
    }
}
