using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public sealed class MoveTaskExecutor(FileTransferEngine transfer, TaskAccessService? accessService = null)
{
    public async Task ExecuteAsync(
        FileTask task,
        CancellationToken cancellationToken,
        CancellationToken shutdownToken,
        FileActor? actor = null)
    {
        var plan = transfer.BuildPlan(task, cancellationToken, actor);
        await transfer.FailPreflightAsync(task, plan, cancellationToken);
        var progress = await transfer.StartRunningAsync(task, plan, cancellationToken);
        var destination = plan.Destination!;

        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            TransferArtifact? artifact = null;
            try
            {
                // Права автора перечитываются до начала следующего объекта.
                if (accessService is not null)
                {
                    await accessService.EnsureEntryAsync(task, entry.SourcePath, destination, cancellationToken);
                }

                if (transfer.TryRenameEntry(entry))
                {
                    await progress.ItemCompletedInstantlyAsync(entry.Bytes, entry.ItemCount, CancellationToken.None);
                }
                else
                {
                    artifact = await transfer.CreateTemporaryArtifactAsync(task, destination, entry.Name, cancellationToken);
                    await transfer.CopyEntryAsync(entry, artifact.PhysicalPath, progress, cancellationToken);
                    // Проверка перед публикацией: отзыв прав оставляет конечное
                    // имя свободным, источник цел, временный объект удаляется.
                    if (accessService is not null)
                    {
                        await accessService.EnsureEntryAsync(task, entry.SourcePath, destination, cancellationToken);
                    }

                    // Критическая секция: публикация, снятие регистрации и удаление
                    // источника не прерываются отменой.
                    FileTransferEngine.Publish(artifact.PhysicalPath, entry.FinalPhysicalPath, entry.SourcePath.IsDirectory);
                    await transfer.RemoveArtifactAsync(artifact, CancellationToken.None);
                    artifact = null;
                    DeleteSourceAfterPublish(entry.SourcePath);
                    await progress.ItemCompletedInstantlyAsync(entry.Bytes, entry.ItemCount, CancellationToken.None);
                }

                await transfer.CompleteItemAsync(task.Id, entry, CancellationToken.None);
            }
            catch (FileTaskAccessRevokedException)
            {
                if (artifact is not null)
                {
                    await transfer.CleanupArtifactAsync(artifact, CancellationToken.None);
                }

                await transfer.RevokeItemAsync(task.Id, entry, CancellationToken.None);
                await transfer.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.AccessDenied, CancellationToken.None);
                await progress.ReportAsync(force: true, CancellationToken.None);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (artifact is not null)
                {
                    await transfer.CleanupArtifactAsync(artifact, cancellationToken);
                }

                var error = FileTransferEngine.ToErrorCode(exception);
                await transfer.FailItemAsync(task.Id, entry, error, cancellationToken);
                await transfer.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.SkippedAfterError, cancellationToken);
                await progress.ReportAsync(force: true, cancellationToken);
                throw new FileTaskExecutionException(error, exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (shutdownToken.IsCancellationRequested)
                {
                    // Остановка приложения: артефакт остаётся зарегистрированным
                    // для очистки при следующем старте.
                    throw;
                }

                if (artifact is not null)
                {
                    await transfer.CleanupArtifactAsync(artifact, CancellationToken.None);
                }

                await transfer.CancelItemAsync(task.Id, entry, CancellationToken.None);
                await transfer.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.SkippedAfterCancel, CancellationToken.None);
                await progress.ReportAsync(force: true, CancellationToken.None);
                throw new FileTaskCancelledException();
            }
        }

        await progress.ReportAsync(force: true, cancellationToken);
    }

    private void DeleteSourceAfterPublish(ResolvedPath sourcePath)
    {
        try
        {
            transfer.DeleteSource(sourcePath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FileTaskExecutionException(FileTaskErrorCodes.MoveSourceNotDeleted, exception);
        }
    }
}
