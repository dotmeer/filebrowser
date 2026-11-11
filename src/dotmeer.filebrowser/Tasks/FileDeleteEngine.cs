using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// Двухфазная механика удаления (SPEC §9.5): предпроверка собирает все ошибки,
/// затем каждый объект атомарно переименовывается в зарегистрированный
/// временный артефакт и артефакт удаляется. Точка «удалено» — успешное
/// переименование: имя сразу освобождается, а физическое освобождение места
/// завершает очистка зарегистрированных артефактов при следующем старте.
/// </summary>
public class FileDeleteEngine(
    PathResolver pathResolver,
    TaskRepository repository,
    TaskProgressService runtimeProgress,
    ApplicationPaths applicationPaths,
    TaskMetrics? metrics = null)
{
    internal async Task<FileDeletePlan> BuildPlanAsync(FileTask task, CancellationToken cancellationToken)
    {
        var errors = new List<FileDeletePlanError>();
        var entries = new List<FileDeletePlanEntry>();
        var artifacts = await repository.GetTemporaryArtifactsAsync(cancellationToken);

        foreach (var source in task.Sources)
        {
            try
            {
                var resolved = pathResolver.ResolveExisting(source);
                if (string.IsNullOrEmpty(resolved.RelativePath))
                {
                    errors.Add(new FileDeletePlanError(source, FileTaskErrorCodes.RootAsSource));
                    continue;
                }

                var scan = ScanForDeletion(resolved, artifacts, cancellationToken);
                if (scan.Error is not null)
                {
                    errors.Add(new FileDeletePlanError(source, scan.Error));
                    continue;
                }

                entries.Add(new FileDeletePlanEntry(source, resolved, scan.Bytes, scan.Items));
            }
            catch (UnsupportedFileSystemObjectException)
            {
                errors.Add(new FileDeletePlanError(source, FileTaskErrorCodes.UnsupportedOrSymlink));
            }
            catch (FileSystemResolutionException)
            {
                errors.Add(new FileDeletePlanError(source, FileTaskErrorCodes.SourceUnavailable));
            }
            catch (IOException)
            {
                errors.Add(new FileDeletePlanError(source, FileTaskErrorCodes.SourceUnreadable));
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new FileDeletePlanError(source, FileTaskErrorCodes.SourceAccessDenied));
            }
        }

        return new FileDeletePlan(entries, errors);
    }

    internal async Task FailPreflightAsync(FileTask task, FileDeletePlan plan, CancellationToken cancellationToken)
    {
        if (plan.Errors.Count == 0)
        {
            return;
        }

        var sourcesWithSpecificError = plan.Errors
            .Where(error => error.Source is not null)
            .Select(error => error.Source!)
            .ToHashSet(FileRefComparer.Instance);
        foreach (var error in plan.Errors)
        {
            if (error.Source is not null)
            {
                await repository.UpdateTaskItemAsync(task.Id, error.Source, "failed", error.Code, 0, null, cancellationToken);
            }
        }

        foreach (var source in task.Sources.Where(source => !sourcesWithSpecificError.Contains(source)))
        {
            await repository.UpdateTaskItemAsync(task.Id, source, "failed", FileTaskErrorCodes.PreflightFailed, 0, null, cancellationToken);
        }

        throw new FileTaskExecutionException(FileTaskErrorCodes.PreflightFailed);
    }

    internal async Task<TransferProgress> StartRunningAsync(FileTask task, FileDeletePlan plan, CancellationToken cancellationToken)
    {
        // Байты для удаления не показательны: прогресс и процент считаются
        // по верхнеуровневым объектам (SPEC §13.9).
        await repository.UpdateProgressAsync(task.Id, 0, null, 0, plan.Entries.Count, cancellationToken);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running", cancellationToken: cancellationToken);
        return new TransferProgress(repository, runtimeProgress, task.Id, task.Type, null, plan.Entries.Count, metrics);
    }

    internal async Task<DeleteArtifact> RegisterArtifactAsync(FileTask task, FileDeletePlanEntry entry, CancellationToken cancellationToken)
    {
        var parentPhysicalPath = Path.GetDirectoryName(entry.SourcePath.PhysicalPath)!;
        var parentRelativePath = GetParentRelativePath(entry.SourcePath.RelativePath);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var temporaryName = $".filebrowser-{task.Id:N}-{Guid.NewGuid():N}";
            var relativePath = string.IsNullOrEmpty(parentRelativePath)
                ? temporaryName
                : $"{parentRelativePath}/{temporaryName}";
            var physicalPath = Path.Combine(parentPhysicalPath, temporaryName);
            if (PathInspection.Exists(physicalPath))
            {
                continue;
            }

            try
            {
                var record = await repository.RegisterTemporaryArtifactAsync(
                    task.Id,
                    new FileRef(entry.SourcePath.Root, relativePath),
                    cancellationToken);
                return new DeleteArtifact(record, physicalPath, entry.SourcePath.IsDirectory);
            }
            catch (Exception exception) when (exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                // Случайное совпадение имени безвредно: выбираем другое.
            }
        }

        throw new FileTaskExecutionException(FileTaskErrorCodes.TemporaryArtifactFailed);
    }

    /// <summary>
    /// Атомарная точка удаления: объект в той же родительской папке, поэтому
    /// переименование не копирует данные и освобождает публичное имя сразу.
    /// </summary>
    internal static void MoveIntoArtifact(DeleteArtifact artifact, ResolvedPath sourcePath)
    {
        if (sourcePath.IsDirectory)
        {
            Directory.Move(sourcePath.PhysicalPath, artifact.PhysicalPath);
        }
        else
        {
            File.Move(sourcePath.PhysicalPath, artifact.PhysicalPath, overwrite: false);
        }
    }

    internal virtual Task DeleteArtifactAsync(DeleteArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            if (artifact.IsDirectory)
            {
                foreach (var _ in FileSystemTreeWalker.EnumerateValidated(artifact.PhysicalPath, cancellationToken))
                {
                }

                Directory.Delete(artifact.PhysicalPath, recursive: true);
            }
            else
            {
                File.Delete(artifact.PhysicalPath);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FileDeleteIncompleteException(exception);
        }
    }

    internal Task RemoveArtifactAsync(DeleteArtifact artifact, CancellationToken cancellationToken) =>
        repository.RemoveTemporaryArtifactAsync(artifact.Record.Id, cancellationToken);

    /// <summary>
    /// Убирает артефакт, если переименование не состоялось: регистрация и
    /// физический остаток не должны переживать неудачную попытку.
    /// </summary>
    internal async Task CleanupArtifactAsync(DeleteArtifact artifact, CancellationToken cancellationToken)
    {
        var physicalPath = artifact.PhysicalPath;
        try
        {
            if (Directory.Exists(physicalPath))
            {
                Directory.Delete(physicalPath, recursive: true);
            }
            else if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }

            if (!PathInspection.Exists(physicalPath))
            {
                await repository.RemoveTemporaryArtifactAsync(artifact.Record.Id, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileSystemResolutionException)
        {
            // Регистрация сохраняется для безопасной очистки при следующем старте.
        }
    }

    internal Task CompleteItemAsync(Guid taskId, FileDeletePlanEntry entry, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "completed", null, entry.ItemCount, entry.ItemCount, cancellationToken);

    internal Task FailItemAsync(Guid taskId, FileDeletePlanEntry entry, string error, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "failed", error, 0, entry.ItemCount, cancellationToken);

    internal Task CancelItemAsync(Guid taskId, FileDeletePlanEntry entry, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "not_processed", FileTaskErrorCodes.CancelledByUser, 0, entry.ItemCount, cancellationToken);

    internal async Task SkipRemainingAsync(Guid taskId, FileDeletePlan plan, int currentIndex, string skipErrorCode, CancellationToken cancellationToken)
    {
        for (var remaining = currentIndex + 1; remaining < plan.Entries.Count; remaining++)
        {
            var pending = plan.Entries[remaining];
            await repository.UpdateTaskItemAsync(taskId, pending.Source, "not_processed", skipErrorCode, 0, pending.ItemCount, cancellationToken);
        }
    }

    private DeleteScan ScanForDeletion(ResolvedPath source, IReadOnlyList<TemporaryArtifact> artifacts, CancellationToken cancellationToken)
    {
        foreach (var protectedPath in ProtectedPaths())
        {
            if (PathInspection.IsWithin(source.PhysicalPath, protectedPath))
            {
                return new DeleteScan(0, 0, FileTaskErrorCodes.ServiceFileProtected);
            }
        }

        foreach (var artifact in artifacts)
        {
            if (ArtifactIsInside(source, artifact.File))
            {
                return new DeleteScan(0, 0, FileTaskErrorCodes.ArtifactInUse);
            }
        }

        long bytes = 0;
        long items = 0;
        string? error = null;
        foreach (var node in FileSystemTreeWalker.EnumerateValidated(source.PhysicalPath, cancellationToken))
        {
            items++;
            if (node.IsDirectory)
            {
                continue;
            }

            bytes += new FileInfo(node.Path).Length;
            if (error is null && IsReadOnly(node.Path))
            {
                error = FileTaskErrorCodes.ReadOnlyObject;
            }
        }

        return new DeleteScan(bytes, items, error);
    }

    private IEnumerable<string> ProtectedPaths()
    {
        yield return applicationPaths.ConfigPath;
        yield return applicationPaths.DatabasePath;
        yield return applicationPaths.DatabasePath + "-wal";
        yield return applicationPaths.DatabasePath + "-shm";
    }

    private static bool ArtifactIsInside(ResolvedPath source, FileRef artifactPath) =>
        artifactPath.Root.Equals(source.Root, StringComparison.OrdinalIgnoreCase)
        && (artifactPath.Path.Equals(source.RelativePath, StringComparison.Ordinal)
            || artifactPath.Path.StartsWith(source.RelativePath + "/", StringComparison.Ordinal));

    private static bool IsReadOnly(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FileSystemResolutionException("Не удалось прочитать атрибуты объекта.", exception);
        }
    }

    private static string GetParentRelativePath(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }
}

internal sealed record FileDeletePlan(IReadOnlyList<FileDeletePlanEntry> Entries, List<FileDeletePlanError> Errors);

internal sealed record FileDeletePlanEntry(FileRef Source, ResolvedPath SourcePath, long Bytes, long ItemCount);

internal sealed record FileDeletePlanError(FileRef? Source, string Code);

internal sealed record DeleteArtifact(TemporaryArtifact Record, string PhysicalPath, bool IsDirectory);

internal sealed record DeleteScan(long Bytes, long Items, string? Error);

/// <summary>
/// Объект уже переименован в артефакт, но физически не удалён. Данные скрыты
/// из пространства имён; регистрация артефакта сохраняется до очистки при старте.
/// </summary>
public sealed class FileDeleteIncompleteException(Exception innerException)
    : Exception("Объект скрыт, но не удалён полностью.", innerException);

internal sealed class FileRefComparer : IEqualityComparer<FileRef>
{
    public static FileRefComparer Instance { get; } = new();

    public bool Equals(FileRef? x, FileRef? y) => x is not null && y is not null
        && x.Root.Equals(y.Root, StringComparison.OrdinalIgnoreCase)
        && x.Path.Equals(y.Path, StringComparison.Ordinal);

    public int GetHashCode(FileRef value) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Root), StringComparer.Ordinal.GetHashCode(value.Path));
}
