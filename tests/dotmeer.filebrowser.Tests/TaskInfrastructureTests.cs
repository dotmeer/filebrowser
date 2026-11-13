using System.Net;
using System.Net.Http.Json;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
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
    public void SqliteErrors_DistinguishesUniqueFromOtherConstraints()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "constraints.db");
        new TaskDatabase(databasePath).Migrate();
        using var connection = new TaskDatabase(databasePath).OpenConnection();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE unique_probe(value TEXT UNIQUE); INSERT INTO unique_probe(value) VALUES ('a');";
            create.ExecuteNonQuery();
        }

        SqliteException unique = Assert.Throws<SqliteException>(() =>
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO unique_probe(value) VALUES ('a');";
            insert.ExecuteNonQuery();
        });
        Assert.True(SqliteErrors.IsUniqueConstraint(unique));

        using (var createPrimaryKey = connection.CreateCommand())
        {
            createPrimaryKey.CommandText = "CREATE TABLE pk_probe(id TEXT PRIMARY KEY); INSERT INTO pk_probe(id) VALUES ('a');";
            createPrimaryKey.ExecuteNonQuery();
        }

        SqliteException primaryKey = Assert.Throws<SqliteException>(() =>
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO pk_probe(id) VALUES ('a');";
            insert.ExecuteNonQuery();
        });
        Assert.True(SqliteErrors.IsPrimaryKeyConstraint(primaryKey));
        Assert.False(SqliteErrors.IsUniqueConstraint(primaryKey));

        SqliteException foreignKey = Assert.Throws<SqliteException>(() =>
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO task_items(task_id, source_root, source_path, status)
                VALUES ('00000000-0000-0000-0000-000000000001', 'files', 'x', 'pending');
                """;
            insert.ExecuteNonQuery();
        });
        Assert.False(SqliteErrors.IsUniqueConstraint(foreignKey));
    }

    [Fact]
    public async Task Repository_RejectsConcurrentDuplicateTaskId()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var taskId = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await repository.CreateActiveAsync(
                        FileTaskType.Upload,
                        [new FileRef("files", "same.txt")],
                        new FileRef("files", string.Empty),
                        FileTaskStatus.Scanning,
                        "scanning",
                        1,
                        1,
                        CancellationToken.None,
                        taskId);
                    return true;
                }
                catch (FileTaskIdConflictException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(success => success));
        Assert.NotNull(await repository.GetAsync(taskId));
    }

    [Fact]
    public async Task Executor_RejectsUploadTypeInsteadOfQueueingIt()
    {
        var executor = new FileTaskExecutor(null!, null!, null!);
        var now = DateTimeOffset.UtcNow;
        var task = new FileTask(
            Guid.NewGuid(),
            FileTaskType.Upload,
            [],
            [],
            new FileRef("files", string.Empty),
            FileTaskStatus.Scanning,
            "scanning",
            0,
            null,
            0,
            null,
            null,
            now,
            now,
            now,
            null);

        await Assert.ThrowsAsync<UnsupportedFileTaskTypeException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None));
    }

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
    public async Task Migration3_PreservesExistingTasksAndArtifactsAndIsIdempotent()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "tasks.db");
        var taskId = CreateVersion2Database(databasePath);

        new TaskDatabase(databasePath).Migrate();
        new TaskDatabase(databasePath).Migrate();

        var repository = new TaskRepository(new TaskDatabase(databasePath));
        var restored = await repository.GetAsync(taskId);
        Assert.NotNull(restored);
        Assert.Equal(FileTaskStatus.Completed, restored.Status);
        var artifact = Assert.Single(await repository.GetTemporaryArtifactsAsync());
        Assert.Equal(taskId, artifact.TaskId);
        Assert.Equal(new FileRef("files", ".filebrowser-legacy"), artifact.File);

        using (var connection = new TaskDatabase(databasePath).OpenConnection())
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            using var reader = check.ExecuteReader();
            Assert.False(reader.Read(), "PRAGMA foreign_key_check вернул строки.");
        }

        Assert.Equal(1, await repository.DeleteInactiveAsync());
        Assert.Null(await repository.GetAsync(taskId));
        artifact = Assert.Single(await repository.GetTemporaryArtifactsAsync());
        Assert.Null(artifact.TaskId);
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

    private static TaskRepository CreateRepository(string databasePath)
    {
        var database = new TaskDatabase(databasePath);
        database.Migrate();
        return new TaskRepository(database);
    }

    private static Guid CreateVersion2Database(string databasePath)
    {
        var taskId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            CREATE TABLE tasks (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                status TEXT NOT NULL,
                stage TEXT NOT NULL,
                destination_root TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                bytes_done INTEGER NOT NULL DEFAULT 0,
                bytes_total INTEGER NULL,
                items_done INTEGER NOT NULL DEFAULT 0,
                items_total INTEGER NULL,
                error TEXT NULL,
                created_at_utc TEXT NOT NULL,
                started_at_utc TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL
            );
            CREATE INDEX ix_tasks_created_at ON tasks(created_at_utc, id);
            CREATE INDEX ix_tasks_status ON tasks(status);
            CREATE TABLE task_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                source_root TEXT NOT NULL,
                source_path TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT NULL,
                items_done INTEGER NOT NULL DEFAULT 0,
                items_total INTEGER NULL,
                UNIQUE(task_id, source_root, source_path)
            );
            CREATE TABLE temporary_artifacts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                root TEXT NOT NULL,
                path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(root, path)
            );
            CREATE TABLE app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO schema_migrations(version, applied_at_utc) VALUES (1, $now), (2, $now);
            INSERT INTO tasks(id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc)
            VALUES ($taskId, 'copy', 'completed', 'completed', 'files', 'target', 0, NULL, 1, 1, NULL, $now, $now, $now, $now);
            INSERT INTO task_items(task_id, source_root, source_path, status, error, items_done, items_total)
            VALUES ($taskId, 'files', 'legacy.txt', 'completed', NULL, 1, 1);
            INSERT INTO temporary_artifacts(task_id, root, path, created_at_utc)
            VALUES ($taskId, 'files', '.filebrowser-legacy', $now);
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return taskId;
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
                services.AddSingleton(executor);
            });

    private sealed class RecordingExecutor(int expectedCount = 1) : IFileTaskExecutor
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TaskId { get; private set; }

        public List<string> SourcePaths { get; } = [];

        public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
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
}
