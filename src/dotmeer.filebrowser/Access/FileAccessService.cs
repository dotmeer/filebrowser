using System.Security.Claims;
using dotmeer.filebrowser.Auth;

namespace dotmeer.filebrowser.Access;

/// <summary>
/// Собирает <see cref="FileActor"/> из серверной сессии и текущих прав в
/// SQLite. Вызывающий код перечитывает актора перед каждым новым действием,
/// поэтому изменение прав не зависит от ранее открытого дерева или диалога.
/// </summary>
public sealed class FileAccessService
{
    private readonly AuthOptions _options;
    private readonly AuthService _auth;
    private readonly UserStore _users;
    private readonly RootPermissionStore _permissions;
    private readonly ServicePathProtectionProvider _protection;

    public FileAccessService(
        AuthOptions options,
        AuthService auth,
        UserStore users,
        RootPermissionStore permissions,
        ServicePathProtectionProvider protection)
    {
        _options = options;
        _auth = auth;
        _users = users;
        _permissions = permissions;
        _protection = protection;
    }

    public async Task<FileActor> CreateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (_options.IsAnonymous)
        {
            return FileActor.Anonymous;
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return FileActor.Denied(_protection.Current);
        }

        var session = await _auth.ValidatePrincipalAsync(principal, cancellationToken);
        if (session is null)
        {
            return FileActor.Denied(_protection.Current);
        }

        if (session.IsAdmin)
        {
            return FileActor.ForAdmin(session.UserId);
        }

        if (session.MustChangePassword)
        {
            return FileActor.Denied(_protection.Current);
        }

        var rights = await _permissions.GetForUserAsync(session.UserId, cancellationToken);
        return FileActor.ForUser(session.UserId, rights, _protection.Current);
    }

    /// <summary>
    /// Явный актор по ID учётной записи. Используется worker-слоем и прямыми
    /// сервисными тестами.
    /// </summary>
    public async Task<FileActor> CreateForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.MustChangePassword)
        {
            return FileActor.Denied(_protection.Current);
        }

        if (user.IsAdmin)
        {
            return FileActor.ForAdmin(user.Id);
        }

        var rights = await _permissions.GetForUserAsync(user.Id, cancellationToken);
        return FileActor.ForUser(user.Id, rights, _protection.Current);
    }

    /// <summary>
    /// Актор автора уже принятой задачи. Выход, смена и сброс пароля сами по
    /// себе права не отзывают: учитываются только существование учётной записи
    /// и текущие права по корням в SQLite. Удаление учётки даёт отказ.
    /// </summary>
    public async Task<FileActor> CreateForTaskAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return FileActor.Denied(_protection.Current);
        }

        if (user.IsAdmin)
        {
            return FileActor.ForAdmin(user.Id);
        }

        var rights = await _permissions.GetForUserAsync(user.Id, cancellationToken);
        return FileActor.ForUser(user.Id, rights, _protection.Current);
    }
}
