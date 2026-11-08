using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class TaskManagementTests
{
    [Fact]
    public async Task Repository_PublishesNotificationsAndUnsubscribeStopsThem()
    {
        using var workspace = new TestWorkspace();
        var notifications = new TaskNotificationService();
        var repository = CreateRepository(workspace, notifications);
        var received = new List<TaskChangeNotification>();
        var subscription = notifications.Subscribe(received.Add);

        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "first.txt")], new FileRef("files", "target"));
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");

        Assert.Contains(received, change => change.Kind == TaskChangeKind.Created && change.TaskId == task.Id);
        Assert.Contains(received, change => change.Kind == TaskChangeKind.Updated && change.TaskId == task.Id);

        subscription.Dispose();
        var countAfterUnsubscribe = received.Count;
        await repository.UpdateProgressAsync(task.Id, 10, 100, 1, 5);

        Assert.Equal(countAfterUnsubscribe, received.Count);
    }

    [Fact]
    public async Task HistoryCleanup_DeletesOnlyInactiveTasksWithTheirItemsAndArtifacts()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var completed = await CreateTerminalTaskAsync(repository, FileTaskStatus.Completed, "completed.txt");
        var failed = await CreateTerminalTaskAsync(repository, FileTaskStatus.Failed, "failed.txt");
        var interrupted = await CreateTerminalTaskAsync(repository, FileTaskStatus.Interrupted, "interrupted.txt");
        var active = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("files", "active.txt")], new FileRef("files", "target"));
        await repository.RegisterTemporaryArtifactAsync(completed.Id, new FileRef("files", ".filebrowser-cleanup"));

        var deleted = await repository.DeleteInactiveAsync();

        Assert.Equal(3, deleted);
        Assert.Null(await repository.GetAsync(completed.Id));
        Assert.Null(await repository.GetAsync(failed.Id));
        Assert.Null(await repository.GetAsync(interrupted.Id));
        Assert.NotNull(await repository.GetAsync(active.Id));
        Assert.Equal(1, CountRows(workspace, "task_items"));
        Assert.Equal(0, CountRows(workspace, "temporary_artifacts"));
    }

    [Fact]
    public async Task HistoryPage_ExcludesActiveTasksAndPaginates()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var completed = await CreateTerminalTaskAsync(repository, FileTaskStatus.Completed, "completed.txt");
        var failed = await CreateTerminalTaskAsync(repository, FileTaskStatus.Failed, "failed.txt");
        var interrupted = await CreateTerminalTaskAsync(repository, FileTaskStatus.Interrupted, "interrupted.txt");
        var active = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "active.txt")], new FileRef("files", "target"));

        var firstPage = await repository.GetHistoryPageAsync(0, 2);
        var secondPage = await repository.GetHistoryPageAsync(2, 2);
        var activeTasks = await repository.GetActiveAsync();

        Assert.Equal(2, firstPage.Tasks.Count);
        Assert.True(firstPage.HasMore);
        Assert.Single(secondPage.Tasks);
        Assert.False(secondPage.HasMore);
        Assert.DoesNotContain(firstPage.Tasks.Concat(secondPage.Tasks), task => task.Id == active.Id);
        Assert.Contains(firstPage.Tasks.Concat(secondPage.Tasks), task => task.Id == completed.Id);
        Assert.Contains(firstPage.Tasks.Concat(secondPage.Tasks), task => task.Id == failed.Id);
        Assert.Contains(firstPage.Tasks.Concat(secondPage.Tasks), task => task.Id == interrupted.Id);
        Assert.Equal(active.Id, Assert.Single(activeTasks).Id);
    }

    [Fact]
    public async Task HistoryEndpoint_RequiresAntiforgeryAndKeepsActiveTasks()
    {
        using var workspace = new TestWorkspace();
        var executor = new NeverCompletingExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var notifications = host.Services.GetRequiredService<TaskNotificationService>();
        var received = new List<TaskChangeNotification>();
        using var subscription = notifications.Subscribe(received.Add);
        var active = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "active.txt")], new FileRef("files", "target"));
        await WaitForStatusAsync(repository, active.Id, FileTaskStatus.Scanning);
        Assert.Contains(received, change => change.Kind == TaskChangeKind.Created && change.TaskId == active.Id);
        Assert.Contains(received, change => change.Kind == TaskChangeKind.Updated && change.TaskId == active.Id);
        await CreateTerminalTaskAsync(repository, FileTaskStatus.Completed, "completed.txt");
        await CreateTerminalTaskAsync(repository, FileTaskStatus.Failed, "failed.txt");

        using var rejected = await host.Client.DeleteAsync("/api/tasks/history");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.NotNull(await repository.GetAsync(active.Id));

        var token = await GetAntiforgeryTokenAsync(host.Client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/tasks/history");
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await host.Client.SendAsync(request);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("deletedCount").GetInt32());
        Assert.NotNull(await repository.GetAsync(active.Id));
        Assert.Single(await repository.GetActiveAsync());
    }

    [Fact]
    public async Task TwoWorkers_RunTwoTasksConcurrentlyAndDeferThirdInFifoOrder()
    {
        using var workspace = new TestWorkspace();
        var executor = new GatedExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 2), executor);
        Assert.Equal(2, host.Services.GetRequiredService<dotmeer.filebrowser.Configuration.FileBrowserConfiguration>().Workers);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();

        var first = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "first.txt")], new FileRef("files", "target"));
        var second = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "second.txt")], new FileRef("files", "target"));
        var third = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "third.txt")], new FileRef("files", "target"));

        await executor.WaitForStartedAsync(2);
        Assert.Equal(FileTaskStatus.Queued, (await repository.GetAsync(third.Id))!.Status);
        Assert.Equal(2, executor.StartedIds.Count);

        executor.Release();
        await executor.WaitForStartedAsync(1);
        await WaitForStatusAsync(repository, first.Id, FileTaskStatus.Completed);
        await WaitForStatusAsync(repository, second.Id, FileTaskStatus.Completed);
        await WaitForStatusAsync(repository, third.Id, FileTaskStatus.Completed);

        Assert.Equal(third.Id, executor.StartedIds[2]);
        Assert.Equal(new HashSet<Guid> { first.Id, second.Id }, executor.StartedIds.Take(2).ToHashSet());
    }

    [Fact]
    public void Presentation_ComputesProgressAndHandlesEmptyTrees()
    {
        var scanning = CreateTask(FileTaskStatus.Scanning, 0, null);
        var runningWithoutTotal = CreateTask(FileTaskStatus.Running, 0, 0);
        var running = CreateTask(FileTaskStatus.Running, 50, 200);
        var completed = CreateTask(FileTaskStatus.Completed, 200, 200);
        var overrun = CreateTask(FileTaskStatus.Running, 250, 200);

        Assert.Null(TaskPresentation.GetPercent(scanning));
        Assert.Null(TaskPresentation.GetPercent(runningWithoutTotal));
        Assert.Equal(25, TaskPresentation.GetPercent(running));
        Assert.Equal(100, TaskPresentation.GetPercent(completed));
        Assert.Equal(100, TaskPresentation.GetPercent(overrun));

        var originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            var presentation = CreatePresentation();
            Assert.Equal("0 B", presentation.FormatBytes(0));
            Assert.Equal("—", presentation.FormatBytes(null));
            Assert.Equal("0 B/s", presentation.FormatSpeed(0));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static TaskPresentation CreatePresentation()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Microsoft.Extensions.Options.Options.Create(new LocalizationOptions { ResourcesPath = "Localization" }),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        return new TaskPresentation(new StringLocalizer<SharedResources>(factory));
    }

    private static FileTask CreateTask(FileTaskStatus status, long bytesDone, long? bytesTotal) => new(
        Guid.NewGuid(),
        FileTaskType.Copy,
        [new FileRef("files", "first.txt")],
        [],
        new FileRef("files", "target"),
        status,
        status.ToString().ToLowerInvariant(),
        bytesDone,
        bytesTotal,
        0,
        null,
        null,
        DateTimeOffset.UtcNow,
        null,
        DateTimeOffset.UtcNow,
        null);

    private static async Task<FileTask> CreateTerminalTaskAsync(TaskRepository repository, FileTaskStatus status, string sourceName)
    {
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", sourceName)], new FileRef("files", "target"));
        switch (status)
        {
            case FileTaskStatus.Failed:
                await repository.TransitionAsync(task.Id, FileTaskStatus.Failed, "failed", "test failure");
                break;
            case FileTaskStatus.Interrupted:
                await repository.TransitionAsync(task.Id, FileTaskStatus.Interrupted, "interrupted");
                break;
            case FileTaskStatus.Completed:
                await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
                await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");
                await repository.TransitionAsync(task.Id, FileTaskStatus.Completed, "completed");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Нужен терминальный статус.");
        }

        return (await repository.GetAsync(task.Id))!;
    }

    private static TaskRepository CreateRepository(TestWorkspace workspace) =>
        CreateRepository(workspace, new TaskNotificationService());

    private static TaskRepository CreateRepository(TestWorkspace workspace, TaskNotificationService notifications)
    {
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        return new TaskRepository(database, notifications);
    }

    private static int CountRows(TestWorkspace workspace, string table)
    {
        using var connection = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db")).OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid taskId, FileTaskStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var task = await repository.GetAsync(taskId, timeout.Token);
            if (task?.Status == expected)
            {
                return task;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }

        throw new TimeoutException($"Задача {taskId} не перешла в состояние {expected}.");
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/browse?root=files");
        var match = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"(?<token>[^\\\"]+)\\\"");
        Assert.True(match.Success, "На странице не найден antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static TestHost CreateHost(string configPath, IFileTaskExecutor executor)
    {
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalDatabasePath = Environment.GetEnvironmentVariable("DatabasePath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", configPath);
        Environment.SetEnvironmentVariable("DatabasePath", Path.Combine(Path.GetDirectoryName(configPath)!, "app.db"));
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        var keyDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
                services.RemoveAll<IFileTaskExecutor>();
                services.AddSingleton(executor);
            }));
        try
        {
            var client = factory.CreateClient();
            return new TestHost(factory, client);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("DatabasePath", originalDatabasePath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    private sealed class NeverCompletingExecutor : IFileTaskExecutor
    {
        public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class GatedExecutor : IFileTaskExecutor
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly ConcurrentQueue<Guid> _startedIds = new();
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Guid> StartedIds => _startedIds.ToArray();

        public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken)
        {
            _startedIds.Enqueue(task.Id);
            _started.Release();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStartedAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                if (!await _started.WaitAsync(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Задача не была запущена вовремя.");
                }
            }
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class TestHost(WebApplicationFactory<Program> factory, HttpClient client) : IDisposable
    {
        public IServiceProvider Services => factory.Services;

        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }
    }
}
