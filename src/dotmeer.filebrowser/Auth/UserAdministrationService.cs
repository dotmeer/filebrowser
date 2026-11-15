using System.Security.Claims;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Settings;

namespace dotmeer.filebrowser.Auth;

public enum UserManagementOutcome
{
    Success,
    Denied,
    AdminProtected,
    DuplicateLogin,
    InvalidLogin,
    PasswordRequired,
    UserNotFound
}

public sealed record UserManagementResult(UserManagementOutcome Outcome)
{
    public bool Succeeded => Outcome == UserManagementOutcome.Success;
}

public sealed record ManagedUser(
    Guid Id,
    string Login,
    bool IsAdmin,
    bool MustChangePassword,
    IReadOnlyDictionary<string, RootAccess> Rights);

public sealed record UserListResult(bool Allowed, IReadOnlyList<ManagedUser> Users);

/// <summary>
/// Административные действия над учётными записями.
/// Полномочия проверяются на сервере в каждом вызове по текущей сессии в
/// SQLite, поэтому прямой вызов обработчика обычной учёткой не проходит.
/// </summary>
public sealed class UserAdministrationService(
    UserStore users,
    RootPermissionStore permissions,
    FileBrowserConfiguration configuration,
    AuthOptions options,
    AuthService auth)
{
    public async Task<bool> IsAdminAsync(ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (options.IsAnonymous)
        {
            return false;
        }

        var session = await auth.ValidatePrincipalAsync(actor, cancellationToken);
        return session?.IsAdmin == true;
    }

    public async Task<UserListResult> ListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(actor, cancellationToken))
        {
            return new UserListResult(false, []);
        }

        var stored = await users.GetAllAsync(cancellationToken);
        var managed = new List<ManagedUser>(stored.Count);
        foreach (var user in stored)
        {
            var rights = user.IsAdmin
                ? new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase)
                : await permissions.GetForUserAsync(user.Id, cancellationToken);
            managed.Add(new ManagedUser(user.Id, user.Login, user.IsAdmin, user.MustChangePassword, rights));
        }

        return new UserListResult(true, managed);
    }

    public async Task<UserManagementResult> CreateAsync(
        ClaimsPrincipal actor,
        string login,
        string temporaryPassword,
        IReadOnlyDictionary<string, RootAccess> rights,
        CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(actor, cancellationToken))
        {
            return new UserManagementResult(UserManagementOutcome.Denied);
        }

        if (!UserStore.IsValidLogin(login))
        {
            return new UserManagementResult(UserManagementOutcome.InvalidLogin);
        }

        if (string.IsNullOrWhiteSpace(temporaryPassword))
        {
            return new UserManagementResult(UserManagementOutcome.PasswordRequired);
        }

        try
        {
            await users.CreateUserAsync(
                login.Trim(),
                temporaryPassword,
                mustChangePassword: true,
                NormalizeRights(rights),
                cancellationToken);
            return new UserManagementResult(UserManagementOutcome.Success);
        }
        catch (UserLoginConflictException)
        {
            return new UserManagementResult(UserManagementOutcome.DuplicateLogin);
        }
        catch (InvalidUserLoginException)
        {
            return new UserManagementResult(UserManagementOutcome.InvalidLogin);
        }
    }

    public async Task<UserManagementResult> UpdateRightsAsync(
        ClaimsPrincipal actor,
        Guid userId,
        IReadOnlyDictionary<string, RootAccess> rights,
        CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(actor, cancellationToken))
        {
            return new UserManagementResult(UserManagementOutcome.Denied);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return new UserManagementResult(UserManagementOutcome.UserNotFound);
        }

        if (user.IsAdmin)
        {
            // admin всегда имеет полный доступ; права не редактируются.
            return new UserManagementResult(UserManagementOutcome.AdminProtected);
        }

        await permissions.SetAllAsync(userId, NormalizeRights(rights), cancellationToken);
        return new UserManagementResult(UserManagementOutcome.Success);
    }

    public async Task<UserManagementResult> ResetPasswordAsync(
        ClaimsPrincipal actor,
        Guid userId,
        string temporaryPassword,
        CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(actor, cancellationToken))
        {
            return new UserManagementResult(UserManagementOutcome.Denied);
        }

        if (string.IsNullOrWhiteSpace(temporaryPassword))
        {
            return new UserManagementResult(UserManagementOutcome.PasswordRequired);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return new UserManagementResult(UserManagementOutcome.UserNotFound);
        }

        if (user.IsAdmin)
        {
            // Пароль admin меняется им самим или локальной командой восстановления.
            return new UserManagementResult(UserManagementOutcome.AdminProtected);
        }

        return await users.ResetPasswordAsync(userId, temporaryPassword, cancellationToken)
            ? new UserManagementResult(UserManagementOutcome.Success)
            : new UserManagementResult(UserManagementOutcome.UserNotFound);
    }

    public async Task<UserManagementResult> DeleteAsync(
        ClaimsPrincipal actor,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(actor, cancellationToken))
        {
            return new UserManagementResult(UserManagementOutcome.Denied);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return new UserManagementResult(UserManagementOutcome.UserNotFound);
        }

        if (user.IsAdmin)
        {
            return new UserManagementResult(UserManagementOutcome.AdminProtected);
        }

        return await users.DeleteAsync(userId, cancellationToken)
            ? new UserManagementResult(UserManagementOutcome.Success)
            : new UserManagementResult(UserManagementOutcome.UserNotFound);
    }

    /// <summary>
    /// Оставляет только настроенные корни; отсутствие корня в наборе означает
    /// отсутствие доступа. Имена приводятся к имени из конфигурации.
    /// </summary>
    private Dictionary<string, RootAccess> NormalizeRights(IReadOnlyDictionary<string, RootAccess> rights)
    {
        var normalized = new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in configuration.Roots)
        {
            if (rights.TryGetValue(root.Name, out var access) && access != RootAccess.None)
            {
                normalized[root.Name] = access;
            }
        }

        return normalized;
    }
}

/// <summary>
/// Смена глобального языка интерфейса. В LOCAL это доступно только admin;
/// в ANONYMOUS сохраняется прежнее поведение без входа. Источник истины —
/// текущая сессия в SQLite, а не claim в открытом circuit.
/// </summary>
public sealed class LanguageSettingsService(
    AppSettingsService settings,
    AuthOptions options,
    AuthService auth)
{
    public async Task<bool> TrySetAsync(
        ClaimsPrincipal actor,
        string language,
        CancellationToken cancellationToken)
    {
        if (options.IsLocal)
        {
            var session = await auth.ValidatePrincipalAsync(actor, cancellationToken);
            if (session?.IsAdmin != true)
            {
                return false;
            }
        }

        await settings.SetLanguageAsync(language, cancellationToken);
        return true;
    }
}
