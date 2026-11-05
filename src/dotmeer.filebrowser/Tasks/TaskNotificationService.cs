using System.Collections.Concurrent;

namespace dotmeer.filebrowser.Tasks;

public enum TaskChangeKind
{
    Created,
    Updated,
    HistoryCleared
}

public sealed record TaskChangeNotification(TaskChangeKind Kind, Guid? TaskId);

public sealed class TaskNotificationService(ILogger<TaskNotificationService>? logger = null)
{
    private readonly ConcurrentDictionary<long, Action<TaskChangeNotification>> _subscribers = new();
    private long _nextSubscriptionId;

    public IDisposable Subscribe(Action<TaskChangeNotification> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Interlocked.Increment(ref _nextSubscriptionId);
        _subscribers[id] = handler;
        return new Subscription(this, id);
    }

    public void Publish(TaskChangeNotification notification)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            try
            {
                subscriber(notification);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Подписчик уведомлений о задачах завершился ошибкой");
            }
        }
    }

    private void Unsubscribe(long id) => _subscribers.TryRemove(id, out _);

    private sealed class Subscription(TaskNotificationService owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(id);
            }
        }
    }
}
