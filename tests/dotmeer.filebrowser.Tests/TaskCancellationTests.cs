using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class TaskCancellationTests
{
    [Fact]
    public void Transitions_AllowCancelPathsAndForbidOthers()
    {
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Queued, FileTaskStatus.Cancelled));
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Scanning, FileTaskStatus.Cancelling));
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Running, FileTaskStatus.Cancelling));
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Cancelling, FileTaskStatus.Cancelled));
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Cancelling, FileTaskStatus.Failed));
        Assert.True(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Cancelling, FileTaskStatus.Interrupted));

        Assert.False(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Queued, FileTaskStatus.Cancelling));
        Assert.False(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Running, FileTaskStatus.Cancelled));
        Assert.False(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Completed, FileTaskStatus.Cancelling));
        Assert.False(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Cancelled, FileTaskStatus.Running));
        Assert.False(FileTaskStateTransitions.IsAllowed(FileTaskStatus.Interrupted, FileTaskStatus.Cancelled));

        Assert.True(FileTaskStateTransitions.IsActive(FileTaskStatus.Cancelling));
        Assert.False(FileTaskStateTransitions.IsActive(FileTaskStatus.Cancelled));
    }

    [Fact]
    public async Task Repository_ActiveAndHistoryQueriesIncludeCancelStatuses()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var cancelled = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "cancelled.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(cancelled.Id, FileTaskStatus.Cancelled, "cancelled", FileTaskErrorCodes.CancelledByUser);
        var cancelling = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "cancelling.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(cancelling.Id, FileTaskStatus.Scanning, "scanning");
        await repository.TransitionAsync(cancelling.Id, FileTaskStatus.Running, "running");
        await repository.TransitionAsync(cancelling.Id, FileTaskStatus.Cancelling, "cancelling");
        var queued = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("files", "queued.txt")], new FileRef("files", "target"));

        var active = await repository.GetActiveAsync();
        var history = await repository.GetHistoryPageAsync(0, 50);

        Assert.Equal([cancelling.Id, queued.Id], active.Select(task => task.Id).ToArray());
        Assert.Equal(cancelled.Id, Assert.Single(history.Tasks).Id);
        Assert.NotNull((await repository.GetAsync(cancelled.Id))!.CompletedAtUtc);

        var deleted = await repository.DeleteInactiveAsync();
        Assert.Equal(1, deleted);
        Assert.Null(await repository.GetAsync(cancelled.Id));
        Assert.NotNull(await repository.GetAsync(cancelling.Id));
    }

    [Fact]
    public async Task Repository_CancelPendingItemsTouchesOnlyPendingRows()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy,
            [new FileRef("files", "a.txt"), new FileRef("files", "b.txt")], new FileRef("files", "target"));
        await repository.UpdateTaskItemAsync(task.Id, new FileRef("files", "a.txt"), "completed", null, 1, 1);

        await repository.CancelPendingItemsAsync(task.Id, FileTaskErrorCodes.CancelledByUser);

        var stored = (await repository.GetAsync(task.Id))!;
        Assert.Equal("completed", stored.Items[0].Status);
        Assert.Equal("not_processed", stored.Items[1].Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, stored.Items[1].Error);
    }

    [Fact]
    public void Registry_TryRegisterAllowsExactlyOneOwner()
    {
        var registry = new TaskCancellationRegistry();
        var taskId = Guid.NewGuid();

        Assert.True(registry.TryRegister(taskId, out var first));
        Assert.NotNull(first);
        Assert.False(registry.TryRegister(taskId, out var second));
        Assert.Null(second);

        registry.Unregister(taskId);
        Assert.True(registry.TryRegister(taskId, out var third));
        Assert.False(third.IsCancellationRequested);
        registry.Unregister(taskId);
    }

    [Fact]
    public async Task CancelBeforeTaskRowExists_IsObservedByLateRegistration()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var registry = new TaskCancellationRegistry();
        var cancellation = new TaskCancellationService(repository, registry, new TaskOperationGate());
        var uploadId = Guid.NewGuid();

        await cancellation.CancelAsync(uploadId);

        using var source = registry.Register(uploadId);
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelService_IsIdempotentForMissingAndTerminalTasks()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var registry = new TaskCancellationRegistry();
        var service = new TaskCancellationService(repository, registry, new TaskOperationGate());

        await service.CancelAsync(Guid.NewGuid());

        var completed = await CreateTerminalTaskAsync(repository, FileTaskStatus.Completed);
        await service.CancelAsync(completed.Id);
        await service.CancelAsync(completed.Id);

        Assert.Equal(FileTaskStatus.Completed, (await repository.GetAsync(completed.Id))!.Status);
    }

    [Fact]
    public async Task CancelService_QueuedTaskBecomesCancelledImmediately()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var service = new TaskCancellationService(repository, new TaskCancellationRegistry(), new TaskOperationGate());
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "a.txt")], new FileRef("files", "target"));

        await service.CancelAsync(task.Id);

        var cancelled = (await repository.GetAsync(task.Id))!;
        Assert.Equal(FileTaskStatus.Cancelled, cancelled.Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, cancelled.Error);
        Assert.NotNull(cancelled.CompletedAtUtc);
    }

    [Fact]
    public async Task CancelService_RunningTaskTransitionsToCancellingAndFiresRegistry()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var registry = new TaskCancellationRegistry();
        var service = new TaskCancellationService(repository, registry, new TaskOperationGate());
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "a.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");
        var registered = registry.Register(task.Id);

        await service.CancelAsync(task.Id);

        Assert.True(registered.IsCancellationRequested);
        Assert.Equal(FileTaskStatus.Cancelling, (await repository.GetAsync(task.Id))!.Status);

        // Повторная отмена — no-op.
        await service.CancelAsync(task.Id);
        Assert.Equal(FileTaskStatus.Cancelling, (await repository.GetAsync(task.Id))!.Status);
        registry.Unregister(task.Id);
    }

    [Fact]
    public async Task Worker_CancelRunningTaskTransitionsToCancelled()
    {
        using var workspace = new TestWorkspace();
        var executor = new GatedExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var cancellation = host.Services.GetRequiredService<TaskCancellationService>();

        var task = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "first.txt")], new FileRef("files", "target"));
        await executor.WaitForStartedAsync(1);

        await cancellation.CancelAsync(task.Id);

        var cancelled = await WaitForStatusAsync(repository, task.Id, FileTaskStatus.Cancelled);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, cancelled.Error);
        Assert.Equal("not_processed", Assert.Single(cancelled.Items).Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, cancelled.Items[0].Error);
    }

    [Fact]
    public async Task Worker_CancelQueuedTaskSkipsExecution()
    {
        using var workspace = new TestWorkspace();
        var executor = new GatedExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var cancellation = host.Services.GetRequiredService<TaskCancellationService>();

        var first = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "first.txt")], new FileRef("files", "target"));
        var second = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "second.txt")], new FileRef("files", "target"));
        await executor.WaitForStartedAsync(1);

        await cancellation.CancelAsync(second.Id);
        Assert.Equal(FileTaskStatus.Cancelled, (await repository.GetAsync(second.Id))!.Status);

        executor.Release();
        await WaitForStatusAsync(repository, first.Id, FileTaskStatus.Completed);
        await Task.Delay(200);
        Assert.Equal(FileTaskStatus.Cancelled, (await repository.GetAsync(second.Id))!.Status);
        Assert.DoesNotContain(second.Id, executor.StartedIds);
    }

    [Fact]
    public async Task Move_UserCancelDuringFallbackCopyKeepsSourceAndCleansArtifact()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "destination");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "content");
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: source
                path: '{workspace.RootPath}'
              - name: destination
                path: '{destinationRoot}'
            """);
        var repository = CreateRepository(workspace, configPath);
        var resolver = new PathResolver(FileBrowserConfigurationLoader.Load(configPath));
        using var cancellation = new CancellationTokenSource();
        var engine = new CancellingFallbackEngine(resolver, repository, new TaskProgressService(), cancellation);
        var executor = new MoveTaskExecutor(engine);
        var task = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("source", "note.txt")], new FileRef("destination", "inbox"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");

        await Assert.ThrowsAsync<FileTaskCancelledException>(() => executor.ExecuteAsync(task, cancellation.Token, CancellationToken.None));

        var stored = (await repository.GetAsync(task.Id))!;
        Assert.Equal("not_processed", Assert.Single(stored.Items).Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, stored.Items[0].Error);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "note.txt")));
        Assert.False(File.Exists(Path.Combine(destinationRoot, "inbox", "note.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(destinationRoot, "inbox"), ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task InterruptActiveTasks_IncludesCancelling()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "a.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");
        await repository.TransitionAsync(task.Id, FileTaskStatus.Cancelling, "cancelling");

        var interrupted = await repository.InterruptActiveTasksAsync();

        Assert.Equal(1, interrupted);
        Assert.Equal(FileTaskStatus.Interrupted, (await repository.GetAsync(task.Id))!.Status);
    }

    private static TaskRepository CreateRepository(TestWorkspace workspace) =>
        CreateRepository(workspace, workspace.WriteConfiguration(workspace.RootPath));

    private static TaskRepository CreateRepository(TestWorkspace workspace, string configPath)
    {
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        return new TaskRepository(database);
    }

    private static async Task<FileTask> CreateTerminalTaskAsync(TaskRepository repository, FileTaskStatus status)
    {
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "done.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");
        await repository.TransitionAsync(task.Id, status, status.ToString().ToLowerInvariant());
        return (await repository.GetAsync(task.Id))!;
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

    private static TestHostFactory.TestHost CreateHost(string configPath, IFileTaskExecutor executor) =>
        TestHostFactory.Create(
            configPath,
            configureServices: services =>
            {
                services.RemoveAll<IFileTaskExecutor>();
                services.AddSingleton(executor);
            });

    private sealed class GatedExecutor : IFileTaskExecutor
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _startedIds = new();
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Guid> StartedIds => _startedIds.ToArray();

        public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
        {
            _startedIds.Enqueue(task.Id);
            _started.Release();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStartedAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                if (!await _started.WaitAsync(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Задача не была запущена вовремя.");
                }
            }
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class CancellingFallbackEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        CancellationTokenSource cancellation) : FileTransferEngine(resolver, repository, progress)
    {
        internal override bool TryRenameEntry(FileTransferPlanEntry entry) => false;

        internal override Task CopyEntryAsync(
            FileTransferPlanEntry entry,
            string destinationPath,
            TransferProgress progress,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.CopyEntryAsync(entry, destinationPath, progress, cancellationToken);
        }
    }
}
