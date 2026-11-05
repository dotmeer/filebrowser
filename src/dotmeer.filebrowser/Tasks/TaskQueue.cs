using System.Threading.Channels;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
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

public sealed class TaskDispatcher(TaskRepository repository, TaskQueue queue)
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
            return task;
        }

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
    Task ExecuteAsync(FileTask task, CancellationToken cancellationToken);
}

public sealed class TaskExecutorNotReadyException : Exception
{
    public TaskExecutorNotReadyException() : base("Исполнитель файловых задач ещё не реализован.")
    {
    }
}

public sealed class NotReadyFileTaskExecutor : IFileTaskExecutor
{
    public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken) =>
        Task.FromException(new TaskExecutorNotReadyException());
}

public sealed class TaskExecutorHostedService(
    TaskRepository repository,
    TaskQueue queue,
    IFileTaskExecutor executor,
    TemporaryArtifactCleanupService artifactCleanup,
    TaskProgressService runtimeProgress,
    FileBrowserConfiguration configuration,
    ILogger<TaskExecutorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interrupted = await repository.InterruptActiveTasksAsync(stoppingToken);
        if (interrupted > 0)
        {
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
            Guid taskId;
            try
            {
                taskId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var queuedTask = await repository.GetAsync(taskId, stoppingToken);
                if (queuedTask?.Status != FileTaskStatus.Queued)
                {
                    continue;
                }

                await repository.TransitionAsync(taskId, FileTaskStatus.Scanning, "scanning", cancellationToken: stoppingToken);
                var scanningTask = await repository.GetAsync(taskId, stoppingToken)
                    ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                await executor.ExecuteAsync(scanningTask, stoppingToken);
                var executedTask = await repository.GetAsync(taskId, stoppingToken)
                    ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                if (executedTask.Status == FileTaskStatus.Scanning)
                {
                    await repository.TransitionAsync(taskId, FileTaskStatus.Running, "running", cancellationToken: stoppingToken);
                    executedTask = await repository.GetAsync(taskId, stoppingToken)
                        ?? throw new KeyNotFoundException("Задача исчезла из хранилища.");
                }

                if (executedTask.Status != FileTaskStatus.Running)
                {
                    throw new InvalidOperationException("Исполнитель оставил задачу в недопустимом состоянии.");
                }

                await repository.TransitionAsync(taskId, FileTaskStatus.Completed, "completed", cancellationToken: stoppingToken);
                runtimeProgress.Clear(taskId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Исполнитель задачи {TaskId} завершился ошибкой", taskId);
                await FailTaskSafelyAsync(taskId, exception, stoppingToken);
                runtimeProgress.Clear(taskId);
            }
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
                    ? taskException.SafeMessage
                    : "Исполнитель задачи завершился ошибкой.";
                await repository.TransitionAsync(taskId, FileTaskStatus.Failed, "failed", error, cancellationToken);
            }
        }
        catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
        {
            logger.LogError(persistenceException, "Не удалось сохранить ошибку задачи {TaskId}", taskId);
        }
    }
}

public sealed class FileTaskExecutionException(string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string SafeMessage { get; } = safeMessage;
}
