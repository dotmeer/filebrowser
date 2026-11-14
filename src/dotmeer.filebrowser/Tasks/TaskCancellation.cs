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
    private readonly Dictionary<Guid, Guid?> _sourceOwners = new();
    private readonly Dictionary<(Guid TaskId, Guid? RequesterUserId), PendingCancel> _pending = new();

    public CancellationTokenSource Register(Guid taskId) => Register(taskId, requesterUserId: null);

    /// <summary>
    /// Регистрирует источник отмены. Ожидающий запрос применяется, только
    /// если он сделан системным доступом (admin/ANONYMOUS) или тем же
    /// пользователем; чужая заявка не отменяет чужую загрузку.
    /// </summary>
    public CancellationTokenSource Register(Guid taskId, Guid? requesterUserId)
    {
        lock (_gate)
        {
            PruneExpired();
            if (!_sources.TryGetValue(taskId, out var source))
            {
                source = new CancellationTokenSource();
                _sources[taskId] = source;
                _sourceOwners[taskId] = requesterUserId;
                ApplyPending(taskId, requesterUserId, source);
            }

            return source;
        }
    }

    /// <summary>
    /// Регистрирует единственного владельца отмены для upload-корреляции.
    /// Параллельный запрос с тем же uploadId получает false и не должен
    /// освобождать чужой источник.
    /// </summary>
    public bool TryRegister(Guid taskId, [NotNullWhen(true)] out CancellationTokenSource? source) =>
        TryRegister(taskId, requesterUserId: null, out source);

    public bool TryRegister(Guid taskId, Guid? requesterUserId, [NotNullWhen(true)] out CancellationTokenSource? source)
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
            _sourceOwners[taskId] = requesterUserId;
            ApplyPending(taskId, requesterUserId, source);

            return true;
        }
    }

    public void Unregister(Guid taskId)
    {
        lock (_gate)
        {
            if (_sources.Remove(taskId, out var source))
            {
                _sourceOwners.Remove(taskId);
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
    /// <paramref name="requesterUserId"/> ограничивает ожидающую заявку:
    /// null — системный доступ, иначе только регистрация этого пользователя.
    /// </summary>
    public bool RequestCancel(Guid taskId) => RequestCancel(taskId, requesterUserId: null);

    public bool RequestCancel(Guid taskId, Guid? requesterUserId)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_sources.TryGetValue(taskId, out var source))
            {
                // Чужая заявка не отменяет уже зарегистрированную загрузку.
                if (!SourceBelongsTo(taskId, requesterUserId))
                {
                    return false;
                }

                source.Cancel();
                return true;
            }

            if (_pending.Count >= MaxPending)
            {
                var oldest = _pending.MinBy(entry => entry.Value.RequestedAtUtc);
                if (oldest.Value is not null) _pending.Remove(oldest.Key);
            }

            _pending[(taskId, requesterUserId)] = new PendingCancel(DateTimeOffset.UtcNow);
            return false;
        }
    }

    private bool SourceBelongsTo(Guid taskId, Guid? requesterUserId) =>
        requesterUserId is null
        || (_sourceOwners.TryGetValue(taskId, out var owner) && owner == requesterUserId);

    private void PruneExpired()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var threshold = DateTimeOffset.UtcNow - PendingLifetime;
        foreach (var entry in _pending.ToArray())
        {
            if (entry.Value.RequestedAtUtc <= threshold)
            {
                _pending.Remove(entry.Key);
            }
        }
    }

    private void ApplyPending(Guid taskId, Guid? owner, CancellationTokenSource source)
    {
        var keys = new List<(Guid, Guid?)> { (taskId, owner) };
        if (owner is not null) keys.Add((taskId, null));
        foreach (var key in keys)
        {
            if (_pending.Remove(key)) source.Cancel();
        }
    }

    private sealed record PendingCancel(DateTimeOffset RequestedAtUtc);
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
    public async Task CancelAsync(
        Guid taskId,
        TaskVisibility? visibility = null,
        CancellationToken cancellationToken = default)
    {
        var scope = visibility ?? TaskVisibility.Everything;
        using var operation = await operationGate.EnterAsync(taskId, cancellationToken);
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            // Upload may not have created its task row yet: remember the request
            // for the same requester only; a foreign pending cancel never
            // cancels another user's upload.
            if (CanRecordPending(scope))
            {
                registry.RequestCancel(taskId, scope.RequiredAuthorUserId);
            }

            return;
        }

        if (!IsVisible(scope, task))
        {
            // Чужой ID ведёт себя как отсутствующий: состояние не меняется.
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

    private static bool CanRecordPending(TaskVisibility visibility) =>
        visibility.AllAuthors
        || (visibility.AuthorUserId is { } userId && userId != Guid.Empty);

    private static bool IsVisible(TaskVisibility visibility, FileTask task) =>
        visibility.AllAuthors
        || (visibility.AuthorUserId is { } userId && userId != Guid.Empty && task.AuthorUserId == userId);

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
