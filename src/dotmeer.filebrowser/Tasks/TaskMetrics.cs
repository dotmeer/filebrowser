using System.Diagnostics.Metrics;
using dotmeer.filebrowser.Observability;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskMetrics
{
    private readonly Counter<long> _enqueuedTasks = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.enqueued",
        unit: "{task}",
        description: "Количество задач, принятых в очередь.");

    private readonly Counter<long> _rejectedTasks = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.rejected",
        unit: "{task}",
        description: "Количество задач, отклонённых до исполнения.");

    private readonly Counter<long> _startedTasks = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.started",
        unit: "{task}",
        description: "Количество задач, начатых worker-ами.");

    private readonly Counter<long> _finishedTasks = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.finished",
        unit: "{task}",
        description: "Количество задач в терминальном состоянии.");

    private readonly UpDownCounter<long> _queueDepth = FileBrowserTelemetry.Meter.CreateUpDownCounter<long>(
        "dotmeer.filebrowser.tasks.queue_depth",
        unit: "{task}",
        description: "Текущее количество идентификаторов задач в bounded Channel.");

    private readonly Histogram<double> _taskDuration = FileBrowserTelemetry.Meter.CreateHistogram<double>(
        "dotmeer.filebrowser.tasks.duration",
        unit: "s",
        description: "Длительность исполнения задачи от выборки из очереди до терминального состояния.");

    private readonly Counter<long> _processedBytes = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.bytes_processed",
        unit: "By",
        description: "Фактически обработанные задачами байты.");

    private readonly Counter<long> _processedItems = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.tasks.items_processed",
        unit: "{item}",
        description: "Фактически обработанные задачами файловые объекты.");

    public void TaskEnqueued(FileTaskType type)
    {
        _enqueuedTasks.Add(1, TypeTag(type));
        _queueDepth.Add(1);
    }

    public void TaskDequeued() => _queueDepth.Add(-1);

    public void TaskRejected(FileTaskType type)
    {
        _rejectedTasks.Add(1, TypeTag(type), new KeyValuePair<string, object?>("error.reason", "queue_full"));
    }

    public void TaskStarted(FileTaskType type) => _startedTasks.Add(1, TypeTag(type));

    public void TaskCompleted(FileTaskType type, TimeSpan duration) =>
        TaskFinished(type, "completed", duration);

    public void TaskFailed(FileTaskType? type, TimeSpan duration) =>
        TaskFinished(type, "failed", duration);

    public void TaskCancelled(FileTaskType? type, TimeSpan duration) =>
        TaskFinished(type, "cancelled", duration);

    public void TasksInterrupted(int count)
    {
        if (count > 0)
        {
            _finishedTasks.Add(count, TypeTag(null), StatusTag("interrupted"));
        }
    }

    public void TaskBytesProcessed(FileTaskType type, long bytes)
    {
        if (bytes > 0)
        {
            _processedBytes.Add(bytes, TypeTag(type));
        }
    }

    public void TaskItemsProcessed(FileTaskType type, long items)
    {
        if (items > 0)
        {
            _processedItems.Add(items, TypeTag(type));
        }
    }

    private void TaskFinished(FileTaskType? type, string status, TimeSpan duration)
    {
        _finishedTasks.Add(1, TypeTag(type), StatusTag(status));
        _taskDuration.Record(Math.Max(0, duration.TotalSeconds), TypeTag(type), StatusTag(status));
    }

    private static KeyValuePair<string, object?> TypeTag(FileTaskType? type) =>
        new("task.type", type?.ToString().ToLowerInvariant() ?? "unknown");

    private static KeyValuePair<string, object?> StatusTag(string status) =>
        new("task.status", status);
}
