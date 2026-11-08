namespace dotmeer.filebrowser.Tasks;

public sealed class FileTaskExecutor(CopyTaskExecutor copyExecutor, MoveTaskExecutor moveExecutor) : IFileTaskExecutor
{
    public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken) => task.Type switch
    {
        FileTaskType.Copy => copyExecutor.ExecuteAsync(task, cancellationToken),
        FileTaskType.Move => moveExecutor.ExecuteAsync(task, cancellationToken),
        _ => Task.FromException(new TaskExecutorNotReadyException())
    };
}

public sealed class CopyTaskExecutor(FileTransferEngine transfer)
{
    public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken)
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
                FileTransferEngine.Publish(artifact.PhysicalPath, entry.FinalPhysicalPath, entry.SourcePath.IsDirectory);
                await transfer.RemoveArtifactAsync(artifact, cancellationToken);
                artifact = null;
                await transfer.CompleteItemAsync(task.Id, entry, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (artifact is not null)
                {
                    await transfer.CleanupArtifactAsync(artifact, cancellationToken);
                }

                var error = FileTransferEngine.ToSafeError(task.Type, entry.Source, exception);
                await transfer.FailItemAsync(task.Id, entry, error, cancellationToken);
                await transfer.SkipRemainingAsync(task.Id, plan, index, cancellationToken);
                await progress.ReportAsync(force: true, cancellationToken);
                throw new FileTaskExecutionException(error, exception);
            }
        }

        await progress.ReportAsync(force: true, cancellationToken);
    }
}
