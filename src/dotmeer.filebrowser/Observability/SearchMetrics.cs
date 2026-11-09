using System.Diagnostics.Metrics;

namespace dotmeer.filebrowser.Observability;

public sealed class SearchMetrics
{
    private readonly Counter<long> _completedSearches = FileBrowserTelemetry.Meter.CreateCounter<long>(
        "dotmeer.filebrowser.fs.search.completed",
        unit: "{search}",
        description: "Количество завершённых поисковых запросов.");

    public void SearchCompleted(bool truncated) =>
        _completedSearches.Add(1, new KeyValuePair<string, object?>("search.truncated", truncated));
}
