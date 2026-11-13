using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Archive;

/// <summary>
/// Скачивание папки или набора объектов одной папки одним ZIP-архивом
/// (SPEC §12.2). Модель двухфазная, как у copy/move (§10.5):
/// <see cref="Plan"/> выполняет полный обход без чтения данных и собирает
/// ошибки, <see cref="WriteAsync"/> пишет архив в поток без промежуточного
/// файла. Записи сохраняются без сжатия; symlink и специальные объекты —
/// отказ всего запроса.
/// </summary>
public sealed class FileSystemArchiveService
{
    private const int CopyBufferSize = 128 * 1024;
    private const string TemporaryArtifactPrefix = ".filebrowser-";
    private static readonly DateTimeOffset MinimumZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MaximumZipTimestamp = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    private readonly PathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileSystemArchiveService> _logger;
    private readonly SemaphoreSlim _slots = new(
        ArchiveLimits.MaxConcurrentArchives,
        ArchiveLimits.MaxConcurrentArchives);

    public FileSystemArchiveService(
        PathResolver pathResolver,
        TimeProvider timeProvider,
        ILogger<FileSystemArchiveService> logger)
    {
        _pathResolver = pathResolver;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Занять слот формирования архива. Возвращает null, если свободных
    /// слотов нет (эндпоинт отвечает 429). Слот удерживается до конца
    /// записи ответа.
    /// </summary>
    public IDisposable? TryAcquireSlot() =>
        _slots.Wait(0) ? new SlotLease(_slots) : null;

    /// <summary>
    /// Предпроверка выбора. Пустой <paramref name="names"/> означает
    /// «все дети базовой папки», то есть скачивание папки целиком.
    /// Любая ошибка (symlink, спецфайл, исчезнувший объект, лимит)
    /// прерывает предпроверку: частичный архив не формируется.
    /// </summary>
    public ArchivePlan Plan(FileRef baseFolder, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        cancellationToken.ThrowIfCancellationRequested();

        // Виртуальный корень «Все корни» делегируется реальному, как при
        // скачивании файла (SPEC §7.1, §12.2). В самом списке корней
        // допустим ровно один выбранный корень: у нескольких корней нет
        // общей родительской папки, поэтому архив всех корней запрещён.
        if (VirtualRoot.IsVirtualRoot(baseFolder.Root))
        {
            var (rootName, remainingPath) = VirtualRoot.SplitPath(baseFolder.Path);
            if (string.IsNullOrEmpty(rootName))
            {
                if (names.Count != 1)
                {
                    throw new InvalidFileSystemRequestException(
                        "Скачивание всех корней одним архивом не поддерживается.");
                }

                PathResolver.ValidateNewName(names[0]);
                baseFolder = new FileRef(names[0], string.Empty);
                names = [];
            }
            else
            {
                baseFolder = new FileRef(rootName, remainingPath);
            }
        }

        var resolvedBase = _pathResolver.ResolveExisting(baseFolder);
        if (!resolvedBase.IsDirectory)
        {
            throw new InvalidFileSystemRequestException(
                "Архивом можно скачать только каталог или набор объектов каталога.");
        }

        // Выбрана ровно одна папка: базой архива становится она сама,
        // поэтому имя файла — <папка>.zip, а её содержимое лежит в корне
        // архива (SPEC §12.2). Для набора объектов базой остаётся
        // текущая папка, что делает структуру архива одинаковой.
        if (names.Count == 1)
        {
            PathResolver.ValidateNewName(names[0]);
            var candidate = _pathResolver.ResolveExisting(BuildChildRef(resolvedBase, names[0]));
            if (candidate.IsDirectory)
            {
                resolvedBase = candidate;
                names = [];
            }
        }

        var startedAt = _timeProvider.GetTimestamp();
        var entries = new List<ArchiveEntry>();
        long totalBytes = 0;

        try
        {
            foreach (var item in ResolveTopLevelItems(resolvedBase, names, startedAt, cancellationToken))
            {
                EnsurePreflightBudget(startedAt, cancellationToken);
                foreach (var node in FileSystemTreeWalker.EnumerateValidated(
                             item.PhysicalPath,
                             cancellationToken,
                             ShouldDescend))
                {
                    EnsurePreflightBudget(startedAt, cancellationToken);

                    if (IsTemporaryArtifactName(Path.GetFileName(node.Path)))
                    {
                        continue;
                    }

                    var entryName = BuildEntryName(item, node.Path);

                    ValidateEntryName(entryName);

                    if (node.IsDirectory)
                    {
                        entries.Add(new ArchiveEntry(
                            entryName,
                            node.Path,
                            IsDirectory: true,
                            Size: 0,
                            GetLastWriteTime(node.Path)));
                    }
                    else
                    {
                        var info = new FileInfo(node.Path);
                        // Доступность метаданных не гарантирует возможность
                        // открыть файл. Проверяем чтение до отправки ответа,
                        // не читая содержимое и не оставляя дескрипторов.
                        using (File.OpenHandle(node.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                        }

                        entries.Add(new ArchiveEntry(
                            entryName,
                            node.Path,
                            IsDirectory: false,
                            Size: info.Length,
                            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                        totalBytes += info.Length;
                    }

                    if (entries.Count > ArchiveLimits.MaxEntries)
                    {
                        throw new ArchiveLimitExceededException(
                            "Число объектов превышает допустимое для одного архива.");
                    }

                    if (totalBytes > ArchiveLimits.MaxTotalBytes)
                    {
                        throw new ArchiveLimitExceededException(
                            "Суммарный объём превышает допустимый для одного архива.");
                    }
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FileSystemAccessDeniedException(
                "Недостаточно прав для чтения выбранных объектов.",
                exception);
        }
        catch (FileSystemResolutionException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new FileSystemOperationException(
                "Не удалось прочитать выбранные объекты.",
                exception);
        }

        // Детерминированный порядок записей: имена в ordinal-порядке.
        // Каталог всегда предшествует собственному содержимому, потому что
        // его имя является префиксом имени потомка.
        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        return new ArchivePlan(BuildArchiveName(resolvedBase), entries, totalBytes);
    }

    /// <summary>
    /// Записать архив в поток. Все записи — без сжатия (SPEC §12.2).
    /// Исключение (объект исчез, нет прав, отмена) пробрасывается
    /// вызывающему: заголовки уже отправлены, поэтому эндпоинт обрывает
    /// соединение.
    /// </summary>
    public async Task WriteAsync(ArchivePlan plan, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        cancellationToken.ThrowIfCancellationRequested();
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                var directory = archive.CreateEntry(entry.Name + "/");
                directory.LastWriteTime = ClampTimestamp(entry.ModifiedAtUtc);
                continue;
            }

            var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression);
            zipEntry.LastWriteTime = ClampTimestamp(entry.ModifiedAtUtc);

            await using var source = new FileStream(
                entry.PhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = zipEntry.Open();
            await source.CopyToAsync(target, CopyBufferSize, cancellationToken);
        }
    }

    private IEnumerable<TopLevelItem> ResolveTopLevelItems(
        ResolvedPath baseDirectory,
        IReadOnlyList<string> names,
        long startedAt,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            // Ленивое перечисление: бюджет, отмена и лимит проверяются на
            // каждом элементе, полный список каталога не накапливается.
            var count = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(baseDirectory.PhysicalPath))
            {
                EnsurePreflightBudget(startedAt, cancellationToken);
                var childName = Path.GetFileName(child);
                if (IsTemporaryArtifactName(childName))
                {
                    continue;
                }

                if (++count > ArchiveLimits.MaxEntries)
                {
                    throw new ArchiveLimitExceededException(
                        "Число объектов превышает допустимое для одного архива.");
                }

                yield return new TopLevelItem(childName, child);
            }

            yield break;
        }

        // Явный набор тоже обрабатывается по одному: разрешённые объекты
        // не накапливаются до проверки лимита и бюджета.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            EnsurePreflightBudget(startedAt, cancellationToken);

            // Имя ребёнка проверяется общим валидатором имён: путь,
            // служебный префикс и пустые сегменты недопустимы.
            PathResolver.ValidateNewName(name);
            if (!seen.Add(name))
            {
                continue;
            }

            var resolved = _pathResolver.ResolveExisting(BuildChildRef(baseDirectory, name));
            yield return new TopLevelItem(name, resolved.PhysicalPath);
        }
    }

    private static FileRef BuildChildRef(ResolvedPath parent, string name)
    {
        var relativePath = parent.RelativePath.Length == 0
            ? name
            : $"{parent.RelativePath}/{name}";
        return new FileRef(parent.Root, relativePath);
    }

    private static string BuildEntryName(TopLevelItem item, string physicalPath)
    {
        var relative = Path.GetRelativePath(item.PhysicalPath, physicalPath);
        if (relative == ".")
        {
            return item.Name;
        }

        return $"{item.Name}/{relative.Replace('\\', '/')}";
    }

    private static void ValidateEntryName(string entryName)
    {
        if (entryName.Count(character => character == '/') + 1 > ArchiveLimits.MaxDepth)
        {
            throw new ArchiveLimitExceededException(
                "Глубина вложенности превышает допустимую для одного архива.");
        }

        if (Encoding.UTF8.GetByteCount(entryName) > ArchiveLimits.MaxEntryPathBytes)
        {
            throw new ArchiveLimitExceededException(
                "Слишком длинный путь объекта для архива.");
        }
    }

    private void EnsurePreflightBudget(long startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetElapsedTime(startedAt) > ArchiveLimits.PreflightBudget)
        {
            _logger.LogWarning("Предпроверка архива превысила бюджет времени.");
            throw new ArchiveScanTimeoutException("Не удалось проверить выбранные объекты за отведённое время.");
        }
    }

    private static bool ShouldDescend(string directoryPath) =>
        !IsTemporaryArtifactName(Path.GetFileName(directoryPath));

    private static bool IsTemporaryArtifactName(string name) =>
        name.StartsWith(TemporaryArtifactPrefix, StringComparison.Ordinal);

    private static string BuildArchiveName(ResolvedPath baseDirectory)
    {
        var baseName = baseDirectory.RelativePath.Length == 0
            ? baseDirectory.Root
            : baseDirectory.RelativePath[(baseDirectory.RelativePath.LastIndexOf('/') + 1)..];
        return baseName + ".zip";
    }

    private static DateTimeOffset GetLastWriteTime(string physicalPath)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(physicalPath), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return DateTimeOffset.UnixEpoch;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static DateTimeOffset ClampTimestamp(DateTimeOffset value) =>
        value < MinimumZipTimestamp
            ? MinimumZipTimestamp
            : value > MaximumZipTimestamp
                ? MaximumZipTimestamp
                : value;

    private sealed record TopLevelItem(string Name, string PhysicalPath);

    private sealed class SlotLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
