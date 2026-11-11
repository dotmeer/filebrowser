using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Uploads;

/// <summary>
/// Приём файла из HTTP-запроса (SPEC §11, §13.10). Пишет байты во
/// временный артефакт <c>.filebrowser-*</c> в каталоге назначения и
/// публикует его под конечным именем только после полного приёма.
/// Жизненный цикл задачи типа <c>upload</c> ведёт вызывающий эндпоинт:
/// задача создаётся сразу активной и никогда не попадает в очередь.
/// </summary>
public sealed class UploadService(
    PathResolver pathResolver,
    TaskRepository repository,
    TaskProgressService runtimeProgress,
    FileBrowserConfiguration configuration,
    UploadMetrics? metrics = null)
{
    private const int BufferSize = 128 * 1024;
    private const int ArtifactNameAttempts = 10;

    /// <summary>
    /// Проверяет имя, родительский каталог, конфликт имени, лимит размера
    /// и свободное место. Задачу не создаёт и файлов не изменяет.
    /// </summary>
    public UploadPreflight Prepare(FileRef parent, string name, long? contentLength)
    {
        ArgumentNullException.ThrowIfNull(parent);

        // Отдельный вызов до ResolveNewChild: недопустимое имя должно
        // возвращаться как invalid_name, а не как общая ошибка пути.
        PathResolver.ValidateNewName(name);

        if (contentLength is < 0)
        {
            throw new InvalidFileSystemRequestException("Размер загружаемого файла недопустим.");
        }

        var maxFileBytes = configuration.Uploads.MaxFileBytes;
        if (contentLength > maxFileBytes)
        {
            throw new FileTooLargeException();
        }

        var finalPath = pathResolver.ResolveNewChild(parent, name);
        var parentPhysicalPath = Path.GetDirectoryName(finalPath.PhysicalPath)
            ?? throw new InvalidFileSystemRequestException("Не удалось определить каталог назначения.");

        if (contentLength is > 0)
        {
            EnsureFreeSpace(parentPhysicalPath, contentLength.Value);
        }

        return new UploadPreflight(
            new FileRef(finalPath.Root, GetParentPath(finalPath.RelativePath)),
            finalPath,
            parentPhysicalPath,
            contentLength);
    }

    /// <summary>
    /// Создаёт задачу <c>upload</c> в состоянии scanning и регистрирует
    /// временный артефакт до его появления на диске (SPEC §13.5).
    /// </summary>
    public async Task<UploadSession> StartAsync(UploadPreflight preflight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (preflight.ContentLength is null)
        {
            throw new InvalidOperationException("Для приёма файла требуется Content-Length.");
        }

        var task = await repository.CreateActiveAsync(
            FileTaskType.Upload,
            [preflight.Final],
            preflight.Parent,
            FileTaskStatus.Scanning,
            "scanning",
            preflight.ContentLength,
            1,
            cancellationToken);

        try
        {
            var parentRelativePath = preflight.Parent.Path;
            for (var attempt = 0; attempt < ArtifactNameAttempts; attempt++)
            {
                var temporaryName = $".filebrowser-{task.Id:N}-{Guid.NewGuid():N}";
                var relativePath = string.IsNullOrEmpty(parentRelativePath)
                    ? temporaryName
                    : $"{parentRelativePath}/{temporaryName}";
                var physicalPath = Path.Combine(preflight.ParentPhysicalPath, temporaryName);
                if (PathInspection.Exists(physicalPath))
                {
                    continue;
                }

                try
                {
                    var artifact = await repository.RegisterTemporaryArtifactAsync(
                        task.Id,
                        new FileRef(preflight.FinalPath.Root, relativePath),
                        cancellationToken);
                    return new UploadSession(task.Id, preflight, physicalPath, artifact.Id);
                }
                catch (Exception exception) when (exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    // Совпадение случайного имени безвредно: выберем другое.
                }
            }

            throw new FileTaskExecutionException(FileTaskErrorCodes.TemporaryArtifactFailed);
        }
        catch
        {
            await FailQuietlyAsync(task.Id, FileTaskErrorCodes.TemporaryArtifactFailed);
            throw;
        }
    }

    /// <summary>
    /// Потоково принимает тело запроса. Промежуточный прогресс сохраняется
    /// не чаще раза в секунду (SPEC §13.6).
    /// </summary>
    public async Task<long> ReceiveAsync(UploadSession session, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(body);

        var expectedLength = session.Preflight.ContentLength
            ?? throw new InvalidOperationException("Для приёма файла требуется Content-Length.");
        await repository.TransitionAsync(session.TaskId, FileTaskStatus.Running, "uploading", cancellationToken: cancellationToken);

        await using var destination = new FileStream(
            session.TemporaryPhysicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        var total = 0L;
        var lastPersisted = DateTimeOffset.UtcNow;
        var maxFileBytes = configuration.Uploads.MaxFileBytes;

        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxFileBytes)
            {
                throw new FileTooLargeException();
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            runtimeProgress.Report(session.TaskId, total);

            var now = DateTimeOffset.UtcNow;
            if (now - lastPersisted >= TimeSpan.FromSeconds(1))
            {
                await repository.UpdateProgressAsync(session.TaskId, total, expectedLength, 0, 1, cancellationToken);
                lastPersisted = now;
            }
        }

        await destination.FlushAsync(cancellationToken);
        await repository.UpdateProgressAsync(session.TaskId, total, expectedLength, 0, 1, CancellationToken.None);

        if (total != expectedLength)
        {
            throw new UploadIncompleteException();
        }

        return total;
    }

    /// <summary>
    /// Публикует временный файл под конечным именем без перезаписи.
    /// Секция «публикация → снятие регистрации → фиксация» отменой не
    /// прерывается (та же модель, что у copy/move, SPEC §13.4).
    /// </summary>
    public async Task PublishAsync(UploadSession session, long bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            File.Move(session.TemporaryPhysicalPath, session.Preflight.FinalPath.PhysicalPath, overwrite: false);
        }
        catch (IOException exception) when (PathInspection.Exists(session.Preflight.FinalPath.PhysicalPath))
        {
            throw new FileSystemNameConflictException("Объект с таким именем уже существует.", exception);
        }

        await repository.RemoveTemporaryArtifactAsync(session.ArtifactId, CancellationToken.None);
        await repository.UpdateTaskItemAsync(
            session.TaskId,
            session.Preflight.Final,
            "completed",
            null,
            1,
            1,
            CancellationToken.None);

        var current = await repository.GetAsync(session.TaskId, CancellationToken.None);
        if (current?.Status == FileTaskStatus.Cancelling)
        {
            // Отмена выиграла гонку после фактической публикации: результат
            // сохраняется, задача фиксируется как отменённая (SPEC §13.8).
            await TransitionQuietlyAsync(
                session.TaskId,
                FileTaskStatus.Cancelled,
                "cancelled",
                FileTaskErrorCodes.CancelledByUser);
            metrics?.UploadCancelled();
            return;
        }

        await repository.TransitionAsync(
            session.TaskId,
            FileTaskStatus.Completed,
            "completed",
            cancellationToken: CancellationToken.None);
        metrics?.UploadCompleted(bytes);
    }

    /// <summary>
    /// Удаляет временный файл и фиксирует неуспех. Вызывается на любой
    /// ошибке, обрыве соединения и отмене. Если временный файл удалить не
    /// удалось, регистрация сохраняется для очистки при следующем старте.
    /// </summary>
    public async Task AbortAsync(UploadSession session, UploadAbortReason reason, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(session);

        var temporaryStillExists = TryDeleteTemporary(session.TemporaryPhysicalPath);
        if (!temporaryStillExists)
        {
            await repository.RemoveTemporaryArtifactAsync(session.ArtifactId, CancellationToken.None);
        }

        var userCancelled = reason == UploadAbortReason.UserCancelled;
        await repository.UpdateTaskItemAsync(
            session.TaskId,
            session.Preflight.Final,
            userCancelled ? "not_processed" : "failed",
            errorCode,
            0,
            1,
            CancellationToken.None);

        var current = await repository.GetAsync(session.TaskId, CancellationToken.None);
        if (current is null)
        {
            RecordAbort(reason);
            return;
        }

        if (userCancelled || current.Status == FileTaskStatus.Cancelling)
        {
            if (current.Status is FileTaskStatus.Scanning or FileTaskStatus.Running)
            {
                // Отмена из панели задач обычно уже перевела задачу в
                // cancelling; прямой вызов (тест, гонка) доводит переход сам.
                await TransitionQuietlyAsync(session.TaskId, FileTaskStatus.Cancelling, "cancelling", null);
            }

            await TransitionQuietlyAsync(
                session.TaskId,
                FileTaskStatus.Cancelled,
                "cancelled",
                FileTaskErrorCodes.CancelledByUser);
        }
        else
        {
            await TransitionQuietlyAsync(session.TaskId, FileTaskStatus.Failed, "failed", errorCode);
        }

        RecordAbort(reason);
    }

    private void RecordAbort(UploadAbortReason reason)
    {
        switch (reason)
        {
            case UploadAbortReason.UserCancelled:
                metrics?.UploadCancelled();
                break;
            case UploadAbortReason.ClientDisconnected:
                metrics?.UploadAborted();
                break;
            default:
                metrics?.UploadFailed();
                break;
        }
    }

    private async Task FailQuietlyAsync(Guid taskId, string errorCode)
    {
        try
        {
            var current = await repository.GetAsync(taskId, CancellationToken.None);
            if (current is not null && FileTaskStateTransitions.IsAllowed(current.Status, FileTaskStatus.Failed))
            {
                await repository.TransitionAsync(taskId, FileTaskStatus.Failed, "failed", errorCode, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Задача уже могла быть финализирована; очистка артефакта при
            // старте всё равно обработает оставшуюся запись.
        }
    }

    private async Task TransitionQuietlyAsync(Guid taskId, FileTaskStatus status, string stage, string? errorCode)
    {
        var current = await repository.GetAsync(taskId, CancellationToken.None);
        if (current is null || !FileTaskStateTransitions.IsAllowed(current.Status, status))
        {
            return;
        }

        try
        {
            await repository.TransitionAsync(taskId, status, stage, errorCode, CancellationToken.None);
        }
        catch (InvalidFileTaskTransitionException)
        {
            // Гонка с другой финализацией: состояние уже изменено конкурентно.
        }
    }

    private static bool TryDeleteTemporary(string physicalPath)
    {
        try
        {
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }

            return PathInspection.Exists(physicalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void EnsureFreeSpace(string parentPhysicalPath, long requiredBytes)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(parentPhysicalPath);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DriveNotFoundException or ArgumentException)
        {
            // Advisory-проверка: если место определить нельзя, приём не блокируется.
            return;
        }

        if (available < requiredBytes)
        {
            throw new InsufficientSpaceException();
        }
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }
}

public sealed record UploadPreflight(
    FileRef Parent,
    ResolvedPath FinalPath,
    string ParentPhysicalPath,
    long? ContentLength)
{
    public FileRef Final => new(FinalPath.Root, FinalPath.RelativePath);
}

public sealed record UploadSession(
    Guid TaskId,
    UploadPreflight Preflight,
    string TemporaryPhysicalPath,
    long ArtifactId);

public enum UploadAbortReason
{
    UserCancelled,
    ClientDisconnected,
    Error
}

public sealed class FileTooLargeException()
    : FileSystemResolutionException("Файл превышает допустимый размер.");

public sealed class InsufficientSpaceException()
    : FileSystemResolutionException("Недостаточно свободного места в каталоге назначения.");

public sealed class UploadIncompleteException()
    : FileSystemResolutionException("Загрузка не завершена: получено меньше данных, чем заявлено.");
