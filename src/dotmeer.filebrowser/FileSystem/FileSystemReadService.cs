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
            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
            var isDevice = (attributes & FileAttributes.Device) != 0;
            var kind = isSymlink || isDevice
                ? "unsupported"
                : (attributes & FileAttributes.Directory) != 0 ? "directory" : "file";
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

public sealed record DirectoryListingDto(string Root, string Path, IReadOnlyList<FileEntryDto> Entries);

public sealed record FileEntryDto(string Name, string Path, string Kind, long? Size, DateTimeOffset ModifiedAtUtc);
