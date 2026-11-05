using System.Collections.Concurrent;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskProgressService
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, ProgressWindow> _windows = new();

    public void Report(Guid taskId, long bytesDone) => Report(taskId, bytesDone, DateTimeOffset.UtcNow);

    public void Report(Guid taskId, long bytesDone, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd(taskId, static _ => new ProgressWindow());
        lock (window)
        {
            window.Samples.Enqueue(new ProgressSample(now, bytesDone));
            while (window.Samples.Count > 1 && now - window.Samples.Peek().Timestamp > Window)
            {
                window.Samples.Dequeue();
            }
        }
    }

    public double GetBytesPerSecond(Guid taskId) => GetBytesPerSecond(taskId, DateTimeOffset.UtcNow);

    public double GetBytesPerSecond(Guid taskId, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(taskId, out var window))
        {
            return 0;
        }

        lock (window)
        {
            while (window.Samples.Count > 1 && now - window.Samples.Peek().Timestamp > Window)
            {
                window.Samples.Dequeue();
            }

            if (window.Samples.Count < 2)
            {
                return 0;
            }

            var first = window.Samples.Peek();
            var last = window.Samples.Last();
            var elapsedSeconds = (last.Timestamp - first.Timestamp).TotalSeconds;
            return elapsedSeconds <= 0 ? 0 : Math.Max(0, (last.BytesDone - first.BytesDone) / elapsedSeconds);
        }
    }

    public void Clear(Guid taskId) => _windows.TryRemove(taskId, out _);

    private sealed class ProgressWindow
    {
        public Queue<ProgressSample> Samples { get; } = new();
    }

    private sealed record ProgressSample(DateTimeOffset Timestamp, long BytesDone);
}
