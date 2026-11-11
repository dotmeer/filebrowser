namespace dotmeer.filebrowser.FileSystem;

internal static class FileSystemTreeWalker
{
    public static IEnumerable<FileSystemTreeNode> EnumerateValidated(
        string rootPath,
        CancellationToken cancellationToken = default,
        Func<string, bool>? shouldDescend = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var enumerators = new Stack<IEnumerator<string>>();
        var currentPath = rootPath;
        var hasCurrent = true;
        try
        {
            while (hasCurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathInspection.ThrowIfUnsupported(currentPath);
                var isDirectory = Directory.Exists(currentPath);
                yield return new FileSystemTreeNode(currentPath, isDirectory);

                if (isDirectory && (shouldDescend is null || shouldDescend(currentPath)))
                {
                    enumerators.Push(Directory.EnumerateFileSystemEntries(currentPath).GetEnumerator());
                }

                hasCurrent = false;
                while (enumerators.Count > 0 && !hasCurrent)
                {
                    var enumerator = enumerators.Peek();
                    if (enumerator.MoveNext())
                    {
                        currentPath = enumerator.Current;
                        hasCurrent = true;
                    }
                    else
                    {
                        enumerator.Dispose();
                        enumerators.Pop();
                    }
                }
            }
        }
        finally
        {
            while (enumerators.Count > 0)
            {
                enumerators.Pop().Dispose();
            }
        }
    }

    /// <summary>
    /// Итеративный обход, который молча пропускает symlink/неподдерживаемые/исчезнувшие
    /// узлы (не возвращает их и не заходит внутрь) вместо исключения. Используется
    /// поиском (SPEC §10.6): неподдерживаемые объекты исключаются, symlink не просматриваются.
    /// </summary>
    public static IEnumerable<FileSystemTreeNode> EnumerateSkippingUnsupported(
        string rootPath,
        CancellationToken cancellationToken = default,
        Func<string, bool>? shouldDescend = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var enumerators = new Stack<IEnumerator<string>>();
        var currentPath = rootPath;
        var hasCurrent = true;
        try
        {
            while (hasCurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = TryGetAttributes(currentPath);
                if (attributes.HasValue && IsSupported(attributes.Value))
                {
                    var isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
                    yield return new FileSystemTreeNode(currentPath, isDirectory);

                    if (isDirectory && (shouldDescend is null || shouldDescend(currentPath)))
                    {
                        var children = EnumerateChildren(currentPath);
                        if (children is not null)
                        {
                            enumerators.Push(children);
                        }
                    }
                }

                hasCurrent = false;
                while (enumerators.Count > 0 && !hasCurrent)
                {
                    var enumerator = enumerators.Peek();
                    string? next = null;
                    var moved = false;
                    try
                    {
                        moved = enumerator.MoveNext();
                        if (moved)
                        {
                            next = enumerator.Current;
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }

                    if (moved)
                    {
                        currentPath = next!;
                        hasCurrent = true;
                    }
                    else
                    {
                        enumerator.Dispose();
                        enumerators.Pop();
                    }
                }
            }
        }
        finally
        {
            while (enumerators.Count > 0)
            {
                enumerators.Pop().Dispose();
            }
        }
    }

    private static IEnumerator<string>? EnumerateChildren(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSupported(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0
        && (attributes & FileAttributes.Device) == 0;
}

internal readonly record struct FileSystemTreeNode(string Path, bool IsDirectory);
