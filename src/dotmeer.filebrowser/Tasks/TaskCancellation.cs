using System.Diagnostics.CodeAnalysis;

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// In-memory registry of per-task cancellation sources. A worker registers
/// a source before the task starts executing; <see cref="TaskCancellationService"/>
/// fires it on a user-initiated cancel. The registry is deliberately not
/// persisted: after a restart active tasks are interrupted anyway.
/// </summary>
/// <remarks>
/// Cancellation can arrive before the upload task row is created (the browser
/// knows the correlation id from the start). Such requests are remembered for
/// a short time so that the later registration observes them; entries expire
/// and are bounded to avoid an unbounded in-memory registry.
/// </remarks>
public sealed class TaskCancellationRegistry
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    private const int MaxPending = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _sources = new();
    private readonly Dictionary<Guid, DateTimeOffset> _pending = new();

    public CancellationTokenSource Register(Guid taskId)
    {
        lock (_gate)
        {
            PruneExpired();
            if (!_sources.TryGetValue(taskId, out var source))
            {
                source = new CancellationTokenSource();
                _sources[taskId] = source;
                if (_pending.Remove(taskId))
                {
                    source.Cancel();
                }
            }

            return source;
        }
    }

    /// <summary>
    /// Регистрирует единственного владельца отмены для upload-корреляции.
    /// Параллельный запрос с тем же uploadId получает false и не должен
    /// освобождать чужой источник.
    /// </summary>
    public bool TryRegister(Guid taskId, [NotNullWhen(true)] out CancellationTokenSource? source)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_sources.ContainsKey(taskId))
            {
                source = null;
                return false;
            }

            source = new CancellationTokenSource();
            _sources[taskId] = source;
            if (_pending.Remove(taskId))
            {
                source.Cancel();
            }

            return true;
        }
    }

    public void Unregister(Guid taskId)
    {
        lock (_gate)
        {
            if (_sources.Remove(taskId, out var source))
            {
                source.Dispose();
            }
        }
    }

    public bool TryCancel(Guid taskId)
    {
        lock (_gate)
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
    /// Cancels a registered task or remembers the request until the task is
    /// registered. Used when a cancel arrives before the task row exists.
    /// </summary>
    public bool RequestCancel(Guid taskId)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_sources.TryGetValue(taskId, out var source))
            {
                source.Cancel();
                return true;
            }

            if (_pending.Count >= MaxPending)
            {
                _pending.Clear();
            }

            _pending[taskId] = DateTimeOffset.UtcNow;
            return false;
        }
    }

    private void PruneExpired()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var threshold = DateTimeOffset.UtcNow - PendingLifetime;
        foreach (var entry in _pending.ToArray())
        {
            if (entry.Value <= threshold)
            {
                _pending.Remove(entry.Key);
            }
        }
    }
}

/// <summary>
/// User-initiated task cancellation. Idempotent by design: cancelling a
/// finished, missing or already cancelling task is a silent no-op.
/// </summary>
public sealed class TaskCancellationService(
    TaskRepository repository,
    TaskCancellationRegistry registry,
    TaskOperationGate operationGate,
    ILogger<TaskCancellationService>? logger = null)
{
    public async Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var operation = await operationGate.EnterAsync(taskId, cancellationToken);
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            // Upload may not have created its task row yet: remember the request.
            registry.RequestCancel(taskId);
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
