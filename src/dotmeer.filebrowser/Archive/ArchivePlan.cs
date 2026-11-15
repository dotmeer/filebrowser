using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Archive;

/// <summary>Запись будущего архива: путь считается относительно базовой папки.</summary>
public sealed record ArchiveEntry(string Name, string PhysicalPath, bool IsDirectory, long Size, DateTimeOffset ModifiedAtUtc);

/// <summary>
/// Результат предпроверки: имя архива, отсортированный список записей и
/// суммарный объём. Манифест существует только в памяти и не кешируется
/// между предпроверкой UI и предпроверкой эндпоинта.
/// </summary>
public sealed record ArchivePlan(
    string ArchiveName,
    IReadOnlyList<ArchiveEntry> Entries,
    long TotalBytes,
    IReadOnlyList<string>? SkippedSymlinkPaths = null)
{
    public int SkippedSymlinkCount => SkippedSymlinkPaths?.Count ?? 0;
}

/// <summary>
/// Выбор для скачивания архивом: базовая папка и имена её детей
/// (пустой список — все дети, то есть скачивание папки целиком).
/// <see cref="CreatorUserId"/> привязывает тикет к учётной записи в LOCAL.
/// </summary>
public sealed record ArchiveSelection(
    string Root,
    string Path,
    IReadOnlyList<string> Names,
    Guid? CreatorUserId = null,
    IReadOnlyList<string>? ConfirmedSkippedSymlinkPaths = null)
{
    public FileRef ToFileRef() => new(Root, Path);
}

/// <summary>Нарушен лимит (число записей, объём, глубина, длина пути).</summary>
public sealed class ArchiveLimitExceededException : FileSystemResolutionException
{
    public ArchiveLimitExceededException(string message) : base(message)
    {
    }

    public ArchiveLimitExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Предпроверка не уложилась в бюджет времени.</summary>
public sealed class ArchiveScanTimeoutException : FileSystemResolutionException
{
    public ArchiveScanTimeoutException(string message) : base(message)
    {
    }
}

/// <summary>Все слоты формирования архивов заняты.</summary>
public sealed class ArchiveBusyException : FileSystemResolutionException
{
    public ArchiveBusyException(string message) : base(message)
    {
    }
}

/// <summary>Состав symlink изменился после подтверждения пользователем.</summary>
public sealed class ArchiveSelectionChangedException : FileSystemResolutionException
{
    public ArchiveSelectionChangedException() : base("Состав символических ссылок изменился после подтверждения.")
    {
    }
}
