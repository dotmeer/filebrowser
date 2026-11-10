using System.Collections.Concurrent;

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// In-memory registry of per-task cancellation sources. A worker registers
/// a source before the task starts executing; <see cref="TaskCancellationService"/>
/// fires it on a user-initiated cancel. The registry is deliberately not
/// persisted: after a restart active tasks are interrupted anyway.
/// </summary>
public sealed class TaskCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationTokenSource Register(Guid taskId) =>
        _sources.GetOrAdd(taskId, static _ => new CancellationTokenSource());

    public void Unregister(Guid taskId)
    {
        if (_sources.TryRemove(taskId, out var source))
        {
            source.Dispose();
        }
    }

    public bool TryCancel(Guid taskId)
    {
        if (_sources.TryGetValue(taskId, out var source))
        {
            source.Cancel();
            return true;
        }

        return false;
    }
}

/// <summary>
/// User-initiated task cancellation. Idempotent by design: cancelling a
/// finished, missing or already cancelling task is a silent no-op.
/// </summary>
public sealed class TaskCancellationService(
    TaskRepository repository,
    TaskCancellationRegistry registry,
    ILogger<TaskCancellationService>? logger = null)
{
    public async Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            return;
        }

        switch (task.Status)
        {
            case FileTaskStatus.Queued:
                await TransitionSafelyAsync(taskId, FileTaskStatus.Cancelled, "cancelled", FileTaskErrorCodes.CancelledByUser, cancellationToken);
                return;
            case FileTaskStatus.Scanning:
            case FileTaskStatus.Running:
                await TransitionSafelyAsync(taskId, FileTaskStatus.Cancelling, "cancelling", null, cancellationToken);
                registry.TryCancel(taskId);
                return;
            default:
                // Терминальные состояния и cancelling: повторная отмена ничего не меняет.
                return;
        }
    }

    private async Task TransitionSafelyAsync(Guid taskId, FileTaskStatus status, string stage, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await repository.TransitionAsync(taskId, status, stage, error, cancellationToken);
        }
        catch (InvalidFileTaskTransitionException exception)
        {
            // Гонка с завершением задачи: состояние уже финальное, отмена не нужна.
            logger?.LogDebug(exception, "Отмена задачи {TaskId} проиграла гонку с завершением", taskId);
        }
    }
}

/// <summary>
/// Thrown by executors after a user-initiated cancel has been fully
/// finalized (artifact cleaned up, items marked). The worker translates it
/// into the Cancelled status. Shutdown cancellation keeps using plain
/// <see cref="OperationCanceledException"/> and leaves the task interrupted.
/// </summary>
public sealed class FileTaskCancelledException() : Exception("Задача отменена пользователем.");
