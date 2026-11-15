using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// Актуальные полномочия автора задачи. Используется worker-слоем и
/// HTTP-загрузкой на безопасных точках исполнения: актор перечитывается из
/// SQLite, поэтому отзыв прав или удаление учётной записи прекращают работу
/// до начала следующих изменений на файловой системе. Задача без автора
/// (ANONYMOUS или строка предыдущей схемы) исполняется системным доступом.
/// </summary>
public sealed class TaskAccessService(FileAccessService fileAccess)
{
    /// <summary>Задача без автора: анонимный режим и старые строки.</summary>
    public Task<FileActor> ResolveActorAsync(Guid? authorUserId, CancellationToken cancellationToken) =>
        authorUserId is { } userId
            ? fileAccess.CreateForTaskAsync(userId, cancellationToken)
            : Task.FromResult(FileActor.Unrestricted);

    /// <summary>
    /// Проверка перед стартом и на границе верхнеуровневых объектов: у автора
    /// должны оставаться права записи на все корни задачи.
    /// </summary>
    public async Task<FileActor> EnsureTaskCanStartAsync(FileTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var actor = await ResolveActorAsync(task.AuthorUserId, cancellationToken);
        EnsureRootsOrRevoke(actor, task);
        return actor;
    }

    /// <summary>
    /// Повторная проверка конкретного объекта и назначения перед следующим
    /// шагом. Закрытый служебный каталог и потеря права записи дают
    /// <see cref="FileTaskAccessRevokedException"/>, а не общую ошибку
    /// исполнения.
    /// </summary>
    public async Task EnsureEntryAsync(
        FileTask task,
        ResolvedPath source,
        ResolvedPath? destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(source);
        var actor = await ResolveActorAsync(task.AuthorUserId, cancellationToken);
        try
        {
            EnsureRoots(actor, task);
            actor.EnsureMutableBranch(source.Root, source.PhysicalPath);
            if (destination is not null)
            {
                actor.EnsureWritablePath(destination.Root, destination.PhysicalPath);
            }
        }
        catch (FileSystemAccessDeniedException)
        {
            throw new FileTaskAccessRevokedException();
        }
    }

    /// <summary>Проверка права записи в каталог назначения upload.</summary>
    public async Task EnsureUploadWritableAsync(
        Guid? authorUserId,
        string rootName,
        string parentPhysicalPath,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveActorAsync(authorUserId, cancellationToken);
        try
        {
            EnsureWritable(actor, rootName, parentPhysicalPath);
        }
        catch (FileSystemAccessDeniedException)
        {
            throw new FileTaskAccessRevokedException();
        }
    }

    /// <summary>
    /// Проверка для приёма тела upload, которая не ждёт следующего байта.
    /// Возвращает false, если автор потерял право записи в каталог назначения.
    /// </summary>
    public async Task<bool> CanContinueUploadAsync(
        Guid? authorUserId,
        string rootName,
        string parentPhysicalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await ResolveActorAsync(authorUserId, cancellationToken);
            EnsureWritable(actor, rootName, parentPhysicalPath);
            return true;
        }
        catch (FileSystemAccessDeniedException)
        {
            return false;
        }
    }

    private static void EnsureWritable(FileActor actor, string rootName, string parentPhysicalPath)
    {
        if (actor.IsFullAccess)
        {
            return;
        }

        actor.EnsureWritablePath(rootName, parentPhysicalPath);
    }

    private static void EnsureRootsOrRevoke(FileActor actor, FileTask task)
    {
        try
        {
            EnsureRoots(actor, task);
        }
        catch (FileSystemAccessDeniedException)
        {
            throw new FileTaskAccessRevokedException();
        }
    }

    private static void EnsureRoots(FileActor actor, FileTask task)
    {
        if (actor.IsFullAccess)
        {
            return;
        }

        foreach (var reference in task.Sources.Append(task.Destination))
        {
            if (!actor.CanWriteRoot(ResolveRootName(reference)))
            {
                throw new FileSystemAccessDeniedException("Недостаточно прав для доступа к файловому корню задачи.");
            }
        }
    }

    /// <summary>
    /// Публичная ссылка под AllRoots делегируется реальному корню;
    /// права проверяются по его имени, а не по имени виртуального корня.
    /// </summary>
    private static string ResolveRootName(FileRef reference) =>
        VirtualRoot.IsVirtualRoot(reference.Root)
            ? VirtualRoot.SplitPath(reference.Path).RootName
            : reference.Root;
}
