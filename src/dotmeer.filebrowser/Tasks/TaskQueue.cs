using System.Diagnostics;
using System.Threading.Channels;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using Microsoft.Extensions.Hosting;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100)
    {
        SingleWriter = false,
        SingleReader = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    public bool TryEnqueue(Guid taskId) => _channel.Writer.TryWrite(taskId);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAsync(cancellationToken);
}

public sealed class TaskDispatcher(
    TaskRepository repository,
    TaskQueue queue,
    TaskMetrics? metrics = null,
    ILogger<TaskDispatcher>? logger = null)
{
    public async Task<FileTask> EnqueueAsync(FileTaskType type, IReadOnlyList<FileRef> sources, FileRef destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(destination.Root) || destination.Path is null)
        {
            throw new ArgumentException("Назначение содержит некорректную ссылку.", nameof(destination));
        }

        if (sources.Any(source => source is null
                || string.IsNullOrWhiteSpace(source.Root)
                || source.Path is null))
        {
            throw new ArgumentException("Список источников содержит некорректную ссылку.", nameof(sources));
        }

        var normalizedSources = sources
            .DistinctBy(source => (source.Root, source.Path), FileRefTupleComparer.Instance)
            .OrderBy(source => $"{source.Root}/{source.Path}", StringComparer.Ordinal)
            .ToArray();
        ValidateSourceList(normalizedSources);
        var task = await repository.CreateQueuedAsync(type, normalizedSources, destination, cancellationToken);
        if (queue.TryEnqueue(task.Id))
        {
            metrics?.TaskEnqueued(task.Type);
            logger?.LogInformation(
                "Поставлена задача {TaskId}: тип={TaskType}, источников={SourceCount}, корень назначения={DestinationRoot}",
                task.Id,
                task.Type,
                task.Sources.Count,
                task.Destination.Root);
            return task;
        }

        metrics?.TaskRejected(task.Type);
        logger?.LogWarning("Очередь задач заполнена; задача {TaskId} удалена из хранилища", task.Id);
        await repository.DeleteAsync(task.Id, cancellationToken);
        throw new FileTaskQueueFullException();
    }

    private static void ValidateSourceList(IReadOnlyList<FileRef> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("Для задачи требуется хотя бы один источник.", nameof(sources));
        }

        if (sources.Any(source => string.IsNullOrEmpty(source.Path)))
        {
            throw new InvalidFileSystemRequestException("Корень нельзя копировать или перемещать.");
        }

        var firstParent = GetPublicParent(sources[0]);
        for (var index = 1; index < sources.Count; index++)
        {
            if (!SamePublicParent(firstParent, GetPublicParent(sources[index])))
            {
                throw new InvalidFileSystemRequestException("Все источники должны находиться в одной родительской папке.");
            }
        }
    }

    private static (string Root, string Path) GetPublicParent(FileRef source)
    {
        var separator = source.Path.LastIndexOf('/');
        return (source.Root, separator < 0 ? string.Empty : source.Path[..separator]);
    }

    private static bool SamePublicParent((string Root, string Path) first, (string Root, string Path) second) =>
        first.Root.Equals(second.Root, StringComparison.OrdinalIgnoreCase)
        && first.Path.Equals(second.Path, StringComparison.Ordinal);
}

internal sealed class FileRefTupleComparer : IEqualityComparer<(string Root, string Path)>
{
    public static FileRefTupleComparer Instance { get; } = new();

    public bool Equals((string Root, string Path) x, (string Root, string Path) y) =>
        x.Root.Equals(y.Root, StringComparison.OrdinalIgnoreCase)
        && x.Path.Equals(y.Path, StringComparison.Ordinal);

    public int GetHashCode((string Root, string Path) value) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Root), StringComparer.Ordinal.GetHashCode(value.Path));
}

public interface IFileTaskExecutor
{
    /// <param name="cancellationToken">
    /// Linked token: fires on application shutdown or on a user-initiated cancel.
    /// </param>
    /// <param name="shutdownToken">
    /// Application shutdown token without the user-cancel part. Lets executors
    /// distinguish a user cancel (finalize as cancelled) from a shutdown
    /// (leave the task active; startup marks it interrupted).
    /// </param>
    Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken);
}

public sealed class TaskExecutorHostedService(
    TaskRepository repository,
    TaskQueue queue,
    IFileTaskExecutor executor,
    TemporaryArtifactCleanupService artifactCleanup,
    TaskProgressService runtimeProgress,
    TaskCancellationRegistry cancellationRegistry,
    FileBrowserConfiguration configuration,
    ILogger<TaskExecutorHostedService> logger,
    TaskMetrics? metrics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interrupted = await repository.InterruptActiveTasksAsync(stoppingToken);
        if (interrupted > 0)
        {
            metrics?.TasksInterrupted(interrupted);
            logger.LogWarning("Восстановлено прерванных задач: {TaskCount}", interrupted);
        }
        await artifactCleanup.CleanupAsync(stoppingToken);

        var workers = Enumerable.Range(0, configuration.Workers)
            .Select(_ => RunWorkerAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid taskId = Guid.Empty;
            FileTask? task = null;
            DateTimeOffset executionStartedUtc = DateTimeOffset.UtcNow;
            Activity? activity = null;
            try
            {
                taskId = await queue.DequeueAsync(stoppingToken);
                metrics?.TaskDequeued();
                task = await repository.GetAsync(taskId, stoppingToken);
                if (task?.Status != FileTaskStatus.Queued)
                {
                    continue;
                }

                metrics?.TaskStarted(task.Type);
                executionStartedUtc = DateTimeOffset.UtcNow;
                activity = StartTaskActivity(task);

                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    cancellationRegistry.Register(taskId).Token);
                try
                {
                    await repository.TransitionAsync(taskId, FileTaskStatus.Scanning, "scanning", cancellationToken: stoppingToken);
                    task = await repository.GetAsync(taskId, stoppingToken)
                        ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                    await executor.ExecuteAsync(task, linkedCancellation.Token, stoppingToken);
                    var executedTask = await repository.GetAsync(taskId, stoppingToken)
                        ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                    if (executedTask.Status == FileTaskStatus.Scanning)
                    {
                        await repository.TransitionAsync(taskId, FileTaskStatus.Running, "running", cancellationToken: stoppingToken);
                        executedTask = await repository.GetAsync(taskId, stoppingToken)
                            ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                    }

                    if (executedTask.Status == FileTaskStatus.Cancelling)
                    {
                        // Отмена выиграла гонку: работа фактически завершена,
                        // результат сохраняется, задача фиксируется как отменённая.
                        await repository.TransitionAsync(taskId, FileTaskStatus.Cancelled, "cancelled", FileTaskErrorCodes.CancelledByUser, stoppingToken);
                        activity?.SetTag("filebrowser.task.status", "cancelled");
                        metrics?.TaskCancelled(executedTask.Type, DateTimeOffset.UtcNow - executionStartedUtc);
                        runtimeProgress.Clear(taskId);
                        continue;
                    }

                    if (executedTask.Status != FileTaskStatus.Running)
                    {
                        throw new InvalidOperationException("Исполнитель оставил задачу в недопустимом состоянии.");
                    }

                    await repository.TransitionAsync(taskId, FileTaskStatus.Completed, "completed", cancellationToken: stoppingToken);
                    activity?.SetTag("filebrowser.task.status", "completed");
                    metrics?.TaskCompleted(executedTask.Type, DateTimeOffset.UtcNow - executionStartedUtc);
                    runtimeProgress.Clear(taskId);
                }
                finally
                {
                    cancellationRegistry.Unregister(taskId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidFileTaskTransitionException) when (taskId != Guid.Empty)
            {
                // Возможная гонка отмены с запуском задачи; любые другие
                // недопустимые переходы — это дефект и должны падать в generic-catch.
                var current = await repository.GetAsync(taskId, CancellationToken.None);
                if (current is null || current.Status is not (FileTaskStatus.Cancelling or FileTaskStatus.Cancelled))
                {
                    throw;
                }

                logger.LogDebug("Задача {TaskId} отменена конкурентно с запуском воркера", taskId);
                await CancelTaskSafelyAsync(taskId, stoppingToken);
                runtimeProgress.Clear(taskId);
            }
            catch (Exception exception) when (exception is OperationCanceledException or FileTaskCancelledException)
            {
                // Пользовательская отмена (остановка приложения отфильтрована выше).
                activity?.SetTag("filebrowser.task.status", "cancelled");
                metrics?.TaskCancelled(task?.Type, DateTimeOffset.UtcNow - executionStartedUtc);
                logger.LogInformation("Задача {TaskId} отменена по запросу пользователя", taskId);
                await CancelTaskSafelyAsync(taskId, stoppingToken);
                runtimeProgress.Clear(taskId);
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("filebrowser.task.status", "failed");
                activity?.SetTag("error.type", exception.GetType().Name);
                metrics?.TaskFailed(task?.Type, DateTimeOffset.UtcNow - executionStartedUtc);
                logger.LogWarning(exception, "Исполнитель задачи {TaskId} завершился ошибкой", taskId);
                await FailTaskSafelyAsync(taskId, exception, stoppingToken);
                runtimeProgress.Clear(taskId);
            }
            finally
            {
                activity?.Dispose();
            }
        }
    }

    private async Task CancelTaskSafelyAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await repository.CancelPendingItemsAsync(taskId, FileTaskErrorCodes.CancelledByUser, cancellationToken);
            var task = await repository.GetAsync(taskId, cancellationToken);
            if (task is not null && FileTaskStateTransitions.IsAllowed(task.Status, FileTaskStatus.Cancelled))
            {
                await repository.TransitionAsync(taskId, FileTaskStatus.Cancelled, "cancelled", FileTaskErrorCodes.CancelledByUser, cancellationToken);
            }
        }
        catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
        {
            logger.LogError(persistenceException, "Не удалось зафиксировать отмену задачи {TaskId}", taskId);
        }
    }

    private async Task FailTaskSafelyAsync(Guid taskId, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            var task = await repository.GetAsync(taskId, cancellationToken);
            if (task is not null && FileTaskStateTransitions.IsAllowed(task.Status, FileTaskStatus.Failed))
            {
                var error = exception is FileTaskExecutionException taskException
                    ? taskException.SafeCode
                    : FileTaskErrorCodes.ExecutorFailed;
                await repository.TransitionAsync(taskId, FileTaskStatus.Failed, "failed", error, cancellationToken);
            }
        }
        catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
        {
            logger.LogError(persistenceException, "Не удалось сохранить ошибку задачи {TaskId}", taskId);
        }
    }

    private static Activity? StartTaskActivity(FileTask task)
    {
        var activity = FileBrowserTelemetry.ActivitySource.StartActivity(
            "filebrowser.task.execute",
            ActivityKind.Internal);
        activity?.SetTag("filebrowser.task.id", task.Id.ToString("D"));
        activity?.SetTag("filebrowser.task.type", task.Type.ToString().ToLowerInvariant());
        activity?.SetTag("filebrowser.task.source_count", task.Sources.Count);
        activity?.SetTag("filebrowser.task.destination_root", task.Destination.Root);
        return activity;
    }
}

public sealed class FileTaskExecutionException(string safeCode, Exception? innerException = null)
    : Exception(safeCode, innerException)
{
    /// <summary>
    /// Stable error code persisted in SQLite and translated at presentation time.
    /// </summary>
    public string SafeCode { get; } = safeCode;
}
