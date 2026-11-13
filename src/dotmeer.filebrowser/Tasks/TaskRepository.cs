using System.Globalization;
using dotmeer.filebrowser.FileSystem;
using Microsoft.Data.Sqlite;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskRepository(TaskDatabase database, TaskNotificationService? notifications = null)
{
    public Task<FileTask> CreateQueuedAsync(FileTaskType type, IReadOnlyList<FileRef> sources, FileRef destination, CancellationToken cancellationToken = default) =>
        CreateAsync(type, sources, destination, FileTaskStatus.Queued, "queued", null, null, cancellationToken, null);

    /// <summary>
    /// Creates a task that is already active and is not placed into the task
    /// queue. Used by upload: its lifecycle is driven by the HTTP request that
    /// carries the bytes, so a worker must never pick it up (SPEC §13.10).
    /// </summary>
    public Task<FileTask> CreateActiveAsync(
        FileTaskType type,
        IReadOnlyList<FileRef> sources,
        FileRef destination,
        FileTaskStatus status,
        string stage,
        long? bytesTotal,
        long? itemsTotal,
        CancellationToken cancellationToken = default,
        Guid? taskId = null)
    {
        if (!FileTaskStateTransitions.IsActive(status) || status == FileTaskStatus.Queued)
        {
            throw new ArgumentException("Активная задача должна быть создана в состоянии scanning/running/cancelling.", nameof(status));
        }

        return CreateAsync(type, sources, destination, status, stage, bytesTotal, itemsTotal, cancellationToken, taskId);
    }

    private Task<FileTask> CreateAsync(
        FileTaskType type,
        IReadOnlyList<FileRef> sources,
        FileRef destination,
        FileTaskStatus status,
        string stage,
        long? bytesTotal,
        long? itemsTotal,
        CancellationToken cancellationToken,
        Guid? taskId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        if (sources.Count == 0)
        {
            throw new ArgumentException("Для задачи требуется хотя бы один источник.", nameof(sources));
        }

        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор задачи не может быть пустым.", nameof(taskId));
        }

        var now = DateTimeOffset.UtcNow;
        var startedAt = status == FileTaskStatus.Queued ? (DateTimeOffset?)null : now;
        var task = new FileTask(
            taskId ?? Guid.NewGuid(),
            type,
            sources.ToArray(),
            [],
            destination,
            status,
            stage,
            0,
            bytesTotal,
            0,
            itemsTotal,
            null,
            now,
            startedAt,
            now,
            null);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tasks(id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc)
                VALUES ($id, $type, $status, $stage, $destinationRoot, $destinationPath, 0, $bytesTotal, 0, $itemsTotal, NULL, $createdAtUtc, $startedAtUtc, $updatedAtUtc, NULL);
                """;
            command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            command.Parameters.AddWithValue("$type", ToDatabaseValue(task.Type));
            command.Parameters.AddWithValue("$status", ToDatabaseValue(task.Status));
            command.Parameters.AddWithValue("$stage", task.Stage);
            command.Parameters.AddWithValue("$destinationRoot", destination.Root);
            command.Parameters.AddWithValue("$destinationPath", destination.Path);
            command.Parameters.AddWithValue("$bytesTotal", (object?)bytesTotal ?? DBNull.Value);
            command.Parameters.AddWithValue("$itemsTotal", (object?)itemsTotal ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAtUtc", ToDatabaseValue(now));
            command.Parameters.AddWithValue("$startedAtUtc", (object?)startedAt is null ? DBNull.Value : ToDatabaseValue(startedAt.Value));
            command.Parameters.AddWithValue("$updatedAtUtc", ToDatabaseValue(now));
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (SqliteErrors.IsPrimaryKeyConstraint(exception))
            {
                throw new FileTaskIdConflictException();
            }
        }

        foreach (var source in sources)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO task_items(task_id, source_root, source_path, status, error, items_done, items_total)
                VALUES ($taskId, $sourceRoot, $sourcePath, 'pending', NULL, 0, $itemsTotal);
                """;
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            command.Parameters.AddWithValue("$sourceRoot", source.Root);
            command.Parameters.AddWithValue("$sourcePath", source.Path);
            command.Parameters.AddWithValue("$itemsTotal", (object?)itemsTotal ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        Notify(TaskChangeKind.Created, task.Id);
        return Task.FromResult(task);
    }

    public Task<FileTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        return Task.FromResult(GetTask(connection, id));
    }

    public Task<TaskPage> GetPageAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Пагинация задач недопустима.");
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc
            FROM tasks
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $take OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$take", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<TaskRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(ReadTaskRow(reader));
            }
        }

        var tasks = BuildTasks(connection, rows).ToList();

        var hasMore = tasks.Count > limit;
        if (hasMore)
        {
            tasks.RemoveAt(tasks.Count - 1);
        }

        return Task.FromResult(new TaskPage(tasks, offset, limit, hasMore));
    }

    public Task<IReadOnlyList<FileTask>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc
            FROM tasks
            WHERE status IN ('queued', 'scanning', 'running', 'cancelling')
            ORDER BY created_at_utc ASC, id ASC;
            """;
        var rows = new List<TaskRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(ReadTaskRow(reader));
            }
        }

        var tasks = BuildTasks(connection, rows);
        return Task.FromResult<IReadOnlyList<FileTask>>(tasks);
    }

    public Task<TaskPage> GetHistoryPageAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Пагинация задач недопустима.");
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc
            FROM tasks
            WHERE status IN ('completed', 'failed', 'cancelled', 'interrupted')
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $take OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$take", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<TaskRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(ReadTaskRow(reader));
            }
        }

        var tasks = BuildTasks(connection, rows).ToList();
        var hasMore = tasks.Count > limit;
        if (hasMore)
        {
            tasks.RemoveAt(tasks.Count - 1);
        }

        return Task.FromResult(new TaskPage(tasks, offset, limit, hasMore));
    }

    public Task TransitionAsync(Guid id, FileTaskStatus nextStatus, string stage, string? error = null, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = GetTask(connection, id, transaction) ?? throw new KeyNotFoundException("Задача не найдена.");
        if (!FileTaskStateTransitions.IsAllowed(current.Status, nextStatus))
        {
            throw new InvalidFileTaskTransitionException(current.Status, nextStatus);
        }

        var now = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks
            SET status = $nextStatus,
                stage = $stage,
                error = $error,
                started_at_utc = CASE WHEN started_at_utc IS NULL AND $nextStatus IN ('scanning', 'running') THEN $updatedAtUtc ELSE started_at_utc END,
                updated_at_utc = $updatedAtUtc,
                completed_at_utc = CASE WHEN $nextStatus IN ('completed', 'failed', 'cancelled', 'interrupted') THEN $updatedAtUtc ELSE NULL END
            WHERE id = $id AND status = $currentStatus;
            """;
        command.Parameters.AddWithValue("$nextStatus", ToDatabaseValue(nextStatus));
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", ToDatabaseValue(now));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$currentStatus", ToDatabaseValue(current.Status));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Состояние задачи изменилось конкурентно.");
        }

        transaction.Commit();
        Notify(TaskChangeKind.Updated, id);
        return Task.CompletedTask;
    }

    public Task<UploadFinalizeOutcome> FinalizePublishedUploadAsync(
        Guid id,
        FileRef source,
        long artifactId,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = GetTask(connection, id, transaction);
        if (current is null || current.Status is not (FileTaskStatus.Running or FileTaskStatus.Cancelling))
        {
            transaction.Rollback();
            return Task.FromResult(current?.Status switch
            {
                FileTaskStatus.Completed => UploadFinalizeOutcome.Completed,
                FileTaskStatus.Cancelled => UploadFinalizeOutcome.Cancelled,
                _ => UploadFinalizeOutcome.Unavailable
            });
        }

        using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText = """
                UPDATE task_items SET status = 'completed', error = NULL, items_done = 1, items_total = 1
                WHERE task_id = $taskId AND source_root = $root AND source_path = $path;
                """;
            item.Parameters.AddWithValue("$taskId", id.ToString("D"));
            item.Parameters.AddWithValue("$root", source.Root);
            item.Parameters.AddWithValue("$path", source.Path);
            if (item.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Элемент опубликованной загрузки не найден.");
            }
        }

        var nextStatus = current.Status == FileTaskStatus.Cancelling
            ? FileTaskStatus.Cancelled
            : FileTaskStatus.Completed;
        using (var task = connection.CreateCommand())
        {
            task.Transaction = transaction;
            task.CommandText = """
                UPDATE tasks SET status = $status, stage = $stage, error = $error,
                    items_done = 1, items_total = 1, updated_at_utc = $updatedAtUtc,
                    completed_at_utc = $updatedAtUtc
                WHERE id = $id AND status = $currentStatus;
                """;
            task.Parameters.AddWithValue("$status", ToDatabaseValue(nextStatus));
            task.Parameters.AddWithValue("$stage", nextStatus == FileTaskStatus.Completed ? "completed" : "cancelled");
            task.Parameters.AddWithValue("$error", (object?)(nextStatus == FileTaskStatus.Cancelled ? FileTaskErrorCodes.CancelledByUser : null) ?? DBNull.Value);
            task.Parameters.AddWithValue("$updatedAtUtc", ToDatabaseValue(DateTimeOffset.UtcNow));
            task.Parameters.AddWithValue("$id", id.ToString("D"));
            task.Parameters.AddWithValue("$currentStatus", ToDatabaseValue(current.Status));
            if (task.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return Task.FromResult(UploadFinalizeOutcome.Unavailable);
            }
        }

        using (var artifact = connection.CreateCommand())
        {
            artifact.Transaction = transaction;
            artifact.CommandText = "DELETE FROM temporary_artifacts WHERE id = $id;";
            artifact.Parameters.AddWithValue("$id", artifactId);
            artifact.ExecuteNonQuery();
        }

        transaction.Commit();
        Notify(TaskChangeKind.Updated, id);
        return Task.FromResult(nextStatus == FileTaskStatus.Completed
            ? UploadFinalizeOutcome.Completed
            : UploadFinalizeOutcome.Cancelled);
    }

    public Task<int> InterruptActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks
            SET status = 'interrupted',
                stage = 'interrupted',
                error = 'task_interrupted_restart',
                updated_at_utc = $updatedAtUtc,
                completed_at_utc = $updatedAtUtc
            WHERE status IN ('queued', 'scanning', 'running', 'cancelling');
            """;
        command.Parameters.AddWithValue("$updatedAtUtc", ToDatabaseValue(now));
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        if (changed > 0)
        {
            Notify(TaskChangeKind.Updated, null);
        }
        return Task.FromResult(changed);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE id = $id AND status = 'queued';";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var changed = command.ExecuteNonQuery();
        if (changed > 0)
        {
            Notify(TaskChangeKind.Updated, id);
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteInactiveAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tasks WHERE status IN ('completed', 'failed', 'cancelled', 'interrupted');";
        var deleted = command.ExecuteNonQuery();
        transaction.Commit();
        if (deleted > 0)
        {
            Notify(TaskChangeKind.HistoryCleared, null);
        }
        return Task.FromResult(deleted);
    }

    public Task CancelPendingItemsAsync(Guid taskId, string error, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE task_items
            SET status = 'not_processed',
                error = $error
            WHERE task_id = $taskId AND status = 'pending';
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        var changed = command.ExecuteNonQuery();
        if (changed > 0)
        {
            Notify(TaskChangeKind.Updated, taskId);
        }

        return Task.CompletedTask;
    }

    public Task<TemporaryArtifact> RegisterTemporaryArtifactAsync(Guid taskId, FileRef file, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO temporary_artifacts(task_id, root, path, created_at_utc)
            VALUES ($taskId, $root, $path, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$root", file.Root);
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$createdAtUtc", ToDatabaseValue(now));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return Task.FromResult(new TemporaryArtifact(id, taskId, file, now));
    }

    public Task RemoveTemporaryArtifactAsync(long artifactId, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM temporary_artifacts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", artifactId);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TemporaryArtifact>> GetTemporaryArtifactsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, task_id, root, path, created_at_utc FROM temporary_artifacts ORDER BY id;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<TemporaryArtifact>();
        while (reader.Read())
        {
            artifacts.Add(new TemporaryArtifact(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                new FileRef(reader.GetString(2), reader.GetString(3)),
                ParseDate(reader.GetString(4))));
        }
        return Task.FromResult<IReadOnlyList<TemporaryArtifact>>(artifacts);
    }

    public Task UpdateProgressAsync(Guid id, long bytesDone, long? bytesTotal, long itemsDone, long? itemsTotal, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET bytes_done = $bytesDone,
                bytes_total = $bytesTotal,
                items_done = $itemsDone,
                items_total = $itemsTotal,
                updated_at_utc = $updatedAtUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$bytesDone", bytesDone);
        command.Parameters.AddWithValue("$bytesTotal", (object?)bytesTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemsDone", itemsDone);
        command.Parameters.AddWithValue("$itemsTotal", (object?)itemsTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", ToDatabaseValue(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
        Notify(TaskChangeKind.Progress, id);
        return Task.CompletedTask;
    }

    public Task UpdateTaskItemAsync(Guid taskId, FileRef source, string status, string? error, long itemsDone, long? itemsTotal, CancellationToken cancellationToken = default)
    {        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE task_items
            SET status = $status,
                error = $error,
                items_done = $itemsDone,
                items_total = $itemsTotal
            WHERE task_id = $taskId AND source_root = $sourceRoot AND source_path = $sourcePath;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemsDone", itemsDone);
        command.Parameters.AddWithValue("$itemsTotal", (object?)itemsTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$sourceRoot", source.Root);
        command.Parameters.AddWithValue("$sourcePath", source.Path);
        command.ExecuteNonQuery();
        Notify(TaskChangeKind.Updated, taskId);
        return Task.CompletedTask;
    }

    private void Notify(TaskChangeKind kind, Guid? taskId) =>
        notifications?.Publish(new TaskChangeNotification(kind, taskId));

    private static FileTask? GetTask(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, type, status, stage, destination_root, destination_path, bytes_done, bytes_total, items_done, items_total, error, created_at_utc, started_at_utc, updated_at_utc, completed_at_utc
            FROM tasks WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        TaskRow? row;
        using (var reader = command.ExecuteReader())
        {
            row = reader.Read() ? ReadTaskRow(reader) : null;
        }
        return row is null ? null : ToFileTask(row, GetSources(connection, id, transaction), GetItems(connection, id, transaction));
    }

    private static IReadOnlyList<FileRef> GetSources(SqliteConnection connection, Guid taskId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source_root, source_path FROM task_items WHERE task_id = $taskId ORDER BY id;";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        using var reader = command.ExecuteReader();
        var sources = new List<FileRef>();
        while (reader.Read())
        {
            sources.Add(new FileRef(reader.GetString(0), reader.GetString(1)));
        }
        return sources;
    }

    private static IReadOnlyList<TaskItem> GetItems(SqliteConnection connection, Guid taskId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source_root, source_path, status, error, items_done, items_total
            FROM task_items WHERE task_id = $taskId ORDER BY id;
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        using var reader = command.ExecuteReader();
        var items = new List<TaskItem>();
        while (reader.Read())
        {
            items.Add(new TaskItem(
                reader.GetInt64(0),
                taskId,
                new FileRef(reader.GetString(1), reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }
        return items;
    }

    private static IReadOnlyList<FileTask> BuildTasks(SqliteConnection connection, IReadOnlyList<TaskRow> rows)
    {
        var itemsByTask = GetItemsBatch(connection, rows.Select(row => row.Id).ToArray());
        return rows
            .Select(row =>
            {
                var items = itemsByTask.TryGetValue(row.Id, out var stored) ? stored : [];
                var sources = items.Select(item => item.Source).ToArray();
                return ToFileTask(row, sources, items);
            })
            .ToArray();
    }

    private static Dictionary<Guid, IReadOnlyList<TaskItem>> GetItemsBatch(
        SqliteConnection connection,
        IReadOnlyList<Guid> taskIds)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TaskItem>>();
        if (taskIds.Count == 0)
        {
            return result;
        }

        using var command = connection.CreateCommand();
        var parameterNames = new string[taskIds.Count];
        for (var index = 0; index < taskIds.Count; index++)
        {
            parameterNames[index] = $"$taskId{index}";
            command.Parameters.AddWithValue(parameterNames[index], taskIds[index].ToString("D"));
        }

        command.CommandText = $"""
            SELECT id, task_id, source_root, source_path, status, error, items_done, items_total
            FROM task_items
            WHERE task_id IN ({string.Join(", ", parameterNames)})
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var grouped = new Dictionary<Guid, List<TaskItem>>();
        while (reader.Read())
        {
            var taskId = Guid.Parse(reader.GetString(1));
            if (!grouped.TryGetValue(taskId, out var items))
            {
                items = [];
                grouped[taskId] = items;
            }

            items.Add(new TaskItem(
                reader.GetInt64(0),
                taskId,
                new FileRef(reader.GetString(2), reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static TaskRow ReadTaskRow(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Enum.Parse<FileTaskType>(reader.GetString(1), ignoreCase: true),
        new FileRef(reader.GetString(4), reader.GetString(5)),
        Enum.Parse<FileTaskStatus>(reader.GetString(2), ignoreCase: true),
        reader.GetString(3),
        reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParseDate(reader.GetString(11)),
        reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)),
        ParseDate(reader.GetString(13)),
        reader.IsDBNull(14) ? null : ParseDate(reader.GetString(14)));

    private static FileTask ToFileTask(TaskRow row, IReadOnlyList<FileRef> sources, IReadOnlyList<TaskItem> items) => new(
        row.Id,
        row.Type,
        sources,
        items,
        row.Destination,
        row.Status,
        row.Stage,
        row.BytesDone,
        row.BytesTotal,
        row.ItemsDone,
        row.ItemsTotal,
        row.Error,
        row.CreatedAtUtc,
        row.StartedAtUtc,
        row.UpdatedAtUtc,
        row.CompletedAtUtc);

    private static string ToDatabaseValue(FileTaskType type) => type.ToString().ToLowerInvariant();

    private static string ToDatabaseValue(FileTaskStatus status) => status.ToString().ToLowerInvariant();

    private static string ToDatabaseValue(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record TaskRow(
        Guid Id,
        FileTaskType Type,
        FileRef Destination,
        FileTaskStatus Status,
        string Stage,
        long BytesDone,
        long? BytesTotal,
        long ItemsDone,
        long? ItemsTotal,
        string? Error,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? CompletedAtUtc);
}
