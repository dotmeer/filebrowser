namespace dotmeer.filebrowser.FileSystem;

internal enum FileSystemEntryKind
{
    RegularFile,
    Directory,
    Symlink,
    Unsupported
}

internal static class PathInspection
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static void ThrowIfExistingPathContainsSymlink(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("Путь корня невалиден.");
        }

        var relativePath = Path.GetRelativePath(root, path);
        if (relativePath == ".")
        {
            return;
        }

        var currentPath = root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!TryGetAttributes(currentPath, out var attributes))
            {
                return;
            }

            if (IsSymlink(currentPath, attributes))
            {
                throw new InvalidOperationException("Путь корня не может содержать символические ссылки.");
            }
        }
    }

    public static void ThrowIfUnsupported(string path)
    {
        if (!TryClassify(path, out var kind))
        {
            throw new FileSystemResolutionException("Запрошенный объект не существует.");
        }

        switch (kind)
        {
            case FileSystemEntryKind.Symlink:
                throw new UnsupportedFileSystemObjectException("Символические ссылки не поддерживаются.");
            case FileSystemEntryKind.Unsupported:
                throw new UnsupportedFileSystemObjectException("Тип объекта не поддерживается.");
        }
    }

    public static bool TryClassify(string path, out FileSystemEntryKind kind)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            kind = default;
            return false;
        }

        kind = Classify(path, attributes);
        return true;
    }

    public static FileSystemEntryKind Classify(string path, FileAttributes attributes)
    {
        if (IsSymlink(path, attributes))
        {
            return FileSystemEntryKind.Symlink;
        }

        if (UnixFileType.IsSpecialFile(path) || (attributes & FileAttributes.Device) != 0)
        {
            return FileSystemEntryKind.Unsupported;
        }

        return (attributes & FileAttributes.Directory) != 0
            ? FileSystemEntryKind.Directory
            : FileSystemEntryKind.RegularFile;
    }

    public static bool PathsOverlap(string firstPath, string secondPath) =>
        IsWithin(firstPath, secondPath) || IsWithin(secondPath, firstPath);

    public static bool IsWithin(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(parentPath, candidatePath);
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && !relativePath.Equals("..", PathComparison)
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison));
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSymlink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
