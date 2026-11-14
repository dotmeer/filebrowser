using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using dotmeer.filebrowser.Access;

namespace dotmeer.filebrowser.Auth;

public static class AuthPolicies
{
    /// <summary>Рабочий UI: в ANONYMOUS разрешён всем, в LOCAL — действующей учётке.</summary>
    public const string Application = "FileBrowserApplication";

    /// <summary>Любая действующая учётная запись (смена собственного пароля).</summary>
    public const string Authenticated = "FileBrowserAuthenticated";
}

public sealed class ApplicationAccessRequirement : IAuthorizationRequirement;

public sealed class AuthenticatedUserRequirement : IAuthorizationRequirement;

public sealed class AccessPolicyHandler : AuthorizationHandler<IAuthorizationRequirement>
{
    private readonly AuthOptions _options;
    private readonly AuthService _auth;

    public AccessPolicyHandler(AuthOptions options, AuthService auth)
    {
        _options = options;
        _auth = auth;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IAuthorizationRequirement requirement)
    {
        if (_options.IsAnonymous)
        {
            context.Succeed(requirement);
            return;
        }

        if (requirement is ApplicationAccessRequirement)
        {
            if (await _auth.EvaluateAsync(context.User, CancellationToken.None) == AccessLevel.Full)
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (requirement is AuthenticatedUserRequirement
            && await _auth.ValidatePrincipalAsync(context.User, CancellationToken.None) is not null)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Проверка доступа из интерактивных серверных действий. Открытый circuit
/// хранит исходный principal, но не является доказательством полномочий:
/// каждый вызов перечитывает сессию из SQLite.
/// </summary>
public sealed class AccessGuard
{
    private readonly AuthOptions _options;
    private readonly AuthService _auth;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly FileAccessService _fileAccess;

    public AccessGuard(
        AuthOptions options,
        AuthService auth,
        AuthenticationStateProvider authenticationState,
        IHttpContextAccessor httpContextAccessor,
        FileAccessService fileAccess)
    {
        _options = options;
        _auth = auth;
        _authenticationState = authenticationState;
        _httpContextAccessor = httpContextAccessor;
        _fileAccess = fileAccess;
    }

    public async Task<AccessLevel> GetAccessLevelAsync()
    {
        if (_options.IsAnonymous)
        {
            return AccessLevel.Full;
        }

        return await _auth.EvaluateAsync(await GetPrincipalAsync(), CancellationToken.None);
    }

    /// <summary>
    /// Актуальный файловый актор для текущего действия. Снимок прав
    /// перечитывается из SQLite, поэтому ранее открытое дерево или диалог
    /// не сохраняет устаревшие полномочия.
    /// </summary>
    public async Task<FileActor> GetFileActorAsync()
    {
        if (_options.IsAnonymous)
        {
            return FileActor.Anonymous;
        }

        return await _fileAccess.CreateAsync(await GetPrincipalAsync(), CancellationToken.None);
    }

    public async Task<ClaimsPrincipal> GetPrincipalAsync()
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            return principal;
        }

        return (await _authenticationState.GetAuthenticationStateAsync()).User;
    }

    /// <summary>
    /// Полномочия администратора перечитываются из SQLite для каждого
    /// административного действия; claim в открытом circuit не является
    /// доказательством. В ANONYMOUS управление учётками недоступно.
    /// </summary>
    public async Task<bool> IsAdminAsync()
    {
        if (_options.IsAnonymous)
        {
            return false;
        }

        var session = await _auth.ValidatePrincipalAsync(await GetPrincipalAsync(), CancellationToken.None);
        return session?.IsAdmin == true;
    }

    public async Task<bool> HasApplicationAccessAsync() =>
        await GetAccessLevelAsync() == AccessLevel.Full;

    public static string GetRedirectPath(AccessLevel level) => level switch
    {
        AccessLevel.Anonymous => "/login",
        AccessLevel.MustChangePassword => "/change-password",
        AccessLevel.Denied => "/access-denied",
        _ => "/"
    };
}
