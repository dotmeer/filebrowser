using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace dotmeer.filebrowser.Auth;

public static class AuthClaimTypes
{
    public const string SessionId = "filebrowser:session";
}

public enum AccessLevel
{
    Anonymous,
    MustChangePassword,
    Denied,
    Full
}

public enum LoginOutcome
{
    Success,
    InvalidCredentials
}

public enum ChangePasswordOutcome
{
    Success,
    InvalidCurrentPassword,
    SamePassword,
    UserNotFound,
    SessionRevoked
}

/// <summary>
/// Оркестрация входа, сессий и паролей. Cookie содержит только
/// зашифрованный идентификатор серверной сессии; права и признак смены
/// временного пароля перечитываются из SQLite при каждой проверке.
/// </summary>
public sealed class AuthService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    private readonly AuthOptions _options;
    private readonly UserStore _users;
    private readonly PasswordService _passwords;
    private readonly TimeProvider _time;
    private readonly string _dummyHash;

    public AuthService(
        AuthOptions options,
        UserStore users,
        PasswordService passwords,
        TimeProvider time)
    {
        _options = options;
        _users = users;
        _passwords = passwords;
        _time = time;
        _dummyHash = passwords.DummyHash;
    }

    public Task<bool> AdminExistsAsync(CancellationToken cancellationToken) =>
        _users.AdminExistsAsync(cancellationToken);

    public async Task<(LoginOutcome Outcome, UserRecord? User)> AuthenticateAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByLoginAsync(login, cancellationToken);
        var hash = user?.PasswordHash ?? _dummyHash;
        var passwordValid = _passwords.Verify(hash, password);
        return user is not null && passwordValid
            ? (LoginOutcome.Success, user)
            : (LoginOutcome.InvalidCredentials, null);
    }

    /// <summary>
    /// Атомарная первичная настройка admin. null — другой запрос уже создал
    /// администратора; вызывающий переходит к обычному входу.
    /// </summary>
    public async Task<UserRecord?> TrySetupAdminAsync(string password, CancellationToken cancellationToken)
    {
        return await _users.TryCreateAdminAsync(password, cancellationToken);
    }

    public async Task<(ChangePasswordOutcome Outcome, Guid? SessionId)> ChangeOwnPasswordAsync(
        UserRecord user,
        Guid currentSessionId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (!_passwords.Verify(user.PasswordHash, currentPassword))
        {
            return (ChangePasswordOutcome.InvalidCurrentPassword, null);
        }

        if (user.MustChangePassword
            && string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return (ChangePasswordOutcome.SamePassword, null);
        }

        var sessionId = await _users.TryChangePasswordAsync(
            user, currentSessionId, newPassword, _time.GetUtcNow().Add(SessionLifetime), cancellationToken);
        return sessionId is null
            ? (ChangePasswordOutcome.SessionRevoked, null)
            : (ChangePasswordOutcome.Success, sessionId);
    }

    public async Task<bool> SignInAsync(HttpContext httpContext, UserRecord user)
    {
        var expiresAtUtc = _time.GetUtcNow().Add(SessionLifetime);
        var sessionId = await _users.TryCreateSessionAsync(user, expiresAtUtc, httpContext.RequestAborted);
        if (sessionId is null)
        {
            return false;
        }
        await IssueCookieAsync(httpContext, user, sessionId.Value, expiresAtUtc);
        return true;
    }

    public Task SignInChangedPasswordAsync(HttpContext httpContext, UserRecord user, Guid sessionId) =>
        IssueCookieAsync(httpContext, user with { MustChangePassword = false }, sessionId,
            _time.GetUtcNow().Add(SessionLifetime));

    private static async Task IssueCookieAsync(HttpContext httpContext, UserRecord user, Guid sessionId, DateTimeOffset expiresAtUtc)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.IsAdmin ? "admin" : "user"),
                new Claim(AuthClaimTypes.SessionId, sessionId.ToString("D"))
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = expiresAtUtc
            });
    }

    public async Task SignOutAsync(HttpContext httpContext)
    {
        var sessionId = GetSessionId(httpContext.User);
        if (sessionId is { } id)
        {
            await _users.RevokeSessionAsync(id, httpContext.RequestAborted);
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Проверяет cookie-сессию. Cookie-событие вызывает этот метод на каждом
    /// защищённом HTTP-запросе; отозванная сессия отклоняется сразу.
    /// </summary>
    public async Task<SessionUser?> ValidatePrincipalAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var sessionId = GetSessionId(principal);
        return sessionId is null ? null : await _users.GetActiveSessionAsync(sessionId.Value, cancellationToken);
    }

    /// <summary>
    /// Текущий уровень доступа. В ANONYMOUS доступ полный независимо от
    /// cookie. В LOCAL источник истины — SQLite: отозванная/истёкшая сессия
    /// считается анонимной, временный пароль ограничивает доступ, действующая
    /// учётная запись (admin или обычная) получает рабочий UI. Файловые права
    /// обычной учётки проверяются отдельно по корням.
    /// </summary>
    public async Task<AccessLevel> EvaluateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (_options.IsAnonymous)
        {
            return AccessLevel.Full;
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return AccessLevel.Anonymous;
        }

        var session = await ValidatePrincipalAsync(principal, cancellationToken);
        if (session is null)
        {
            return AccessLevel.Anonymous;
        }

        if (session.MustChangePassword)
        {
            return AccessLevel.MustChangePassword;
        }

        return AccessLevel.Full;
    }

    public async Task<SessionUser?> GetCurrentSessionAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) =>
        _options.IsLocal ? await ValidatePrincipalAsync(principal, cancellationToken) : null;

    public static Guid? GetSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(AuthClaimTypes.SessionId)?.Value, out var sessionId)
            ? sessionId
            : null;
}
