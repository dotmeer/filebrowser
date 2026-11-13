using Microsoft.Data.Sqlite;

namespace dotmeer.filebrowser.Tasks;

public sealed class TaskDatabase
{
    private readonly string _connectionString;

    public TaskDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Не удалось определить каталог SQLite.");
        }

        Directory.CreateDirectory(directory);
        using var connection = OpenConnection();
        using (var integrityCheck = connection.CreateCommand())
        {
            integrityCheck.CommandText = "PRAGMA integrity_check;";
            var result = integrityCheck.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLite database integrity check failed.");
            }
        }

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """);

        if (!MigrationApplied(connection, transaction, 1))
        {
            Execute(connection, transaction, """
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
                """);
            using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("$version", 1);
            insertMigration.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            insertMigration.ExecuteNonQuery();
        }

        if (!MigrationApplied(connection, transaction, 2))
        {
            Execute(connection, transaction, """
                CREATE TABLE app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
            using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("$version", 2);
            insertMigration.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            insertMigration.ExecuteNonQuery();
        }

        if (!MigrationApplied(connection, transaction, 3))
        {
            // Регистрация временного артефакта переживает очистку истории:
            // удаление задачи обнуляет task_id, но не удаляет запись об остатке.
            Execute(connection, transaction, """
                CREATE TABLE temporary_artifacts_new (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NULL REFERENCES tasks(id) ON DELETE SET NULL,
                    root TEXT NOT NULL,
                    path TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    UNIQUE(root, path)
                );
                INSERT INTO temporary_artifacts_new(id, task_id, root, path, created_at_utc)
                SELECT id, task_id, root, path, created_at_utc FROM temporary_artifacts;
                DROP TABLE temporary_artifacts;
                ALTER TABLE temporary_artifacts_new RENAME TO temporary_artifacts;
                """);
            using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("$version", 3);
            insertMigration.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            insertMigration.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool MigrationApplied(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM schema_migrations WHERE version = $version;";
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
