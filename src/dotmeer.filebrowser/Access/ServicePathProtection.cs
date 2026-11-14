using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Access;

/// <summary>
/// Неизменяемый набор физических каталогов, закрытых для обычных учётных
/// записей: непосредственные родители активных ConfigPath/DatabasePath,
/// попадающих внутрь настроенного корня. Сравнение — по сегментам с учётом
/// платформы; клиенту эти пути не выдаются.
/// </summary>
public sealed class ServicePathProtection
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string[] _protectedDirectories;
    private readonly HashSet<string> _hiddenRoots;

    internal ServicePathProtection(IEnumerable<string> protectedDirectories, IEnumerable<string> hiddenRoots)
    {
        _protectedDirectories = protectedDirectories
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        _hiddenRoots = new HashSet<string>(hiddenRoots, StringComparer.OrdinalIgnoreCase);
    }

    public static ServicePathProtection None { get; } = new([], []);

    public bool IsInsideProtected(string physicalPath) =>
        _protectedDirectories.Any(directory => PathInspection.IsWithin(directory, physicalPath));

    /// <summary>
    /// Истина для закрытого каталога и любого его предка, включая корень:
    /// такой предок нельзя рекурсивно архивировать, копировать, удалять
    /// или переименовывать целиком.
    /// </summary>
    public bool ContainsProtected(string physicalPath) =>
        _protectedDirectories.Any(directory => PathInspection.IsWithin(physicalPath, directory));

    public bool IsRootHidden(string rootName) =>
        _hiddenRoots.Contains(FileActor.NormalizeRoot(rootName));
}

/// <summary>
/// Вычисляет защиту один раз при старте из активных путей приложения.
/// Если служебный путь задан ссылкой, учитываются его заданный и фактический пути.
/// </summary>
public sealed class ServicePathProtectionProvider
{
    public ServicePathProtectionProvider(
        FileBrowserConfiguration configuration,
        ApplicationPaths applicationPaths)
    {
        Current = Build(configuration, applicationPaths);
    }

    public ServicePathProtection Current { get; }

    private static ServicePathProtection Build(
        FileBrowserConfiguration configuration,
        ApplicationPaths applicationPaths)
    {
        var directories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var hiddenRoots = new List<string>();

        foreach (var servicePath in new[] { applicationPaths.ConfigPath, applicationPaths.DatabasePath })
        {
            var physicalPath = ResolvePhysicalFilePath(servicePath);
            foreach (var path in new[] { Path.GetFullPath(servicePath), physicalPath })
            foreach (var root in configuration.Roots)
            {
                if (!PathInspection.IsWithin(root.PhysicalPath, path))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent))
                {
                    continue;
                }

                parent = Path.GetFullPath(parent);
                directories.Add(parent);
                if (PathInspection.IsWithin(root.PhysicalPath, parent)
                    && PathInspection.IsWithin(parent, root.PhysicalPath))
                {
                    // Родитель совпал с корнем: закрывается весь корень.
                    hiddenRoots.Add(root.Name);
                }
            }
        }

        return new ServicePathProtection(directories, hiddenRoots);
    }

    /// <summary>
    /// Разрешает каждый существующий компонент пути через ссылки, включая
    /// цепочки промежуточных ссылок. Нужен только для расчёта закрытой ветки.
    /// </summary>
    private static string ResolvePhysicalFilePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var pending = new Queue<string>();
            AddPathSegments(fullPath, pending);
            var current = Path.GetPathRoot(fullPath) ?? string.Empty;
            var linksFollowed = 0;
            while (pending.Count > 0)
            {
                var segment = pending.Dequeue();
                var candidate = Path.GetFullPath(Path.Combine(current, segment));
                FileSystemInfo? info = null;
                if (Directory.Exists(candidate))
                {
                    info = new DirectoryInfo(candidate);
                }
                else if (File.Exists(candidate))
                {
                    info = new FileInfo(candidate);
                }

                var target = info?.ResolveLinkTarget(returnFinalTarget: false);
                if (target is not null)
                {
                    if (++linksFollowed > 40)
                    {
                        return fullPath;
                    }
                    var remainder = pending.ToArray();
                    pending.Clear();
                    AddPathSegments(Path.GetFullPath(target.FullName), pending);
                    foreach (var part in remainder)
                    {
                        pending.Enqueue(part);
                    }
                    current = Path.GetPathRoot(target.FullName) ?? string.Empty;
                    continue;
                }

                current = candidate;
            }

            return Path.GetFullPath(current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fullPath;
        }
    }

    private static void AddPathSegments(string path, Queue<string> queue)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        foreach (var segment in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            queue.Enqueue(segment);
        }
    }
}
