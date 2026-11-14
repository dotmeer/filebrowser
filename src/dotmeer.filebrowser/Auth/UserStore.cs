using System.Text.RegularExpressions;
using dotmeer.filebrowser.Access;
using Microsoft.Data.Sqlite;

namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Учётная запись. <see cref="Id"/> — неизменяемый внутренний идентификатор;
/// права и будущие задачи ссылаются на него, а не на логин.
/// </summary>
public sealed record UserRecord(
    Guid Id,
    string Login,
    string LoginNormalized,
    string PasswordHash,
    bool IsAdmin,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SessionUser(
    Guid SessionId,
    Guid UserId,
    string Login,
    bool IsAdmin,
    bool MustChangePassword,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Хранение учётных записей и серверных сессий в SQLite. Каждая операция
/// использует отдельное короткое соединение; источник истины для проверки
/// полномочий — текущие строки БД, а не данные cookie.
/// </summary>
public sealed class UserStore
{
    public const string AdminLogin = "admin";
    public const string AdminLoginNormalized = "ADMIN";

    private readonly Tasks.TaskDatabase _database;
    private readonly PasswordService _passwords;
    private readonly TimeProvider _time;

    public UserStore(Tasks.TaskDatabase database, PasswordService passwords, TimeProvider time)
    {
        _database = database;
        _passwords = passwords;
        _time = time;
    }

    public Task<bool> AdminExistsAsync(CancellationToken cancellationToken) =>
        ExistsAsync("SELECT 1 FROM users WHERE is_admin = 1 LIMIT 1;", cancellationToken);

    public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) =>
        ExistsAsync("SELECT 1 FROM users LIMIT 1;", cancellationToken);

    public async Task<UserRecord?> FindByLoginAsync(string login, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLogin(login);
        if (normalized.Length == 0)
        {
            return null;
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, login, login_normalized, password_hash, is_admin, must_change_password,
                   created_at_utc, updated_at_utc
            FROM users
            WHERE login_normalized = $login;
            """;
        command.Parameters.AddWithValue("$login", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<UserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, login, login_normalized, password_hash, is_admin, must_change_password,
                   created_at_utc, updated_at_utc
            FROM users
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    /// <summary>
    /// Атомарно создаёт встроенную учётку admin, только если её ещё нет.
    /// Конкурирующие запросы сериализуются SQLite; проигравший получает null
    /// и переходит к обычному входу.
    /// </summary>
    public async Task<UserRecord?> TryCreateAdminAsync(string password, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = _time.GetUtcNow();
        var hash = _passwords.Hash(password);
        try
        {
            await using var connection = _database.OpenConnection();
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO users(id, login, login_normalized, password_hash, is_admin, must_change_password,
                                  created_at_utc, updated_at_utc)
                SELECT $id, $login, $loginNormalized, $hash, 1, 0, $now, $now
                WHERE NOT EXISTS (SELECT 1 FROM users WHERE is_admin = 1);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$login", AdminLogin);
            command.Parameters.AddWithValue("$loginNormalized", AdminLoginNormalized);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return inserted == 1
                ? new UserRecord(id, AdminLogin, AdminLoginNormalized, hash, true, false, now, now)
                : null;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // Уникальный индекс admin/логина: параллельная настройка уже выиграла.
            return null;
        }
    }

    /// <summary>
    /// Все учётные записи для админской страницы. Пароли не покидают store.
    /// </summary>
    public async Task<IReadOnlyList<UserRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, login, login_normalized, password_hash, is_admin, must_change_password,
                   created_at_utc, updated_at_utc
            FROM users
            ORDER BY is_admin DESC, login_normalized;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<UserRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    /// <summary>
    /// Создаёт обычную учётную запись с временным паролем и правами по корням
    /// одной транзакцией. Логин admin зарезервирован. Используется управлением
    /// пользователями и тестами.
    /// </summary>
    internal Task<Guid> CreateUserAsync(
        string login,
        string password,
        bool mustChangePassword,
        CancellationToken cancellationToken) =>
        CreateUserAsync(login, password, mustChangePassword, permissions: null, cancellationToken);

    public async Task<Guid> CreateUserAsync(
        string login,
        string password,
        bool mustChangePassword,
        IReadOnlyDictionary<string, RootAccess>? permissions,
        CancellationToken cancellationToken)
    {
        var trimmedLogin = login.Trim();
        if (!IsValidLogin(trimmedLogin))
        {
            throw new InvalidUserLoginException();
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }

        var normalized = NormalizeLogin(trimmedLogin);
        if (normalized == AdminLoginNormalized)
        {
            throw new InvalidUserLoginException();
        }

        var id = Guid.NewGuid();
        var now = _time.GetUtcNow();
        var hash = _passwords.Hash(password);
        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO users(id, login, login_normalized, password_hash, is_admin, must_change_password,
                                      created_at_utc, updated_at_utc)
                    VALUES ($id, $login, $loginNormalized, $hash, 0, $mustChange, $now, $now);
                    """;
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$login", trimmedLogin);
                command.Parameters.AddWithValue("$loginNormalized", normalized);
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (permissions is not null)
            {
                foreach (var (rootName, access) in NormalizePermissions(permissions))
                {
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO user_root_permissions(user_id, root_name, access)
                        VALUES ($userId, $rootName, $access);
                        """;
                    insert.Parameters.AddWithValue("$userId", id.ToString("D"));
                    insert.Parameters.AddWithValue("$rootName", rootName);
                    insert.Parameters.AddWithValue("$access", RootAccessNames.ToStorageValue(access));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new UserLoginConflictException();
        }
    }

    /// <summary>
    /// Сброс пароля обычной учётной записи: новый временный пароль, обязательная
    /// смена и отзыв всех прежних сессий одной транзакцией. Встроенная admin
    /// не сбрасывается (для неё есть локальная команда восстановления).
    /// </summary>
    public async Task<bool> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(newPassword));
        }

        var hash = _passwords.Hash(newPassword);
        var now = _time.GetUtcNow();
        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE users SET password_hash = $hash, must_change_password = 1, updated_at_utc = $now
                WHERE id = $id AND is_admin = 0;
                """;
            update.Parameters.AddWithValue("$hash", hash);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", userId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE sessions SET revoked_at_utc = $now
                WHERE user_id = $userId AND revoked_at_utc IS NULL;
                """;
            revoke.Parameters.AddWithValue("$now", now.ToString("O"));
            revoke.Parameters.AddWithValue("$userId", userId.ToString("D"));
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Удаляет обычную учётную запись. Сессии и права удаляются каскадно;
    /// идентификатор автора остаётся в старых задачах и артефактах.
    /// Встроенная admin защищена условием is_admin = 0.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE id = $id AND is_admin = 0;";
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Допустимый логин: 1–64 символа, буквы/цифры Unicode, затем точка,
    /// подчёркивание или дефис. Регистр не важен; admin зарезервирован.
    /// </summary>
    public static bool IsValidLogin(string login) =>
        LoginPattern.IsMatch(login.Trim());

    private static readonly Regex LoginPattern = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, RootAccess> NormalizePermissions(
        IReadOnlyDictionary<string, RootAccess> permissions)
    {
        var normalized = new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rootName, access) in permissions)
        {
            if (access == RootAccess.None || string.IsNullOrWhiteSpace(rootName))
            {
                continue;
            }

            normalized[FileActor.NormalizeRoot(rootName)] = access;
        }

        return normalized;
    }

    public async Task<Guid?> TryCreateSessionAsync(UserRecord verifiedUser, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, user_id, created_at_utc, expires_at_utc, revoked_at_utc)
            SELECT $id, id, $createdAt, $expiresAt, NULL FROM users
            WHERE id = $userId AND password_hash = $passwordHash;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$userId", verifiedUser.Id.ToString("D"));
        command.Parameters.AddWithValue("$passwordHash", verifiedUser.PasswordHash);
        command.Parameters.AddWithValue("$createdAt", _time.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", expiresAtUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? sessionId : null;
    }

    public async Task<Guid?> TryChangePasswordAsync(
        UserRecord verifiedUser, Guid currentSessionId, string newPassword,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        var newHash = _passwords.Hash(newPassword);
        var now = _time.GetUtcNow();
        var newSessionId = Guid.NewGuid();
        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE users SET password_hash = $newHash, must_change_password = 0, updated_at_utc = $now
                WHERE id = $userId AND password_hash = $oldHash
                  AND EXISTS (SELECT 1 FROM sessions s WHERE s.id = $sessionId AND s.user_id = $userId
                              AND s.revoked_at_utc IS NULL AND s.expires_at_utc > $now);
                """;
            update.Parameters.AddWithValue("$newHash", newHash);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$userId", verifiedUser.Id.ToString("D"));
            update.Parameters.AddWithValue("$oldHash", verifiedUser.PasswordHash);
            update.Parameters.AddWithValue("$sessionId", currentSessionId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE sessions SET revoked_at_utc = $now WHERE user_id = $userId AND revoked_at_utc IS NULL;";
            revoke.Parameters.AddWithValue("$now", now.ToString("O"));
            revoke.Parameters.AddWithValue("$userId", verifiedUser.Id.ToString("D"));
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sessions(id, user_id, created_at_utc, expires_at_utc, revoked_at_utc)
                VALUES ($id, $userId, $now, $expiresAt, NULL);
                """;
            insert.Parameters.AddWithValue("$id", newSessionId.ToString("D"));
            insert.Parameters.AddWithValue("$userId", verifiedUser.Id.ToString("D"));
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            insert.Parameters.AddWithValue("$expiresAt", expiresAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return newSessionId;
    }

    public async Task<SessionUser?> GetActiveSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.user_id, s.expires_at_utc, u.login, u.is_admin, u.must_change_password
            FROM sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.id = $id AND s.revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
        if (expiresAt <= _time.GetUtcNow())
        {
            return null;
        }

        return new SessionUser(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0,
            expiresAt);
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions SET revoked_at_utc = $now
            WHERE id = $id AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions SET revoked_at_utc = $now
            WHERE user_id = $userId AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Меняет пароль пользователя и отзывает все его прежние сессии одной
    /// короткой транзакцией. Проверка текущего пароля выполняется вызывающим.
    /// </summary>
    /// <summary>
    /// Восстановление пароля admin владельцем сервера при остановленном
    /// приложении. Меняет только встроенную admin и отзывает её сессии.
    /// </summary>
    public async Task<bool> ReplaceAdminPasswordAsync(string newPassword, CancellationToken cancellationToken)
    {
        var admin = await FindAdminAsync(cancellationToken);
        if (admin is null)
        {
            return false;
        }

        var hash = _passwords.Hash(newPassword);
        var now = _time.GetUtcNow();
        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE users SET password_hash = $hash, must_change_password = 0, updated_at_utc = $now
                WHERE id = $id AND is_admin = 1 AND password_hash = $oldHash;
                """;
            update.Parameters.AddWithValue("$hash", hash);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", admin.Id.ToString("D"));
            update.Parameters.AddWithValue("$oldHash", admin.PasswordHash);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE sessions SET revoked_at_utc = $now WHERE user_id = $userId AND revoked_at_utc IS NULL;";
            revoke.Parameters.AddWithValue("$now", now.ToString("O"));
            revoke.Parameters.AddWithValue("$userId", admin.Id.ToString("D"));
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<UserRecord?> FindAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, login, login_normalized, password_hash, is_admin, must_change_password,
                   created_at_utc, updated_at_utc
            FROM users
            WHERE is_admin = 1
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    private async Task<bool> ExistsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public static string NormalizeLogin(string login) => login.Trim().ToUpperInvariant();

    private static UserRecord ReadUser(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4) != 0,
        reader.GetInt32(5) != 0,
        DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class UserLoginConflictException() : Exception("Логин уже занят.");

public sealed class InvalidUserLoginException() : Exception("Логин недопустим или зарезервирован.");
