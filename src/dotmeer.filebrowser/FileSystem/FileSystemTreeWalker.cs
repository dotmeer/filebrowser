namespace dotmeer.filebrowser.FileSystem;

internal static class FileSystemTreeWalker
{
    public static IEnumerable<FileSystemTreeNode> EnumerateValidated(
        string rootPath,
        CancellationToken cancellationToken = default)
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

                if (isDirectory)
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
}

internal readonly record struct FileSystemTreeNode(string Path, bool IsDirectory);
