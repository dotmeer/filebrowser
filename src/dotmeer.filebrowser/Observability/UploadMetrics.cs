using System.Diagnostics.Metrics;

namespace dotmeer.filebrowser.Observability;

/// <summary>
/// Метрики загрузки файлов (SPEC §11, §18). Итог фиксируется один раз на
/// запрос: <c>completed</c>, <c>failed</c>, <c>cancelled</c>, <c>aborted</c>
/// (обрыв соединения) или <c>rejected</c> (отказ до приёма тела).
/// </summary>
public sealed class UploadMetrics
{
    private readonly Counter<long> _completedUploads = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.fs.upload.completed",
        unit: "{upload}",
        description: "Количество запросов загрузки файла по результату.");

    private readonly Histogram<long> _uploadBytes = FileBrowserTelemetry.Meter.CreateHistogram<long>(
        "dotmeer.filebrowser.fs.upload.bytes",
        unit: "By",
        description: "Объём файла, принятого в успешной загрузке.");

    public void UploadCompleted(long bytes)
    {
        _completedUploads.Add(1, new KeyValuePair<string, object?>("upload.outcome", "completed"));
        _uploadBytes.Record(bytes);
    }

    public void UploadFailed() => Record("failed");

    public void UploadCancelled() => Record("cancelled");

    public void UploadAborted() => Record("aborted");

    public void UploadRejected() => Record("rejected");

    private void Record(string outcome) =>
        _completedUploads.Add(1, new KeyValuePair<string, object?>("upload.outcome", outcome));
}
