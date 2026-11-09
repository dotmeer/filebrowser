using System.Diagnostics;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem.Search;
using dotmeer.filebrowser.Observability;

namespace dotmeer.filebrowser.FileSystem;

public sealed class FileSystemSearchService
{
    public const int DefaultResultLimit = 500;

    public const int MaxResultLimit = 500;

    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(60);

    private const int BudgetCheckInterval = 64;

    private readonly FileBrowserConfiguration _configuration;
    private readonly PathResolver _pathResolver;
    private readonly SearchMetrics _searchMetrics;

    public FileSystemSearchService(
        FileBrowserConfiguration configuration,
        PathResolver pathResolver,
        SearchMetrics searchMetrics)
    {
        _configuration = configuration;
        _pathResolver = pathResolver;
        _searchMetrics = searchMetrics;
    }

    public SearchResultDto Search(
        FileRef start,
        string query,
        string? ext,
        IReadOnlyList<GlobMatcher>? extGlobs,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidFileSystemRequestException("Поисковый запрос не может быть пустым.");
        }

        if (limit < 1 || limit > MaxResultLimit)
        {
            throw new InvalidFileSystemRequestException("Лимит результатов поиска должен быть от 1 до 500.");
        }

        var isVirtual = VirtualRoot.IsVirtualRoot(start.Root);
        string responseRoot;
        string responsePath;
        if (isVirtual && string.IsNullOrEmpty(start.Path))
        {
            responseRoot = VirtualRoot.VirtualRootName;
            responsePath = string.Empty;
        }
        else
        {
            var effectiveStart = start;
            if (isVirtual)
            {
                var (rootName, rest) = VirtualRoot.SplitPath(start.Path);
                effectiveStart = new FileRef(rootName, rest);
            }

            var resolvedStart = _pathResolver.ResolveExisting(effectiveStart);
            if (!resolvedStart.IsDirectory)
            {
                throw new InvalidFileSystemRequestException("Поиск доступен только для каталогов.");
            }

            responseRoot = isVirtual ? VirtualRoot.VirtualRootName : resolvedStart.Root;
            responsePath = isVirtual ? start.Path : resolvedStart.RelativePath;
        }

        using var activity = FileBrowserTelemetry.ActivitySource.StartActivity("fs.search", ActivityKind.Internal);
        var stopwatch = Stopwatch.StartNew();
        var results = new List<FileEntryDto>();
        var unavailableRoots = new List<string>();
        var truncated = false;
        var visitedNodes = 0;

        if (isVirtual && string.IsNullOrEmpty(start.Path))
        {
            foreach (var root in _configuration.Roots)
            {
                ResolvedPath resolved;
                try
                {
                    resolved = _pathResolver.ResolveExisting(new FileRef(root.Name, string.Empty));
                }
                catch (FileSystemResolutionException)
                {
                    unavailableRoots.Add(root.Name);
                    continue;
                }

                var addedFrom = results.Count;
                truncated = WalkSingleRoot(
                    resolved, query, extGlobs, limit, results, stopwatch, cancellationToken, ref visitedNodes);
                for (var index = addedFrom; index < results.Count; index++)
                {
                    results[index] = results[index] with { Path = $"{root.Name}/{results[index].Path}" };
                }

                if (truncated)
                {
                    break;
                }
            }
        }
        else
        {
            var effectiveStart = start;
            if (isVirtual)
            {
                var (rootName, rest) = VirtualRoot.SplitPath(start.Path);
                effectiveStart = new FileRef(rootName, rest);
            }

            var resolved = _pathResolver.ResolveExisting(effectiveStart);
            truncated = WalkSingleRoot(
                resolved, query, extGlobs, limit, results, stopwatch, cancellationToken, ref visitedNodes);
            if (isVirtual)
            {
                for (var index = 0; index < results.Count; index++)
                {
                    results[index] = results[index] with { Path = $"{resolved.Root}/{results[index].Path}" };
                }
            }
        }

        var ordered = results
            .OrderBy(entry => isVirtual ? GetVirtualRootName(entry.Path) : string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => entry.Kind switch
            {
                "directory" => 0,
                "file" => 1,
                _ => 2
            })
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        activity?.SetTag("search.truncated", truncated);
        activity?.SetTag("search.result_count", ordered.Length);
        _searchMetrics.SearchCompleted(truncated);

        return new SearchResultDto(responseRoot, responsePath, query, ext, truncated, ordered)
        {
            UnavailableRoots = unavailableRoots
        };
    }

    /// <summary>
    /// Обходит один реальный корень от уже разрешённого стартового каталога, добавляя
    /// совпадения в общий список результатов. Лимит результатов, бюджет времени и счётчик
    /// узлов общие на весь запрос. Возвращает true, если обход остановлен досрочно.
    /// </summary>
    private static bool WalkSingleRoot(
        ResolvedPath resolved,
        string query,
        IReadOnlyList<GlobMatcher>? extGlobs,
        int limit,
        List<FileEntryDto> results,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        ref int visitedNodes)
    {
        foreach (var node in FileSystemTreeWalker.EnumerateSkippingUnsupported(
            resolved.PhysicalPath,
            cancellationToken,
            ShouldDescendInto))
        {
            if (results.Count >= limit || stopwatch.Elapsed >= TimeBudget)
            {
                return true;
            }

            if (++visitedNodes % BudgetCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (IsStartNode(resolved, node))
            {
                continue;
            }

            var name = Path.GetFileName(node.Path);
            if (name.StartsWith(".filebrowser-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!NameMatching.ContainsOrdinalIgnoreCase(name, query))
            {
                continue;
            }

            if (extGlobs is not null && !extGlobs.Any(glob => glob.IsMatch(name)))
            {
                continue;
            }

            var entry = TryCreateEntry(resolved, node);
            if (entry is null)
            {
                continue;
            }

            results.Add(entry);
        }

        return false;
    }

    private static string GetVirtualRootName(string path)
    {
        var separator = path.IndexOf('/');
        return separator < 0 ? path : path[..separator];
    }

    private static bool ShouldDescendInto(string path) =>
        !Path.GetFileName(path).StartsWith(".filebrowser-", StringComparison.Ordinal);

    private static bool IsStartNode(ResolvedPath start, FileSystemTreeNode node) =>
        string.Equals(
            Path.GetFullPath(node.Path),
            Path.GetFullPath(start.PhysicalPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static FileEntryDto? TryCreateEntry(ResolvedPath start, FileSystemTreeNode node)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(node.Path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
        var isDevice = (attributes & FileAttributes.Device) != 0;
        if (isSymlink || isDevice)
        {
            return null;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(node.Path)
            : new FileInfo(node.Path);
        var kind = (attributes & FileAttributes.Directory) != 0 ? "directory" : "file";
        var name = Path.GetFileName(node.Path);
        var relativePath = Path.GetRelativePath(start.PhysicalPath, node.Path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (!string.IsNullOrEmpty(start.RelativePath))
        {
            relativePath = $"{start.RelativePath}/{relativePath}";
        }

        return new FileEntryDto(
            name,
            relativePath,
            kind,
            kind == "file" ? ((FileInfo)info).Length : null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }
}

public sealed record SearchResultDto(
    string Root,
    string Path,
    string Query,
    string? Ext,
    bool Truncated,
    IReadOnlyList<FileEntryDto> Results)
{
    /// <summary>
    /// Недоступные реальные корня, пропущенные при поиске по виртуальному корню
    /// (SPEC §10.6). Для поиска по реальному корню — пустой массив.
    /// </summary>
    public IReadOnlyList<string> UnavailableRoots { get; init; } = [];
}
