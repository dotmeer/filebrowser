namespace dotmeer.filebrowser.Tasks;

public sealed class FileTaskExecutor(CopyTaskExecutor copyExecutor, MoveTaskExecutor moveExecutor, DeleteTaskExecutor deleteExecutor) : IFileTaskExecutor
{
    public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken) => task.Type switch
    {
        FileTaskType.Copy => copyExecutor.ExecuteAsync(task, cancellationToken, shutdownToken),
        FileTaskType.Move => moveExecutor.ExecuteAsync(task, cancellationToken, shutdownToken),
        FileTaskType.Delete => deleteExecutor.ExecuteAsync(task, cancellationToken, shutdownToken),
        _ => Task.FromException(new TaskExecutorNotReadyException())
    };
}

public sealed class CopyTaskExecutor(FileTransferEngine transfer)
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
                artifact = await transfer.CreateTemporaryArtifactAsync(task, destination, entry.Name, cancellationToken);
                await transfer.CopyEntryAsync(entry, artifact.PhysicalPath, progress, cancellationToken);
                // Критическая секция: публикация и фиксация результата не прерываются отменой.
                FileTransferEngine.Publish(artifact.PhysicalPath, entry.FinalPhysicalPath, entry.SourcePath.IsDirectory);
                await transfer.RemoveArtifactAsync(artifact, CancellationToken.None);
                artifact = null;
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
}
