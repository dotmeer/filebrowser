using dotmeer.filebrowser.Configuration;

namespace dotmeer.filebrowser.FileSystem;

public sealed class FileSystemReadService
{
    private readonly FileBrowserConfiguration _configuration;
    private readonly PathResolver _pathResolver;

    public FileSystemReadService(FileBrowserConfiguration configuration, PathResolver pathResolver)
    {
        _configuration = configuration;
        _pathResolver = pathResolver;
    }

    public IReadOnlyList<RootDto> GetRoots()
    {
        return _configuration.Roots
            .Select(root => new RootDto(root.Name, IsAvailable(root.Name)))
            .ToArray();
    }

    public DirectoryListingDto List(FileRef directoryRef)
    {
        if (VirtualRoot.IsVirtualRoot(directoryRef.Root))
        {
            return ListVirtualRoot(directoryRef.Path);
        }

        var resolved = _pathResolver.ResolveExisting(directoryRef);
        if (!resolved.IsDirectory)
        {
            throw new InvalidFileSystemRequestException("Запрошенный объект не является каталогом.");
        }

        var entries = new List<FileEntryDto>();
        foreach (var path in Directory.EnumerateFileSystemEntries(resolved.PhysicalPath))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(".filebrowser-", StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = File.GetAttributes(path);
            var info = (attributes & FileAttributes.Directory) != 0
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);
            var entryKind = PathInspection.Classify(path, attributes);
            var kind = entryKind switch
            {
                FileSystemEntryKind.Directory => "directory",
                FileSystemEntryKind.RegularFile => "file",
                _ => "unsupported"
            };
            var relativePath = string.IsNullOrEmpty(resolved.RelativePath)
                ? name
                : $"{resolved.RelativePath}/{name}";

            entries.Add(new FileEntryDto(
                name,
                relativePath,
                kind,
                kind == "file" ? ((FileInfo)info).Length : null,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        var ordered = entries
            .OrderBy(entry => entry.Kind switch
            {
                "directory" => 0,
                "file" => 1,
                _ => 2
            })
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        return new DirectoryListingDto(resolved.Root, resolved.RelativePath, ordered);
    }

    public ResolvedFile OpenFile(FileRef fileRef)
    {
        if (VirtualRoot.IsVirtualRoot(fileRef.Root))
        {
            var (rootName, rest) = VirtualRoot.SplitPath(fileRef.Path);
            return OpenFile(new FileRef(rootName, rest));
        }

        var resolved = _pathResolver.ResolveExisting(fileRef);
        if (resolved.IsDirectory)
        {
            throw new InvalidFileSystemRequestException("Скачивание поддерживается только для файлов.");
        }

        try
        {
            var stream = new FileStream(resolved.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var name = resolved.RelativePath[(resolved.RelativePath.LastIndexOf('/') + 1)..];
            return new ResolvedFile(name, stream);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileSystemObjectNotFoundException("Запрошенный объект не существует.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileSystemObjectNotFoundException("Запрошенный объект не существует.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FileSystemAccessDeniedException("Недостаточно прав для чтения файла.", exception);
        }
        catch (IOException exception)
        {
            throw new FileSystemOperationException("Не удалось открыть файл.", exception);
        }
    }

    private DirectoryListingDto ListVirtualRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            var entries = new List<FileEntryDto>();
            foreach (var root in _configuration.Roots)
            {
                DateTimeOffset? modifiedAtUtc = null;
                var kind = "unsupported";
                if (IsAvailable(root.Name))
                {
                    kind = "directory";
                    try
                    {
                        modifiedAtUtc = new DateTimeOffset(Directory.GetLastWriteTimeUtc(root.PhysicalPath), TimeSpan.Zero);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                entries.Add(new FileEntryDto(root.Name, root.Name, kind, null, modifiedAtUtc));
            }

            var ordered = entries
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            return new DirectoryListingDto(VirtualRoot.VirtualRootName, string.Empty, ordered);
        }

        var (rootName, rest) = VirtualRoot.SplitPath(path);
        var listing = List(new FileRef(rootName, rest));
        var prefixed = listing.Entries
            .Select(entry => entry with { Path = $"{rootName}/{entry.Path}" })
            .ToArray();
        return new DirectoryListingDto(VirtualRoot.VirtualRootName, path, prefixed);
    }

    private bool IsAvailable(string rootName)
    {
        try
        {
            _ = _pathResolver.ResolveExisting(new FileRef(rootName, string.Empty));
            return true;
        }
        catch (RootUnavailableException)
        {
            return false;
        }
        catch (FileSystemResolutionException)
        {
            return false;
        }
    }
}

public sealed record RootDto(string Name, bool Available);

public sealed record ResolvedFile(string Name, FileStream Stream);

public sealed record DirectoryListingDto(string Root, string Path, IReadOnlyList<FileEntryDto> Entries);

public sealed record FileEntryDto(string Name, string Path, string Kind, long? Size, DateTimeOffset? ModifiedAtUtc);
