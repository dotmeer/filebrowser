using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Access;

/// <summary>
/// Актор файловой операции. В ANONYMOUS и для admin полномочия полные;
/// обычная учётная запись несёт снимок прав по корням и набор закрытых
/// служебных каталогов. Снимок перечитывается на каждом новом действии,
/// поэтому изменение прав применяется к следующему запросу/операции.
/// </summary>
public sealed class FileActor
{
    private static readonly IReadOnlyDictionary<string, RootAccess> NoPermissions =
        new Dictionary<string, RootAccess>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Полный доступ без ограничений. Используется ANONYMOUS, admin и служебными операциями.</summary>
    public static FileActor Unrestricted { get; } = new(null, fullAccess: true);

    public static FileActor Anonymous { get; } = new(null, fullAccess: true);

    private readonly IReadOnlyDictionary<string, RootAccess> _permissions;

    private FileActor(
        Guid? userId,
        bool fullAccess,
        IReadOnlyDictionary<string, RootAccess>? permissions = null,
        ServicePathProtection? protection = null)
    {
        UserId = userId;
        IsFullAccess = fullAccess;
        _permissions = permissions ?? NoPermissions;
        Protection = protection ?? ServicePathProtection.None;
    }

    public Guid? UserId { get; }

    /// <summary>ANONYMOUS, admin или системный доступ: матрица прав не ограничивает.</summary>
    public bool IsFullAccess { get; }

    public ServicePathProtection Protection { get; }

    public static FileActor ForUser(
        Guid userId,
        IReadOnlyDictionary<string, RootAccess> permissions,
        ServicePathProtection protection) =>
        new(userId, fullAccess: false, permissions, protection);

    public static FileActor ForAdmin(Guid userId) => new(userId, fullAccess: true);

    public static FileActor Denied(ServicePathProtection protection) =>
        new(null, fullAccess: false, NoPermissions, protection);

    public RootAccess GetRootAccess(string rootName)
    {
        if (IsFullAccess)
        {
            return RootAccess.Write;
        }

        return !string.IsNullOrWhiteSpace(rootName)
            && _permissions.TryGetValue(NormalizeRoot(rootName), out var access)
                ? access
                : RootAccess.None;
    }

    public bool CanReadRoot(string rootName) =>
        IsFullAccess
        || (GetRootAccess(rootName) >= RootAccess.Read && !Protection.IsRootHidden(rootName));

    public bool CanWriteRoot(string rootName) =>
        IsFullAccess
        || (GetRootAccess(rootName) >= RootAccess.Write && !Protection.IsRootHidden(rootName));

    public bool IsProtectedPath(string physicalPath) =>
        !IsFullAccess && Protection.IsInsideProtected(physicalPath);

    public bool ContainsProtectedPath(string physicalPath) =>
        !IsFullAccess && Protection.ContainsProtected(physicalPath);

    public void EnsureReadableRoot(string rootName)
    {
        if (!CanReadRoot(rootName))
        {
            throw AccessDenied();
        }
    }

    public void EnsureWritableRoot(string rootName)
    {
        if (!CanWriteRoot(rootName))
        {
            throw AccessDenied();
        }
    }

    /// <summary>
    /// Проверяет чтение конкретного существующего объекта: право на корень
    /// и отсутствие попадания в закрытый служебный каталог.
    /// </summary>
    public void EnsureReadablePath(string rootName, string physicalPath)
    {
        EnsureReadableRoot(rootName);
        if (IsProtectedPath(physicalPath))
        {
            throw AccessDenied();
        }
    }

    /// <summary>Проверяет запись в конкретный объект/каталог.</summary>
    public void EnsureWritablePath(string rootName, string physicalPath)
    {
        EnsureWritableRoot(rootName);
        if (IsProtectedPath(physicalPath))
        {
            throw AccessDenied();
        }
    }

    /// <summary>
    /// Запрещает изменение объекта, который сам закрыт или содержит закрытую
    /// служебную ветку. Нужен для rename/delete/copy/move источников.
    /// </summary>
    public void EnsureMutableBranch(string rootName, string physicalPath)
    {
        EnsureWritableRoot(rootName);
        if (IsProtectedPath(physicalPath) || ContainsProtectedPath(physicalPath))
        {
            throw AccessDenied();
        }
    }

    public void EnsureNotProtectedDestination(string physicalPath)
    {
        if (IsProtectedPath(physicalPath))
        {
            throw AccessDenied();
        }
    }

    public void EnsureReadableReference(PathResolver resolver, FileRef fileRef) =>
        EnsureReadablePathCandidate(resolver.ResolveCandidate(fileRef));

    public void EnsureWritableReference(PathResolver resolver, FileRef fileRef) =>
        EnsureWritablePathCandidate(resolver.ResolveCandidate(fileRef));

    public void EnsureMutableReference(PathResolver resolver, FileRef fileRef) =>
        EnsureMutablePathCandidate(resolver.ResolveCandidate(fileRef));

    public void EnsureReadablePathCandidate(ResolvedPath candidate) =>
        EnsureReadablePath(candidate.Root, candidate.PhysicalPath);

    public void EnsureWritablePathCandidate(ResolvedPath candidate) =>
        EnsureWritablePath(candidate.Root, candidate.PhysicalPath);

    public void EnsureMutablePathCandidate(ResolvedPath candidate) =>
        EnsureMutableBranch(candidate.Root, candidate.PhysicalPath);

    public static string NormalizeRoot(string rootName) => rootName.Trim().ToUpperInvariant();

    private static FileSystemAccessDeniedException AccessDenied() =>
        new("Недостаточно прав для доступа к файловому корню или служебному каталогу.");
}
