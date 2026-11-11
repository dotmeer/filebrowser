using System.Diagnostics.Metrics;

namespace dotmeer.filebrowser.Observability;

/// <summary>
/// Метрики скачивания архивом (SPEC §12.2, §18). Завершение фиксируется
/// один раз на запрос: `completed`, `aborted` (обрыв соединения или
/// ошибка после начала ответа) или `rejected` (ошибка до начала ответа).
/// </summary>
public sealed class ArchiveMetrics
{
    private readonly Counter<long> _completedArchives = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.fs.archive.completed",
        unit: "{archive}",
        description: "Количество запросов скачивания архивом по результату.");

    private readonly Histogram<long> _archiveBytes = FileBrowserTelemetry.Meter.CreateHistogram<long>(
        "dotmeer.filebrowser.fs.archive.bytes",
        unit: "By",
        description: "Несжатый объём записей в сформированном архиве.");

    public void ArchiveCompleted(long bytes)
    {
        _completedArchives.Add(1, new KeyValuePair<string, object?>("archive.outcome", "completed"));
        _archiveBytes.Record(bytes);
    }

    public void ArchiveAborted() =>
        _completedArchives.Add(1, new KeyValuePair<string, object?>("archive.outcome", "aborted"));

    public void ArchiveRejected() =>
        _completedArchives.Add(1, new KeyValuePair<string, object?>("archive.outcome", "rejected"));
}
