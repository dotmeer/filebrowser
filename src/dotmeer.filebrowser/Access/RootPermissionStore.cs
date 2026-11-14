using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Access;

/// <summary>
/// Права обычных учётных записей по именам настроенных корней. Строка
/// отсутствует — доступа нет; имя нового корня не получает прав
/// автоматически. Ключ — неизменяемый ID учётной записи.
/// </summary>
public sealed class RootPermissionStore
{
    private readonly TaskDatabase _database;
    private readonly FileBrowserConfiguration _configuration;

    public RootPermissionStore(TaskDatabase database, FileBrowserConfiguration configuration)
    {
        _database = database;
        _configuration = configuration;
    }

    public async Task<IReadOnlyDictionary<string, RootAccess>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var permissions = new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase);
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_name, access
            FROM user_root_permissions
            WHERE user_id = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var access = RootAccessNames.TryParse(reader.GetString(1));
            if (access is not null)
            {
                permissions[reader.GetString(0)] = access.Value;
            }
        }

        return permissions;
    }

    /// <summary>
    /// Заменяет права для текущих корней одной транзакцией. Строки для
    /// временно отсутствующих в конфигурации корней сохраняются.
    /// </summary>
    public async Task SetAllAsync(
        Guid userId,
        IReadOnlyDictionary<string, RootAccess> rights,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rights);
        var normalized = new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rootName, access) in rights)
        {
            if (access == RootAccess.None)
            {
                continue;
            }

            if (!_configuration.RootsByName.TryGetValue(rootName, out var root))
            {
                throw new InvalidOperationException("Корень не найден в конфигурации.");
            }

            normalized[FileActor.NormalizeRoot(root.Name)] = access;
        }

        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            var configuredNames = _configuration.Roots.Select(root => FileActor.NormalizeRoot(root.Name)).ToArray();
            var parameters = configuredNames.Select((_, index) => $"$root{index}").ToArray();
            delete.CommandText = $"DELETE FROM user_root_permissions WHERE user_id = $userId AND root_name IN ({string.Join(", ", parameters)});";
            delete.Parameters.AddWithValue("$userId", userId.ToString("D"));
            for (var index = 0; index < configuredNames.Length; index++)
            {
                delete.Parameters.AddWithValue(parameters[index], configuredNames[index]);
            }
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (rootName, access) in normalized)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO user_root_permissions(user_id, root_name, access)
                VALUES ($userId, $rootName, $access);
                """;
            insert.Parameters.AddWithValue("$userId", userId.ToString("D"));
            insert.Parameters.AddWithValue("$rootName", rootName);
            insert.Parameters.AddWithValue("$access", RootAccessNames.ToStorageValue(access));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Устанавливает право на существующий настроенный корень. Имя
    /// сопоставляется без учёта регистра, как и в конфигурации; <see cref="RootAccess.None"/>
    /// удаляет запись.
    /// </summary>
    public async Task SetAsync(
        Guid userId,
        string rootName,
        RootAccess access,
        CancellationToken cancellationToken)
    {
        if (!_configuration.RootsByName.TryGetValue(rootName, out var root))
        {
            throw new InvalidOperationException("Корень не найден в конфигурации.");
        }

        var normalized = FileActor.NormalizeRoot(root.Name);

        await using var connection = _database.OpenConnection();
        if (access == RootAccess.None)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM user_root_permissions
                WHERE user_id = $userId AND root_name = $rootName;
                """;
            delete.Parameters.AddWithValue("$userId", userId.ToString("D"));
            delete.Parameters.AddWithValue("$rootName", normalized);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_root_permissions(user_id, root_name, access)
            VALUES ($userId, $rootName, $access)
            ON CONFLICT(user_id, root_name) DO UPDATE SET access = excluded.access;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$rootName", normalized);
        command.Parameters.AddWithValue("$access", RootAccessNames.ToStorageValue(access));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
