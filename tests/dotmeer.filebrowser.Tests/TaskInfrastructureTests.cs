using System.Net;
using System.Net.Http.Json;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class TaskInfrastructureTests
{
    [Theory]
    [InlineData(FileTaskStatus.Queued, FileTaskStatus.Scanning, true)]
    [InlineData(FileTaskStatus.Scanning, FileTaskStatus.Running, true)]
    [InlineData(FileTaskStatus.Running, FileTaskStatus.Completed, true)]
    [InlineData(FileTaskStatus.Queued, FileTaskStatus.Completed, false)]
    [InlineData(FileTaskStatus.Completed, FileTaskStatus.Running, false)]
    public void StateTransitions_AllowOnlyLifecycleTransitions(FileTaskStatus from, FileTaskStatus to, bool expected) =>
        Assert.Equal(expected, FileTaskStateTransitions.IsAllowed(from, to));

    [Fact]
    public async Task Repository_PersistsTaskItemsAndTemporaryArtifactsAcrossMigration()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "tasks.db");
        var repository = CreateRepository(databasePath);
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy,
            [new FileRef("files", "a.txt"), new FileRef("files", "folder")],
            new FileRef("files", "target"));
        var artifact = await repository.RegisterTemporaryArtifactAsync(task.Id, new FileRef("files", ".filebrowser-copy"));

        var reopenedRepository = CreateRepository(databasePath);
        var restored = await reopenedRepository.GetAsync(task.Id);

        Assert.NotNull(restored);
        Assert.Equal(FileTaskStatus.Queued, restored.Status);
        Assert.Equal(["a.txt", "folder"], restored.Sources.Select(source => source.Path));
        Assert.Equal("target", restored.Destination.Path);
        Assert.True(artifact.Id > 0);
        using var connection = new TaskDatabase(databasePath).OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM temporary_artifacts WHERE task_id = $taskId;";
        command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Database_DoesNotAttemptToRecoverCorruptedFile()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "corrupted.db");
        File.WriteAllText(databasePath, "not a SQLite database");

        Assert.Throws<SqliteException>(() => new TaskDatabase(databasePath).Migrate());
    }

    [Fact]
    public async Task Repository_InterruptsAllActiveTasksAfterRestart()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "tasks.db");
        var firstRepository = CreateRepository(databasePath);
        var task = await firstRepository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", "a.txt")], new FileRef("files", "target"));
        await firstRepository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        await firstRepository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");

        var restartedRepository = CreateRepository(databasePath);
        var interrupted = await restartedRepository.InterruptActiveTasksAsync();
        var restored = await restartedRepository.GetAsync(task.Id);

        Assert.Equal(1, interrupted);
        Assert.NotNull(restored);
        Assert.Equal(FileTaskStatus.Interrupted, restored.Status);
        Assert.Equal("interrupted", restored.Stage);
        Assert.NotNull(restored.CompletedAtUtc);
    }

    [Fact]
    public async Task Dispatcher_DeletesPersistedTaskWhenBoundedQueueIsFull()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var dispatcher = new TaskDispatcher(repository, new TaskQueue());
        FileTask? last = null;
        for (var index = 0; index < 100; index++)
        {
            last = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", $"source-{index}")], new FileRef("files", "target"));
        }

        await Assert.ThrowsAsync<FileTaskQueueFullException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "overflow")], new FileRef("files", "target")));
        var page = await repository.GetPageAsync(0, 50);

        Assert.NotNull(last);
        Assert.Equal(50, page.Tasks.Count);
        Assert.DoesNotContain(page.Tasks, task => task.Sources.Single().Path == "overflow");
    }

    [Fact]
    public async Task Repository_HandlesConcurrentShortTransactions()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));

        var tasks = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("files", $"source-{index}")], new FileRef("files", "target")))));

        var page = await repository.GetPageAsync(0, 50);

        Assert.Equal(12, tasks.Length);
        Assert.Equal(12, page.Tasks.Count);
    }

    [Fact]
    public async Task HostedWorker_UsesTestExecutorAndCompletesTask()
    {
        using var workspace = new TestWorkspace();
        var executor = new RecordingExecutor();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var task = await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "source.txt")], new FileRef("files", "target"));

        await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var completed = await WaitForStatusAsync(repository, task.Id, FileTaskStatus.Completed);

        Assert.Equal(task.Id, executor.TaskId);
        Assert.NotNull(completed);
        Assert.Equal(FileTaskStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task HostedWorker_DequeuesTasksInFifoOrderWithSingleWorker()
    {
        using var workspace = new TestWorkspace();
        var executor = new RecordingExecutor(expectedCount: 2);
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), executor);
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();

        await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "first.txt")], new FileRef("files", "target"));
        await dispatcher.EnqueueAsync(FileTaskType.Copy, [new FileRef("files", "second.txt")], new FileRef("files", "target"));
        await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["first.txt", "second.txt"], executor.SourcePaths);
    }

    [Fact]
    public async Task TasksApi_ReturnsPagedPublicTasksAndRejectsLargeLimit()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1), new RecordingExecutor());
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var task = await repository.CreateQueuedAsync(FileTaskType.Move, [new FileRef("files", "a.txt")], new FileRef("files", "destination"));

        var response = await host.Client.GetAsync("/api/tasks?offset=0&limit=50");
        var body = await response.Content.ReadAsStringAsync();
        var detail = await host.Client.GetAsync($"/api/tasks/{task.Id:D}");
        var largeLimit = await host.Client.GetAsync("/api/tasks?limit=51");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(task.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a.txt", body);
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, largeLimit.StatusCode);
    }

    private static TaskRepository CreateRepository(string databasePath)
    {
        var database = new TaskDatabase(databasePath);
        database.Migrate();
        return new TaskRepository(database);
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

    private static TestHost CreateHost(string configPath, IFileTaskExecutor executor)
    {
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalDatabasePath = Environment.GetEnvironmentVariable("DatabasePath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", configPath);
        Environment.SetEnvironmentVariable("DatabasePath", Path.Combine(Path.GetDirectoryName(configPath)!, "app.db"));
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
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

    private sealed class RecordingExecutor(int expectedCount = 1) : IFileTaskExecutor
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TaskId { get; private set; }

        public List<string> SourcePaths { get; } = [];

        public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken)
        {
            TaskId = task.Id;
            SourcePaths.Add(task.Sources.Single().Path);
            if (SourcePaths.Count == expectedCount)
            {
                Executed.TrySetResult();
            }
            return Task.CompletedTask;
        }
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
