using dotmeer.filebrowser.Configuration;

namespace dotmeer.filebrowser.FileSystem;

public sealed class PathResolver
{
    private readonly FileBrowserConfiguration _configuration;

    public PathResolver(FileBrowserConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ResolvedPath ResolveExisting(FileRef fileRef)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        var root = GetRoot(fileRef.Root);
        var segments = ValidateRelativePath(fileRef.Path);
        EnsureRootIsAvailable(root);

        var currentPath = root.PhysicalPath;
        for (var index = 0; index < segments.Count; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            if (!PathInspection.Exists(currentPath))
            {
                throw new FileSystemObjectNotFoundException("Запрошенный объект не существует.");
            }

            PathInspection.ThrowIfUnsupported(currentPath);
            if (index < segments.Count - 1 && !Directory.Exists(currentPath))
            {
                throw new InvalidFileSystemRequestException("Компонент пути не является каталогом.");
            }
        }

        return new ResolvedPath(root.Name, string.Join('/', segments), currentPath, Directory.Exists(currentPath));
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
            || name.StartsWith(".filebrowser-", StringComparison.Ordinal))
        {
            throw new InvalidFileSystemRequestException("Имя объекта недопустимо.");
        }
    }

    private ConfiguredRoot GetRoot(string rootName)
    {
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
