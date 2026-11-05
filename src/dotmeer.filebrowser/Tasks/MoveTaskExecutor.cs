using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public sealed class MoveTaskExecutor(FileTransferEngine transfer)
{
    public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken)
    {
        var plan = transfer.BuildPlan(task);
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
                    await progress.ItemCompletedInstantlyAsync(entry.Bytes, entry.ItemCount, cancellationToken);
                }
                else
                {
                    artifact = await transfer.CreateTemporaryArtifactAsync(task, destination, entry.Name, cancellationToken);
                    await transfer.CopyEntryAsync(entry, artifact.PhysicalPath, progress, cancellationToken);
                    FileTransferEngine.Publish(artifact.PhysicalPath, entry.FinalPhysicalPath, entry.SourcePath.IsDirectory);
                    await transfer.RemoveArtifactAsync(artifact, cancellationToken);
                    artifact = null;
                    DeleteSourceAfterPublish(entry.Source, entry.SourcePath, cancellationToken);
                }

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

    private void DeleteSourceAfterPublish(
        FileRef source,
        ResolvedPath sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            transfer.DeleteSource(sourcePath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = $"{FileTransferEngine.Format(source)}: назначение создано, но источник не удалён. " +
                        "Источник мог сохраниться полностью или частично.";
            throw new FileTaskExecutionException(error, exception);
        }
    }
}
