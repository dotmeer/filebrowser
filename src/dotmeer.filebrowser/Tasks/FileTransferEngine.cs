using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public class FileTransferEngine(PathResolver pathResolver, TaskRepository repository, TaskProgressService runtimeProgress, TaskMetrics? metrics = null)
{
    private const int BufferSize = 128 * 1024;

    internal FileTransferPlan BuildPlan(FileTask task)
    {
        var errors = new List<FileTransferPlanError>();
        ResolvedPath? destination = null;
        try
        {
            destination = pathResolver.ResolveExisting(task.Destination);
            if (!destination.IsDirectory)
            {
                errors.Add(new FileTransferPlanError(null, "Назначение не является каталогом."));
            }
        }
        catch (FileSystemResolutionException)
        {
            errors.Add(new FileTransferPlanError(null, "Не удалось открыть каталог назначения."));
        }

        var verb = task.Type == FileTaskType.Move ? "перемещать" : "копировать";
        var candidates = new List<FileTransferPlanEntry>();
        foreach (var source in task.Sources)
        {
            try
            {
                var resolved = pathResolver.ResolveExisting(source);
                if (string.IsNullOrEmpty(resolved.RelativePath))
                {
                    errors.Add(new FileTransferPlanError(source, $"{Format(source)}: корень нельзя {verb}."));
                    continue;
                }

                var name = Path.GetFileName(resolved.PhysicalPath);
                var scan = ScanObject(resolved.PhysicalPath);
                candidates.Add(new FileTransferPlanEntry(source, resolved, name, string.Empty, scan.Bytes, scan.Items));
            }
            catch (UnsupportedFileSystemObjectException)
            {
                errors.Add(new FileTransferPlanError(source, $"{Format(source)}: обнаружен неподдерживаемый объект или символическая ссылка."));
            }
            catch (FileSystemResolutionException)
            {
                errors.Add(new FileTransferPlanError(source, $"{Format(source)}: источник недоступен."));
            }
            catch (IOException)
            {
                errors.Add(new FileTransferPlanError(source, $"{Format(source)}: не удалось прочитать источник."));
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new FileTransferPlanError(source, $"{Format(source)}: нет доступа к источнику."));
            }
        }

        if (destination is not null && destination.IsDirectory && candidates.Count > 0)
        {
            var firstParent = GetParent(candidates[0].SourcePath);
            foreach (var candidate in candidates)
            {
                var parent = GetParent(candidate.SourcePath);
                if (!SamePath(firstParent, parent))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, $"{Format(candidate.Source)}: источники должны находиться в одной родительской папке."));
                }

                if (SamePath(parent, destination))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, $"{Format(candidate.Source)}: назначение совпадает с родительской папкой источников."));
                }

                if (SamePath(candidate.SourcePath, destination)
                    || (candidate.SourcePath.IsDirectory && PathInspection.IsWithin(candidate.SourcePath.PhysicalPath, destination.PhysicalPath)))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, $"{Format(candidate.Source)}: каталог назначения находится внутри источника или совпадает с ним."));
                }

                var finalPath = Path.Combine(destination.PhysicalPath, candidate.Name);
                if (PathInspection.Exists(finalPath))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, $"{Format(candidate.Source)}: name_conflict."));
                }

                candidate.FinalPhysicalPath = finalPath;
            }
        }

        return new FileTransferPlan(destination, candidates, errors);
    }

    internal async Task FailPreflightAsync(FileTask task, FileTransferPlan plan, CancellationToken cancellationToken)
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
                await repository.UpdateTaskItemAsync(task.Id, error.Source, "failed", error.Message, 0, null, cancellationToken);
            }
        }

        foreach (var source in task.Sources.Where(source => !sourcesWithSpecificError.Contains(source)))
        {
            await repository.UpdateTaskItemAsync(task.Id, source, "failed", "Предварительная проверка не пройдена.", 0, null, cancellationToken);
        }

        throw new FileTaskExecutionException($"Предварительная проверка не пройдена: {string.Join("; ", plan.Errors.Select(error => error.Message))}");
    }

    internal async Task<TransferProgress> StartRunningAsync(FileTask task, FileTransferPlan plan, CancellationToken cancellationToken)
    {
        await repository.UpdateProgressAsync(task.Id, 0, plan.TotalBytes, 0, plan.TotalItems, cancellationToken);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running", cancellationToken: cancellationToken);
        return new TransferProgress(repository, runtimeProgress, task.Id, task.Type, plan.TotalBytes, plan.TotalItems, metrics);
    }

    internal async Task<TransferArtifact> CreateTemporaryArtifactAsync(FileTask task, ResolvedPath destination, string sourceName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tempName = $".filebrowser-{task.Id:N}-{Guid.NewGuid():N}";
            var relativePath = string.IsNullOrEmpty(destination.RelativePath)
                ? tempName
                : $"{destination.RelativePath}/{tempName}";
            var physicalPath = Path.Combine(destination.PhysicalPath, tempName);
            if (PathInspection.Exists(physicalPath))
            {
                continue;
            }

            try
            {
                var registered = await repository.RegisterTemporaryArtifactAsync(task.Id, new FileRef(destination.Root, relativePath), cancellationToken);
                return new TransferArtifact(registered, physicalPath);
            }
            catch (Exception exception) when (exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                // A random name collision is harmless; choose another registered path.
            }
        }

        throw new FileTaskExecutionException($"{sourceName}: не удалось создать временный объект.");
    }

    internal virtual async Task CopyEntryAsync(
        FileTransferPlanEntry entry,
        string destinationPath,
        TransferProgress progress,
        CancellationToken cancellationToken)
    {
        if (entry.SourcePath.IsDirectory)
        {
            Directory.CreateDirectory(destinationPath);
            await CopyDirectoryAsync(entry.SourcePath.PhysicalPath, destinationPath, progress, cancellationToken);
        }
        else
        {
            await CopyFileAsync(entry.SourcePath.PhysicalPath, destinationPath, progress, cancellationToken);
        }
    }

    internal virtual bool TryRenameEntry(FileTransferPlanEntry entry)
    {
        try
        {
            Directory.Move(entry.SourcePath.PhysicalPath, entry.FinalPhysicalPath);
            return true;
        }
        catch (IOException exception) when (FileSystemRename.IsCrossDevice(exception))
        {
            return false;
        }
        catch (IOException exception) when (PathInspection.Exists(entry.FinalPhysicalPath))
        {
            throw new FileTaskExecutionException("name_conflict.", exception);
        }
    }

    internal virtual void DeleteSource(ResolvedPath sourcePath)
    {
        PathInspection.ThrowIfUnsupported(sourcePath.PhysicalPath);
        if (!sourcePath.IsDirectory)
        {
            File.Delete(sourcePath.PhysicalPath);
            return;
        }

        EnsureDirectoryTreeSupported(sourcePath.PhysicalPath);
        Directory.Delete(sourcePath.PhysicalPath, recursive: true);
    }

    internal async Task CleanupArtifactAsync(TransferArtifact artifact, CancellationToken cancellationToken)
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
        catch (IOException)
        {
            // Preserve the exact registration for a later safe cleanup attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the exact registration for a later safe cleanup attempt.
        }
    }

    internal Task RemoveArtifactAsync(TransferArtifact artifact, CancellationToken cancellationToken) =>
        repository.RemoveTemporaryArtifactAsync(artifact.Record.Id, cancellationToken);

    internal Task CompleteItemAsync(Guid taskId, FileTransferPlanEntry entry, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "completed", null, entry.ItemCount, entry.ItemCount, cancellationToken);

    internal Task FailItemAsync(Guid taskId, FileTransferPlanEntry entry, string error, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "failed", error, 0, entry.ItemCount, cancellationToken);

    internal async Task SkipRemainingAsync(Guid taskId, FileTransferPlan plan, int currentIndex, CancellationToken cancellationToken)
    {
        for (var remaining = currentIndex + 1; remaining < plan.Entries.Count; remaining++)
        {
            var pending = plan.Entries[remaining];
            await repository.UpdateTaskItemAsync(taskId, pending.Source, "not_processed", "Не обработан после ошибки предыдущего объекта.", 0, pending.ItemCount, cancellationToken);
        }
    }

    internal static void Publish(string temporaryPath, string finalPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.Move(temporaryPath, finalPath);
            }
            else
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
        }
        catch (IOException exception) when (PathInspection.Exists(finalPath))
        {
            throw new FileTaskExecutionException("name_conflict.", exception);
        }
    }

    internal static string Format(FileRef file) => $"{file.Root}/{file.Path}";

    internal static string ToSafeError(FileTaskType taskType, FileRef source, Exception exception)
    {
        var operation = taskType == FileTaskType.Move ? "перемещения" : "копирования";
        return exception switch
        {
            FileTaskExecutionException taskException => taskException.SafeMessage,
            FileSystemNameConflictException => $"{Format(source)}: name_conflict.",
            FileSystemResolutionException => $"{Format(source)}: файловая операция не выполнена.",
            UnauthorizedAccessException => $"{Format(source)}: нет доступа.",
            IOException => $"{Format(source)}: ошибка {operation}.",
            _ => $"{Format(source)}: ошибка {operation}."
        };
    }

    private static async Task CopyDirectoryAsync(string sourcePath, string destinationPath, TransferProgress progress, CancellationToken cancellationToken)
    {
        PathInspection.ThrowIfUnsupported(sourcePath);
        await progress.ItemCompletedAsync(cancellationToken);
        foreach (var childPath in Directory.EnumerateFileSystemEntries(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathInspection.ThrowIfUnsupported(childPath);
            var childDestination = Path.Combine(destinationPath, Path.GetFileName(childPath));
            if (Directory.Exists(childPath))
            {
                Directory.CreateDirectory(childDestination);
                await CopyDirectoryAsync(childPath, childDestination, progress, cancellationToken);
            }
            else
            {
                await CopyFileAsync(childPath, childDestination, progress, cancellationToken);
            }
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, TransferProgress progress, CancellationToken cancellationToken)
    {
        PathInspection.ThrowIfUnsupported(sourcePath);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await progress.BytesCopiedAsync(read, cancellationToken);
        }

        await progress.ItemCompletedAsync(cancellationToken);
    }

    private static ScanResult ScanObject(string path)
    {
        PathInspection.ThrowIfUnsupported(path);
        if (!Directory.Exists(path))
        {
            return new ScanResult(new FileInfo(path).Length, 1);
        }

        long bytes = 0;
        long items = 1;
        foreach (var childPath in Directory.EnumerateFileSystemEntries(path))
        {
            var child = ScanObject(childPath);
            bytes += child.Bytes;
            items += child.Items;
        }
        return new ScanResult(bytes, items);
    }

    private static void EnsureDirectoryTreeSupported(string directoryPath)
    {
        foreach (var childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            PathInspection.ThrowIfUnsupported(childPath);
            if (Directory.Exists(childPath))
            {
                EnsureDirectoryTreeSupported(childPath);
            }
        }
    }

    private static ResolvedPath GetParent(ResolvedPath path)
    {
        var separator = path.RelativePath.LastIndexOf('/');
        var parentRelativePath = separator < 0 ? string.Empty : path.RelativePath[..separator];
        var parentPhysicalPath = Path.GetDirectoryName(path.PhysicalPath)!;
        return new ResolvedPath(path.Root, parentRelativePath, parentPhysicalPath, true);
    }

    private static bool SamePath(ResolvedPath first, ResolvedPath second) =>
        first.Root.Equals(second.Root, StringComparison.OrdinalIgnoreCase)
        && first.RelativePath.Equals(second.RelativePath, StringComparison.Ordinal);

    private sealed record ScanResult(long Bytes, long Items);

    private sealed class FileRefComparer : IEqualityComparer<FileRef>
    {
        public static FileRefComparer Instance { get; } = new();

        public bool Equals(FileRef? x, FileRef? y) => x is not null && y is not null
            && x.Root.Equals(y.Root, StringComparison.OrdinalIgnoreCase)
            && x.Path.Equals(y.Path, StringComparison.Ordinal);

        public int GetHashCode(FileRef value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Root), StringComparer.Ordinal.GetHashCode(value.Path));
    }
}

internal sealed record FileTransferPlan(ResolvedPath? Destination, List<FileTransferPlanEntry> Entries, List<FileTransferPlanError> Errors)
{
    public long TotalBytes => Entries.Sum(entry => entry.Bytes);

    public long TotalItems => Entries.Sum(entry => entry.ItemCount);
}

internal sealed class FileTransferPlanEntry(
    FileRef source,
    ResolvedPath sourcePath,
    string name,
    string finalPhysicalPath,
    long bytes,
    long itemCount)
{
    public FileRef Source { get; } = source;
    public ResolvedPath SourcePath { get; } = sourcePath;
    public string Name { get; } = name;
    public string FinalPhysicalPath { get; set; } = finalPhysicalPath;
    public long Bytes { get; } = bytes;
    public long ItemCount { get; } = itemCount;
}

internal sealed record FileTransferPlanError(FileRef? Source, string Message);

internal sealed record TransferArtifact(TemporaryArtifact Record, string PhysicalPath);

internal sealed class TransferProgress(
    TaskRepository repository,
    TaskProgressService runtimeProgress,
    Guid taskId,
    FileTaskType taskType,
    long bytesTotal,
    long itemsTotal,
    TaskMetrics? metrics)
{
    private long _bytesDone;
    private long _itemsDone;
    private DateTimeOffset _lastPersisted = DateTimeOffset.MinValue;

    public async Task BytesCopiedAsync(int bytes, CancellationToken cancellationToken)
    {
        _bytesDone += bytes;
        runtimeProgress.Report(taskId, _bytesDone);
        metrics?.TaskBytesProcessed(taskType, bytes);
        await ReportAsync(force: false, cancellationToken);
    }

    public async Task ItemCompletedAsync(CancellationToken cancellationToken)
    {
        _itemsDone++;
        metrics?.TaskItemsProcessed(taskType, 1);
        await ReportAsync(force: false, cancellationToken);
    }

    public async Task ItemCompletedInstantlyAsync(long bytes, long items, CancellationToken cancellationToken)
    {
        _bytesDone += bytes;
        _itemsDone += items;
        metrics?.TaskBytesProcessed(taskType, bytes);
        metrics?.TaskItemsProcessed(taskType, items);
        await ReportAsync(force: true, cancellationToken);
    }

    public async Task ReportAsync(bool force, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastPersisted < TimeSpan.FromSeconds(1))
        {
            return;
        }

        await repository.UpdateProgressAsync(taskId, _bytesDone, bytesTotal, _itemsDone, itemsTotal, cancellationToken);
        _lastPersisted = now;
    }
}
