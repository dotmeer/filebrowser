using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public enum FileTaskType
{
    Copy,
    Move
}

public enum FileTaskStatus
{
    Queued,
    Scanning,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public static class FileTaskStateTransitions
{
    public static bool IsAllowed(FileTaskStatus from, FileTaskStatus to) => (from, to) switch
    {
        (FileTaskStatus.Queued, FileTaskStatus.Scanning or FileTaskStatus.Failed or FileTaskStatus.Cancelled or FileTaskStatus.Interrupted) => true,
        (FileTaskStatus.Scanning, FileTaskStatus.Running or FileTaskStatus.Failed or FileTaskStatus.Cancelling or FileTaskStatus.Interrupted) => true,
        (FileTaskStatus.Running, FileTaskStatus.Completed or FileTaskStatus.Failed or FileTaskStatus.Cancelling or FileTaskStatus.Interrupted) => true,
        (FileTaskStatus.Cancelling, FileTaskStatus.Cancelled or FileTaskStatus.Failed or FileTaskStatus.Interrupted) => true,
        _ => false
    };

    public static bool IsActive(FileTaskStatus status) => status is FileTaskStatus.Queued or FileTaskStatus.Scanning or FileTaskStatus.Running or FileTaskStatus.Cancelling;
}

public sealed record FileTask(
    Guid Id,
    FileTaskType Type,
    IReadOnlyList<FileRef> Sources,
    IReadOnlyList<TaskItem> Items,
    FileRef Destination,
    FileTaskStatus Status,
    string Stage,
    long BytesDone,
    long? BytesTotal,
    long ItemsDone,
    long? ItemsTotal,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record TaskItem(
    long Id,
    Guid TaskId,
    FileRef Source,
    string Status,
    string? Error,
    long ItemsDone,
    long? ItemsTotal);

public sealed record TemporaryArtifact(long Id, Guid TaskId, FileRef File, DateTimeOffset CreatedAtUtc);

public sealed record TaskPage(IReadOnlyList<FileTask> Tasks, int Offset, int Limit, bool HasMore);

public sealed class InvalidFileTaskTransitionException(FileTaskStatus from, FileTaskStatus to)
    : InvalidOperationException($"Недопустимый переход состояния задачи: {from} -> {to}.");

public sealed class FileTaskQueueFullException : Exception
{
    public FileTaskQueueFullException() : base("Очередь задач заполнена.")
    {
    }
}

public sealed class FileTaskNotFoundException : Exception
{
    public FileTaskNotFoundException() : base("Задача не найдена.")
    {
    }
}
