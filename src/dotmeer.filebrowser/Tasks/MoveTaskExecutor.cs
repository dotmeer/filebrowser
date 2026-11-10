using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public sealed class MoveTaskExecutor(FileTransferEngine transfer)
{
    public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
    {
        var plan = transfer.BuildPlan(task, cancellationToken);
        await transfer.FailPreflightAsync(task, plan, cancellationToken);
        var progress = await transfer.StartRunningAsync(task, plan, cancellationToken);
        var destination = plan.Destination!;

        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            TransferArtifact? artifact = null;
            try
            {
                if (transfer.TryRenameEntry(entry))
                {
                    await progress.ItemCompletedInstantlyAsync(entry.Bytes, entry.ItemCount, CancellationToken.None);
                }
                else
                {
                    artifact = await transfer.CreateTemporaryArtifactAsync(task, destination, entry.Name, cancellationToken);
                    await transfer.CopyEntryAsync(entry, artifact.PhysicalPath, progress, cancellationToken);
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
