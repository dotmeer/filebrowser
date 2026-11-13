using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Tasks;
using System.Buffers;
using System.IO.Pipelines;

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
    TaskOperationGate operationGate,
    UploadMetrics? metrics = null)
{
    private const int BufferSize = 128 * 1024;
    private const int ArtifactNameAttempts = 10;

    internal Func<UploadPublishCheckpoint, Task>? PublishCheckpointAsync { get; set; }
    internal TaskOperationGate OperationGate => operationGate;

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
    public async Task<UploadSession> StartAsync(UploadPreflight preflight, CancellationToken cancellationToken, Guid? taskId = null)
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
            cancellationToken,
            taskId);

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
                catch (Microsoft.Data.Sqlite.SqliteException exception) when (SqliteErrors.IsUniqueConstraint(exception))
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
    public async Task<long> ReceiveAsync(
        UploadSession session,
        Stream body,
        CancellationToken cancellationToken,
        CancellationToken bodyReadCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(body);
        var reader = PipeReader.Create(body, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            return await ReceiveAsync(session, reader, cancellationToken, bodyReadCancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    /// <summary>
    /// Принимает тело через PipeReader. Пользовательская отмена прерывает
    /// ожидающее чтение, не отменяя HTTP-запрос; отменённый результат
    /// продвигается до передачи отмены вызывающему коду.
    /// </summary>
    public async Task<long> ReceiveAsync(
        UploadSession session,
        PipeReader body,
        CancellationToken cancellationToken,
        CancellationToken bodyReadCancellationToken = default)
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

        using var cancelPendingRead = cancellationToken.Register(static state =>
        {
            ((PipeReader)state!).CancelPendingRead();
        }, body);

        var buffer = new byte[BufferSize];
        var total = 0L;
        var lastPersisted = DateTimeOffset.UtcNow;
        var maxFileBytes = configuration.Uploads.MaxFileBytes;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await body.ReadAsync(bodyReadCancellationToken);
            var remaining = result.Buffer;
            var consumed = result.Buffer.Start;
            var examined = result.Buffer.End;
            try
            {
                if (result.IsCanceled)
                {
                    examined = consumed;
                    cancellationToken.ThrowIfCancellationRequested();
                    bodyReadCancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                while (!remaining.IsEmpty)
                {
                    var chunkLength = (int)Math.Min(remaining.Length, buffer.Length);
                    remaining.Slice(0, chunkLength).CopyTo(buffer);
                    remaining = remaining.Slice(chunkLength);
                    consumed = remaining.Start;

                    total += chunkLength;
                    if (total > maxFileBytes)
                    {
                        throw new FileTooLargeException();
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, chunkLength), cancellationToken);
                    runtimeProgress.Report(session.TaskId, total);

                    var now = DateTimeOffset.UtcNow;
                    if (now - lastPersisted >= TimeSpan.FromSeconds(1))
                    {
                        await repository.UpdateProgressAsync(session.TaskId, total, expectedLength, 0, 1, cancellationToken);
                        lastPersisted = now;
                    }
                }

                consumed = result.Buffer.End;
                if (result.IsCompleted)
                {
                    break;
                }
            }
            finally
            {
                body.AdvanceTo(consumed, examined);
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
    /// Публикует временный файл без перезаписи. Отмена, первой вошедшая
    /// в критическую секцию, не допускает финальный файл; если публикация
    /// уже произошла, файл и успешный элемент сохраняются.
    /// </summary>
    public async Task<UploadPublishOutcome> PublishAsync(UploadSession session, long bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (PublishCheckpointAsync is not null)
        {
            await PublishCheckpointAsync(UploadPublishCheckpoint.BeforeFileMove);
        }

        using (await operationGate.EnterAsync(session.TaskId, CancellationToken.None))
        {
            var current = await repository.GetAsync(session.TaskId, CancellationToken.None);
            if (current is null
                || current.Status is FileTaskStatus.Cancelling or FileTaskStatus.Cancelled)
            {
                // Отмена, первой вошедшая в gate, не допускает финальный файл.
                await AbortAsync(session, UploadAbortReason.UserCancelled, FileTaskErrorCodes.CancelledByUser);
                return UploadPublishOutcome.Cancelled;
            }

            try
            {
                File.Move(session.TemporaryPhysicalPath, session.Preflight.FinalPath.PhysicalPath, overwrite: false);
            }
            catch (IOException exception) when (PathInspection.Exists(session.Preflight.FinalPath.PhysicalPath))
            {
                throw new FileSystemNameConflictException("Объект с таким именем уже существует.", exception);
            }
        }

        if (PublishCheckpointAsync is not null)
        {
            await PublishCheckpointAsync(UploadPublishCheckpoint.AfterFileMove);
        }

        try
        {
            // Состояние публикации и регистрация артефакта меняются одной
            // короткой транзакцией после File.Move.
            var outcome = await repository.FinalizePublishedUploadAsync(
                session.TaskId,
                session.Preflight.Final,
                session.ArtifactId,
                CancellationToken.None);
            switch (outcome)
            {
                case UploadFinalizeOutcome.Completed:
                    metrics?.UploadCompleted(bytes);
                    return UploadPublishOutcome.Completed;
                case UploadFinalizeOutcome.Cancelled:
                    metrics?.UploadCancelled();
                    return UploadPublishOutcome.Cancelled;
                default:
                    throw new InvalidOperationException("Не удалось зафиксировать результат публикации.");
            }
        }
        catch (Exception exception) when (exception is not FileSystemNameConflictException)
        {
            metrics?.UploadFailed();
            await MarkPublishedFailureQuietlyAsync(session.TaskId);
            throw new UploadPublishedException(exception);
        }
    }

    /// <summary>
    /// Убирает временный артефакт и фиксирует неуспех до публикации.
    /// Опубликованный элемент не переписывается. Если временный файл удалить
    /// не удалось, регистрация сохраняется для очистки при следующем старте.
    /// </summary>
    public async Task AbortAsync(UploadSession session, UploadAbortReason reason, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(session);

        var current = await repository.GetAsync(session.TaskId, CancellationToken.None);
        if (current is null)
        {
            RecordAbort(reason);
            return;
        }

        if (current.Status is FileTaskStatus.Completed or FileTaskStatus.Cancelled
            or FileTaskStatus.Failed or FileTaskStatus.Interrupted)
        {
            // Повторная отмена/запоздалый обрыв не меняют уже финальный результат.
            return;
        }

        var itemCompleted = current.Items.Any(item =>
            item.Source == session.Preflight.Final && item.Status == "completed");

        var temporaryStillExists = TryDeleteTemporary(session.TemporaryPhysicalPath);
        if (!temporaryStillExists)
        {
            await repository.RemoveTemporaryArtifactAsync(session.ArtifactId, CancellationToken.None);
        }

        if (!itemCompleted)
        {
            var userCancelled = reason == UploadAbortReason.UserCancelled;
            await repository.UpdateTaskItemAsync(
                session.TaskId,
                session.Preflight.Final,
                userCancelled ? "not_processed" : "failed",
                errorCode,
                0,
                1,
                CancellationToken.None);
        }

        if (reason == UploadAbortReason.UserCancelled || current.Status == FileTaskStatus.Cancelling)
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

    private async Task MarkPublishedFailureQuietlyAsync(Guid taskId)
    {
        try
        {
            var current = await repository.GetAsync(taskId, CancellationToken.None);
            var item = current?.Items.FirstOrDefault();
            if (item is not null && item.Status != "completed")
            {
                await repository.UpdateTaskItemAsync(taskId, item.Source, "completed", null, 1, 1, CancellationToken.None);
            }

            current = await repository.GetAsync(taskId, CancellationToken.None);
            if (current is not null && FileTaskStateTransitions.IsAllowed(current.Status, FileTaskStatus.Failed))
            {
                await repository.TransitionAsync(
                    taskId,
                    FileTaskStatus.Failed,
                    "failed",
                    FileTaskErrorCodes.IoError,
                    CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // При недоступной SQLite регистрацию оставляем для очистки после
            // перевода активной задачи в interrupted при следующем запуске.
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
            available = FileSystemVolume.GetAvailableFreeSpace(parentPhysicalPath);
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

public enum UploadPublishOutcome
{
    Completed,
    Cancelled
}

internal enum UploadPublishCheckpoint
{
    BeforeFileMove,
    AfterFileMove
}

/// <summary>
/// Файл уже опубликован под конечным именем, но финализация задачи не
/// удалась. Вызывающий код не должен удалять опубликованный файл.
/// </summary>
public sealed class UploadPublishedException(Exception innerException)
    : Exception("Файл опубликован, но задача не финализирована.", innerException);

public sealed class FileTooLargeException()
    : FileSystemResolutionException("Файл превышает допустимый размер.");

public sealed class InsufficientSpaceException()
    : FileSystemResolutionException("Недостаточно свободного места в каталоге назначения.");

public sealed class UploadIncompleteException()
    : FileSystemResolutionException("Загрузка не завершена: получено меньше данных, чем заявлено.");
