namespace dotmeer.filebrowser.Tasks;

public sealed class DeleteTaskExecutor(FileDeleteEngine engine)
{
    public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
    {
        var plan = await engine.BuildPlanAsync(task, cancellationToken);
        await engine.FailPreflightAsync(task, plan, cancellationToken);
        var progress = await engine.StartRunningAsync(task, plan, cancellationToken);

        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            DeleteArtifact? artifact = null;
            try
            {
                // Отмена проверяется только на границе верхнеуровневых объектов
                // (SPEC §13.9): переименование и удаление артефакта — критическая
                // секция, как публикация и удаление источника в move (D080).
                cancellationToken.ThrowIfCancellationRequested();
                artifact = await engine.RegisterArtifactAsync(task, entry, cancellationToken);
                FileDeleteEngine.MoveIntoArtifact(artifact, entry.SourcePath);
                await engine.DeleteArtifactAsync(artifact, CancellationToken.None);
                await engine.RemoveArtifactAsync(artifact, CancellationToken.None);
                await engine.CompleteItemAsync(task.Id, entry, CancellationToken.None);
                // Прогресс задачи — по верхнеуровневым объектам (§13.9);
                // счётчик вложенных узлов хранится в task_items.
                await progress.ItemCompletedInstantlyAsync(entry.Bytes, 1, CancellationToken.None);
            }
            catch (FileDeleteIncompleteException exception)
            {
                await engine.FailItemAsync(task.Id, entry, FileTaskErrorCodes.DeleteIncomplete, CancellationToken.None);
                await engine.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.SkippedAfterError, CancellationToken.None);
                await progress.ReportAsync(force: true, CancellationToken.None);
                throw new FileTaskExecutionException(FileTaskErrorCodes.DeleteIncomplete, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (artifact is not null)
                {
                    await engine.CleanupArtifactAsync(artifact, cancellationToken);
                }

                var error = FileTransferEngine.ToErrorCode(exception);
                await engine.FailItemAsync(task.Id, entry, error, cancellationToken);
                await engine.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.SkippedAfterError, cancellationToken);
                await progress.ReportAsync(force: true, cancellationToken);
                throw new FileTaskExecutionException(error, exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (shutdownToken.IsCancellationRequested)
                {
                    // Остановка приложения: регистрация артефакта остаётся для
                    // очистки при следующем старте (поведение до I27).
                    throw;
                }

                if (artifact is not null)
                {
                    await engine.CleanupArtifactAsync(artifact, CancellationToken.None);
                }

                await engine.CancelItemAsync(task.Id, entry, CancellationToken.None);
                await engine.SkipRemainingAsync(task.Id, plan, index, FileTaskErrorCodes.SkippedAfterCancel, CancellationToken.None);
                await progress.ReportAsync(force: true, CancellationToken.None);
                throw new FileTaskCancelledException();
            }
        }

        await progress.ReportAsync(force: true, cancellationToken);
    }
}
