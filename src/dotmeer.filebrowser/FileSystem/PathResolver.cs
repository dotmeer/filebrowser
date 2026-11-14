using dotmeer.filebrowser.Configuration;

namespace dotmeer.filebrowser.FileSystem;

public sealed class PathResolver
{
    private const string TemporaryArtifactPrefix = ".filebrowser-";

    private readonly FileBrowserConfiguration _configuration;

    public PathResolver(FileBrowserConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ResolvedPath ResolveExisting(FileRef fileRef) => ResolveExisting(fileRef, allowTemporaryArtifacts: false);

    /// <summary>Maps a syntactically valid public reference to its physical candidate without touching the filesystem.</summary>
    public ResolvedPath ResolveCandidate(FileRef fileRef)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        var segments = ValidateRelativePath(fileRef.Path);
        ValidateSegments(segments);
        ValidatePublicSegments(segments);
        var (root, relativeSegments) = ResolveRoot(fileRef.Root, segments);
        var physicalPath = relativeSegments.Aggregate(root.PhysicalPath, (current, segment) => Path.Combine(current, segment));
        physicalPath = Path.GetFullPath(physicalPath);
        if (!PathInspection.IsWithin(root.PhysicalPath, physicalPath))
        {
            throw new InvalidFileSystemRequestException("Путь находится вне разрешённого корня.");
        }
        return new ResolvedPath(root.Name, string.Join('/', relativeSegments), physicalPath, false);
    }

    /// <summary>
    /// Resolves a path that belongs to a registered temporary artifact.
    /// Used only by startup cleanup; public operations must use <see cref="ResolveExisting(FileRef)"/>.
    /// </summary>
    internal ResolvedPath ResolveExistingTemporaryArtifact(FileRef fileRef) => ResolveExisting(fileRef, allowTemporaryArtifacts: true);

    private ResolvedPath ResolveExisting(FileRef fileRef, bool allowTemporaryArtifacts)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        var segments = ValidateRelativePath(fileRef.Path);
        ValidateSegments(segments);
        if (!allowTemporaryArtifacts)
        {
            ValidatePublicSegments(segments);
        }

        // Виртуальный корень «Все корни» read-only только на верхнем уровне.
        // Путь под ним (<корень>/<относительный путь>) делегируется реальному
        // корню для всех операций, включая изменяющие (SPEC §7.1).
        var (root, relativeSegments) = ResolveRoot(fileRef.Root, segments);

        EnsureRootIsAvailable(root);

        var currentPath = root.PhysicalPath;
        for (var index = 0; index < relativeSegments.Count; index++)
        {
            currentPath = Path.Combine(currentPath, relativeSegments[index]);
            if (!PathInspection.IsWithin(root.PhysicalPath, currentPath))
            {
                throw new InvalidFileSystemRequestException("Путь находится вне разрешённого корня.");
            }
            if (!PathInspection.Exists(currentPath))
            {
                throw new FileSystemObjectNotFoundException("Запрошенный объект не существует.");
            }

            PathInspection.ThrowIfUnsupported(currentPath);
            if (index < relativeSegments.Count - 1 && !Directory.Exists(currentPath))
            {
                throw new InvalidFileSystemRequestException("Компонент пути не является каталогом.");
            }
        }

        return new ResolvedPath(root.Name, string.Join('/', relativeSegments), currentPath, Directory.Exists(currentPath));
    }

    private (ConfiguredRoot Root, IReadOnlyList<string> Segments) ResolveRoot(
        string rootName,
        IReadOnlyList<string> segments)
    {
        if (VirtualRoot.IsVirtualRoot(rootName))
        {
            if (segments.Count == 0)
            {
                throw new InvalidFileSystemRequestException("Виртуальный корень доступен только для чтения.");
            }

            return (GetRoot(segments[0]), segments.Skip(1).ToArray());
        }

        return (GetRoot(rootName), segments);
    }

    public ResolvedPath ResolveNewChild(FileRef parent, string name)
    {
        var resolvedParent = ResolveExisting(parent);
        if (!resolvedParent.IsDirectory)
        {
            throw new InvalidFileSystemRequestException("Родительский объект не является каталогом.");
        }

        ValidateNewName(name);
        var physicalPath = Path.GetFullPath(Path.Combine(resolvedParent.PhysicalPath, name));
        if (!PathInspection.IsWithin(resolvedParent.PhysicalPath, physicalPath))
        {
            throw new InvalidFileSystemRequestException("Путь находится вне разрешённого корня.");
        }

        if (PathInspection.Exists(physicalPath))
        {
            throw new FileSystemNameConflictException("Объект с таким именем уже существует.");
        }

        var relativePath = string.IsNullOrEmpty(resolvedParent.RelativePath)
            ? name
            : $"{resolvedParent.RelativePath}/{name}";
        return new ResolvedPath(resolvedParent.Root, relativePath, physicalPath, false);
    }

    public static void ValidateNewName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || IsReservedTemporaryArtifactName(name))
        {
            throw new InvalidFileSystemRequestException("Имя объекта недопустимо.");
        }
    }

    private ConfiguredRoot GetRoot(string rootName)
    {
        if (VirtualRoot.IsVirtualRoot(rootName))
        {
            throw new InvalidFileSystemRequestException("Виртуальный корень доступен только для чтения.");
        }

        if (string.IsNullOrWhiteSpace(rootName) || !_configuration.RootsByName.TryGetValue(rootName, out var root))
        {
            throw new FileSystemObjectNotFoundException("Указанный корень не найден.");
        }

        return root;
    }

    private static IReadOnlyList<string> ValidateRelativePath(string path)
    {
        if (path is null || path.IndexOf('\0') >= 0 || path.Contains('\\') || Path.IsPathRooted(path))
        {
            throw new InvalidFileSystemRequestException("Относительный путь недопустим.");
        }

        if (path.Length == 0)
        {
            return [];
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidFileSystemRequestException("Относительный путь недопустим.");
        }

        return segments;
    }

    private static void ValidateSegments(IReadOnlyList<string> segments)
    {
        if (OperatingSystem.IsWindows() && segments.Any(segment => segment.Length >= 2
                && char.IsAsciiLetter(segment[0]) && segment[1] == ':'))
        {
            throw new InvalidFileSystemRequestException("Относительный путь недопустим.");
        }
    }

    /// <summary>True for names reserved for registered temporary file artifacts.</summary>
    public static bool IsReservedTemporaryArtifactName(string name) =>
        name.StartsWith(TemporaryArtifactPrefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidatePublicSegments(IReadOnlyList<string> segments)
    {
        if (segments.Any(IsReservedTemporaryArtifactName))
        {
            throw new InvalidFileSystemRequestException("Служебные объекты недоступны через публичные операции.");
        }
    }

    private static void EnsureRootIsAvailable(ConfiguredRoot root)
    {
        if (!Directory.Exists(root.PhysicalPath))
        {
            throw new RootUnavailableException("Корень недоступен.");
        }

        try
        {
            PathInspection.ThrowIfExistingPathContainsSymlink(root.PhysicalPath);
        }
        catch (InvalidOperationException exception)
        {
            throw new UnsupportedFileSystemObjectException("Символические ссылки не поддерживаются.", exception);
        }

        PathInspection.ThrowIfUnsupported(root.PhysicalPath);
        if (!Directory.Exists(root.PhysicalPath))
        {
            throw new RootUnavailableException("Корень недоступен.");
        }
    }
}

public sealed record ResolvedPath(string Root, string RelativePath, string PhysicalPath, bool IsDirectory);

public class FileSystemResolutionException : Exception
{
    public FileSystemResolutionException(string message) : base(message)
    {
    }

    public FileSystemResolutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class RootUnavailableException : FileSystemResolutionException
{
    public RootUnavailableException(string message) : base(message)
    {
    }
}

public sealed class FileSystemObjectNotFoundException : FileSystemResolutionException
{
    public FileSystemObjectNotFoundException(string message) : base(message)
    {
    }

    public FileSystemObjectNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class InvalidFileSystemRequestException : FileSystemResolutionException
{
    public InvalidFileSystemRequestException(string message) : base(message)
    {
    }
}

public sealed class UnsupportedFileSystemObjectException : FileSystemResolutionException
{
    public UnsupportedFileSystemObjectException(string message) : base(message)
    {
    }

    public UnsupportedFileSystemObjectException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
