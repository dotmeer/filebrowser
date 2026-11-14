using dotmeer.filebrowser.Access;

namespace dotmeer.filebrowser.FileSystem;

public sealed class FileSystemWriteService(PathResolver pathResolver)
{
    private const int DestinationLockPoolSize = 256;

    private static readonly object[] DestinationLocks = CreateDestinationLockPool();

    public FileRef CreateDirectory(FileRef parent, string name, FileActor? actor = null)
    {
        var access = actor ?? FileActor.Unrestricted;
        var parentCandidate = pathResolver.ResolveCandidate(parent);
        access.EnsureWritablePathCandidate(parentCandidate);
        PathResolver.ValidateNewName(name);
        var childCandidate = pathResolver.ResolveCandidate(new FileRef(parentCandidate.Root,
            string.IsNullOrEmpty(parentCandidate.RelativePath) ? name : $"{parentCandidate.RelativePath}/{name}"));
        access.EnsureNotProtectedDestination(childCandidate.PhysicalPath);
        var destination = pathResolver.ResolveNewChild(parent, name);
        var parentPhysicalPath = Path.GetDirectoryName(destination.PhysicalPath)
            ?? throw new InvalidFileSystemRequestException("Не удалось определить каталог назначения.");
        access.EnsureWritablePath(destination.Root, parentPhysicalPath);
        access.EnsureNotProtectedDestination(destination.PhysicalPath);
        lock (GetDestinationLock(destination.PhysicalPath))
        {
            EnsureDestinationDoesNotExist(destination.PhysicalPath);
            AtomicDirectory.Create(destination.PhysicalPath);
        }

        return new FileRef(destination.Root, destination.RelativePath);
    }

    public FileRef Rename(FileRef source, string newName, FileActor? actor = null)
    {
        var access = actor ?? FileActor.Unrestricted;
        access.EnsureMutableReference(pathResolver, source);
        var resolvedSource = ResolveMutableSource(source);
        access.EnsureMutableBranch(resolvedSource.Root, resolvedSource.PhysicalPath);
        var parentPath = GetParentPath(resolvedSource.RelativePath);
        PathResolver.ValidateNewName(newName);
        var parentRef = new FileRef(resolvedSource.Root, parentPath);
        access.EnsureWritableReference(pathResolver, parentRef);
        var destinationCandidate = pathResolver.ResolveCandidate(new FileRef(resolvedSource.Root,
            string.IsNullOrEmpty(parentPath) ? newName : $"{parentPath}/{newName}"));
        access.EnsureNotProtectedDestination(destinationCandidate.PhysicalPath);
        var destination = pathResolver.ResolveNewChild(parentRef, newName);
        access.EnsureNotProtectedDestination(destination.PhysicalPath);

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
        DestinationLocks[(uint)physicalPath.GetHashCode() % DestinationLocks.Length];

    private static object[] CreateDestinationLockPool()
    {
        var locks = new object[DestinationLockPoolSize];
        for (var index = 0; index < locks.Length; index++)
        {
            locks[index] = new object();
        }

        return locks;
    }

    private static void EnsureDestinationDoesNotExist(string physicalPath)
    {
        if (PathInspection.Exists(physicalPath))
        {
            throw new FileSystemNameConflictException("Объект с таким именем уже существует.");
        }
    }

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
    public FileSystemAccessDeniedException(string message) : base(message)
    {
    }

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
