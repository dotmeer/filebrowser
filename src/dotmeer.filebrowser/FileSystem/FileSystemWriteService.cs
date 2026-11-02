using System.Collections.Concurrent;

namespace dotmeer.filebrowser.FileSystem;

public sealed class FileSystemWriteService(PathResolver pathResolver)
{
    private static readonly ConcurrentDictionary<string, object> DestinationLocks = new(PathComparer);

    public FileRef CreateDirectory(FileRef parent, string name)
    {
        var destination = pathResolver.ResolveNewChild(parent, name);
        lock (GetDestinationLock(destination.PhysicalPath))
        {
            EnsureDestinationDoesNotExist(destination.PhysicalPath);
            try
            {
                Directory.CreateDirectory(destination.PhysicalPath);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new FileSystemAccessDeniedException("Недостаточно прав для создания каталога.", exception);
            }
            catch (IOException exception) when (PathInspection.Exists(destination.PhysicalPath))
            {
                throw new FileSystemNameConflictException("Объект с таким именем уже существует.", exception);
            }
            catch (IOException exception)
            {
                throw new FileSystemOperationException("Не удалось создать каталог.", exception);
            }
        }

        return new FileRef(destination.Root, destination.RelativePath);
    }

    public FileRef Rename(FileRef source, string newName)
    {
        var resolvedSource = ResolveMutableSource(source);
        var parentPath = GetParentPath(resolvedSource.RelativePath);
        var destination = pathResolver.ResolveNewChild(new FileRef(resolvedSource.Root, parentPath), newName);

        lock (GetDestinationLock(destination.PhysicalPath))
        {
            EnsureDestinationDoesNotExist(destination.PhysicalPath);
            try
            {
                if (resolvedSource.IsDirectory)
                {
                    Directory.Move(resolvedSource.PhysicalPath, destination.PhysicalPath);
                }
                else
                {
                    File.Move(resolvedSource.PhysicalPath, destination.PhysicalPath, overwrite: false);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new FileSystemAccessDeniedException("Недостаточно прав для переименования объекта.", exception);
            }
            catch (FileNotFoundException exception)
            {
                throw new FileSystemObjectNotFoundException("Исходный объект больше не существует.", exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new FileSystemObjectNotFoundException("Исходный объект больше не существует.", exception);
            }
            catch (IOException exception) when (PathInspection.Exists(destination.PhysicalPath))
            {
                throw new FileSystemNameConflictException("Объект с таким именем уже существует.", exception);
            }
            catch (IOException exception)
            {
                throw new FileSystemOperationException("Не удалось переименовать объект.", exception);
            }
        }

        return new FileRef(destination.Root, destination.RelativePath);
    }

    public void Delete(FileRef source)
    {
        var resolvedSource = ResolveMutableSource(source);
        try
        {
            if (resolvedSource.IsDirectory)
            {
                EnsureDirectoryTreeSupported(resolvedSource.PhysicalPath);
                Directory.Delete(resolvedSource.PhysicalPath, recursive: true);
            }
            else
            {
                File.Delete(resolvedSource.PhysicalPath);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FileSystemAccessDeniedException("Недостаточно прав для удаления объекта.", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileSystemObjectNotFoundException("Исходный объект больше не существует.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileSystemObjectNotFoundException("Исходный объект больше не существует.", exception);
        }
        catch (IOException exception)
        {
            throw new FileSystemOperationException("Не удалось удалить объект.", exception);
        }
    }

    private ResolvedPath ResolveMutableSource(FileRef source)
    {
        var resolvedSource = pathResolver.ResolveExisting(source);
        if (string.IsNullOrEmpty(resolvedSource.RelativePath))
        {
            throw new InvalidFileSystemRequestException("Корень нельзя изменять.");
        }

        return resolvedSource;
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static object GetDestinationLock(string physicalPath) =>
        DestinationLocks.GetOrAdd(physicalPath, static _ => new object());

    private static void EnsureDestinationDoesNotExist(string physicalPath)
    {
        if (PathInspection.Exists(physicalPath))
        {
            throw new FileSystemNameConflictException("Объект с таким именем уже существует.");
        }
    }

    private static void EnsureDirectoryTreeSupported(string directoryPath)
    {
        foreach (var childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            PathInspection.ThrowIfUnsupported(childPath);
            if (Directory.Exists(childPath))
            {
                EnsureDirectoryTreeSupported(childPath);
            }
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed class FileSystemNameConflictException : FileSystemResolutionException
{
    public FileSystemNameConflictException(string message) : base(message)
    {
    }

    public FileSystemNameConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class FileSystemAccessDeniedException : FileSystemResolutionException
{
    public FileSystemAccessDeniedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class FileSystemOperationException : FileSystemResolutionException
{
    public FileSystemOperationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
