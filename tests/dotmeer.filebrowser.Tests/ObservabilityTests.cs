using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class ObservabilityTests
{
    [Fact]
    public void TaskMetrics_ReportsRejectedInterruptedAndProcessedWork()
    {
        var longMeasurements = new ConcurrentQueue<RecordedLongMeasurement>();
        var doubleMeasurements = new ConcurrentQueue<RecordedDoubleMeasurement>();
        using var listener = CreateMeterListener(longMeasurements, doubleMeasurements);
        var metrics = new TaskMetrics();

        metrics.TaskRejected(FileTaskType.Move);
        metrics.TasksInterrupted(3);
        metrics.TaskBytesProcessed(FileTaskType.Copy, 123);
        metrics.TaskItemsProcessed(FileTaskType.Copy, 2);

        Assert.Equal(1, Sum(longMeasurements, "dotmeer.filebrowser.tasks.rejected", ("task.type", "move"), ("error.reason", "queue_full")));
        Assert.Equal(3, Sum(longMeasurements, "dotmeer.filebrowser.tasks.finished", ("task.type", "unknown"), ("task.status", "interrupted")));
        Assert.Equal(123, Sum(longMeasurements, "dotmeer.filebrowser.tasks.bytes_processed", ("task.type", "copy")));
        Assert.Equal(2, Sum(longMeasurements, "dotmeer.filebrowser.tasks.items_processed", ("task.type", "copy")));
        Assert.DoesNotContain(doubleMeasurements, measurement => measurement.Instrument == "dotmeer.filebrowser.tasks.duration");
    }

    [Fact]
    public async Task HostedWorker_RecordsTaskSpanAndMetrics()
    {
        using var workspace = new TestWorkspace();
        var activityFinished = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activities = new ConcurrentQueue<Activity>();
        using var activityListener = CreateActivityListener(activityFinished, activities);
        var longMeasurements = new ConcurrentQueue<RecordedLongMeasurement>();
        var doubleMeasurements = new ConcurrentQueue<RecordedDoubleMeasurement>();
        using var meterListener = CreateMeterListener(longMeasurements, doubleMeasurements);

        var executor = new RecordingExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var task = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "source.txt")], new FileRef("files", "target"));

        await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var completed = await WaitForStatusAsync(repository, task.Id, FileTaskStatus.Completed);

        await activityFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var activity = Assert.Single(activities, item => item.OperationName == "filebrowser.task.execute");
        Assert.Equal(task.Id.ToString("D"), GetTag(activity, "filebrowser.task.id"));
        Assert.Equal("copy", GetTag(activity, "filebrowser.task.type"));
        Assert.Equal("completed", GetTag(activity, "filebrowser.task.status"));
        Assert.Equal("files", GetTag(activity, "filebrowser.task.destination_root"));
        Assert.Equal(1L, Convert.ToInt64(GetTagItem(activity, "filebrowser.task.source_count")));

        Assert.Equal(1, Sum(longMeasurements, "dotmeer.filebrowser.tasks.enqueued", ("task.type", "copy")));
        Assert.Equal(1, Sum(longMeasurements, "dotmeer.filebrowser.tasks.started", ("task.type", "copy")));
        Assert.Equal(1, Sum(longMeasurements, "dotmeer.filebrowser.tasks.finished", ("task.type", "copy"), ("task.status", "completed")));
        Assert.Contains(longMeasurements, measurement =>
            measurement.Instrument == "dotmeer.filebrowser.tasks.queue_depth"
            && measurement.Value == 1);
        Assert.Contains(longMeasurements, measurement =>
            measurement.Instrument == "dotmeer.filebrowser.tasks.queue_depth"
            && measurement.Value == -1);
        Assert.Contains(doubleMeasurements, measurement =>
            measurement.Instrument == "dotmeer.filebrowser.tasks.duration"
            && measurement.Tags.HasTag("task.type", "copy")
            && measurement.Tags.HasTag("task.status", "completed"));
        Assert.Equal(FileTaskStatus.Completed, completed.Status);
    }

    private static MeterListener CreateMeterListener(
        ConcurrentQueue<RecordedLongMeasurement> longMeasurements,
        ConcurrentQueue<RecordedDoubleMeasurement> doubleMeasurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == FileBrowserTelemetry.Meter.Name)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longMeasurements.Enqueue(new RecordedLongMeasurement(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubleMeasurements.Enqueue(new RecordedDoubleMeasurement(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private static ActivityListener CreateActivityListener(TaskCompletionSource<Activity> activityFinished, ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FileBrowserTelemetry.ActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "filebrowser.task.execute")
                {
                    activities.Enqueue(activity);
                    activityFinished.TrySetResult(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid taskId, FileTaskStatus expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var task = await repository.GetAsync(taskId, timeout.Token);
            if (task?.Status == expectedStatus)
            {
                return task;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }

        throw new TimeoutException($"Задача {taskId} не перешла в состояние {expectedStatus}.");
    }

    private static TestHostFactory.TestHost CreateHost(string configPath, IFileTaskExecutor executor) =>
        TestHostFactory.Create(
            configPath,
            configureServices: services =>
            {
                services.RemoveAll<IFileTaskExecutor>();
                services.AddSingleton<IFileTaskExecutor>(executor);
            });

    private static string? GetTag(Activity activity, string name) =>
        GetTagItem(activity, name) as string;

    private static object? GetTagItem(Activity activity, string name) =>
        activity.GetTagItem(name);

    private static long Sum(
        IEnumerable<RecordedLongMeasurement> measurements,
        string instrument,
        params (string Key, string Value)[] tags) =>
        measurements
            .Where(measurement => measurement.Instrument == instrument && measurement.Tags.HasAllTags(tags))
            .Sum(measurement => measurement.Value);

    private sealed class RecordingExecutor : IFileTaskExecutor
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
        {
            Executed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedLongMeasurement(
        string Instrument,
        long Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed record RecordedDoubleMeasurement(
        string Instrument,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}

internal static class ObservabilityTestTagExtensions
{
    public static bool HasTag(this KeyValuePair<string, object?>[] tags, string key, string value) =>
        tags.Any(tag => tag.Key == key && Equals(tag.Value, value));

    public static bool HasAllTags(this KeyValuePair<string, object?>[] tags, (string Key, string Value)[] expected) =>
        expected.All(item => tags.HasTag(item.Key, item.Value));
}
