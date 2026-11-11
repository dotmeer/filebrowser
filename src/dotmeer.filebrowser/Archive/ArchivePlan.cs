using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Archive;

/// <summary>Запись будущего архива: путь считается относительно базовой папки.</summary>
public sealed record ArchiveEntry(string Name, string PhysicalPath, bool IsDirectory, long Size, DateTimeOffset ModifiedAtUtc);

/// <summary>
/// Результат предпроверки: имя архива, отсортированный список записей и
/// суммарный объём. Манифест существует только в памяти и не кешируется
/// между предпроверкой UI и предпроверкой эндпоинта.
/// </summary>
public sealed record ArchivePlan(string ArchiveName, IReadOnlyList<ArchiveEntry> Entries, long TotalBytes);

/// <summary>
/// Выбор для скачивания архивом: базовая папка и имена её детей
/// (пустой список — все дети, то есть скачивание папки целиком).
/// </summary>
public sealed record ArchiveSelection(string Root, string Path, IReadOnlyList<string> Names)
{
    public FileRef ToFileRef() => new(Root, Path);
}

/// <summary>Нарушен лимит SPEC §12.2 (число записей, объём, глубина, длина пути).</summary>
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
