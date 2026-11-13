using dotmeer.filebrowser.FileSystem;
using Microsoft.Data.Sqlite;

namespace dotmeer.filebrowser.Tasks;

public class FileTransferEngine(PathResolver pathResolver, TaskRepository repository, TaskProgressService runtimeProgress, TaskMetrics? metrics = null)
{
    private const int BufferSize = 128 * 1024;

    internal FileTransferPlan BuildPlan(FileTask task, CancellationToken cancellationToken)
    {
        var errors = new List<FileTransferPlanError>();
        ResolvedPath? destination = null;
        try
        {
            destination = pathResolver.ResolveExisting(task.Destination);
            if (!destination.IsDirectory)
            {
                errors.Add(new FileTransferPlanError(null, FileTaskErrorCodes.DestinationNotDirectory));
            }
        }
        catch (FileSystemResolutionException)
        {
            errors.Add(new FileTransferPlanError(null, FileTaskErrorCodes.DestinationUnavailable));
        }

        var candidates = new List<FileTransferPlanEntry>();
        foreach (var source in task.Sources)
        {
            try
            {
                var resolved = pathResolver.ResolveExisting(source);
                if (string.IsNullOrEmpty(resolved.RelativePath))
                {
                    errors.Add(new FileTransferPlanError(source, FileTaskErrorCodes.RootAsSource));
                    continue;
                }

                var name = Path.GetFileName(resolved.PhysicalPath);
                var scan = ScanObject(resolved.PhysicalPath, cancellationToken);
                candidates.Add(new FileTransferPlanEntry(source, resolved, name, string.Empty, scan.Bytes, scan.Items));
            }
            catch (UnsupportedFileSystemObjectException)
            {
                errors.Add(new FileTransferPlanError(source, FileTaskErrorCodes.UnsupportedOrSymlink));
            }
            catch (FileSystemResolutionException)
            {
                errors.Add(new FileTransferPlanError(source, FileTaskErrorCodes.SourceUnavailable));
            }
            catch (IOException)
            {
                errors.Add(new FileTransferPlanError(source, FileTaskErrorCodes.SourceUnreadable));
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new FileTransferPlanError(source, FileTaskErrorCodes.SourceAccessDenied));
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
                    errors.Add(new FileTransferPlanError(candidate.Source, FileTaskErrorCodes.SourcesNotSameParent));
                }

                if (SamePath(parent, destination))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, FileTaskErrorCodes.DestinationEqualsSourceParent));
                }

                if (SamePath(candidate.SourcePath, destination)
                    || (candidate.SourcePath.IsDirectory && PathInspection.IsWithin(candidate.SourcePath.PhysicalPath, destination.PhysicalPath)))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, FileTaskErrorCodes.DestinationInsideSource));
                }

                var finalPath = Path.Combine(destination.PhysicalPath, candidate.Name);
                if (PathInspection.Exists(finalPath))
                {
                    errors.Add(new FileTransferPlanError(candidate.Source, FileTaskErrorCodes.NameConflict));
                }

                candidate.FinalPhysicalPath = finalPath;
            }

            var totalBytes = candidates.Sum(candidate => candidate.Bytes);
            if (totalBytes > 0 && IsFreeSpaceCheckRequired(task, candidates[0].SourcePath, destination))
            {
                long availableFreeSpace;
                try
                {
                    availableFreeSpace = GetAvailableFreeSpace(destination.PhysicalPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DriveNotFoundException)
                {
                    // Advisory-проверка: если место определить нельзя, операция не блокируется.
                    availableFreeSpace = -1;
                }

                if (availableFreeSpace >= 0 && availableFreeSpace < totalBytes)
                {
                    errors.Add(new FileTransferPlanError(null, FileTaskErrorCodes.InsufficientSpace));
                }
            }
        }

        return new FileTransferPlan(destination, candidates, errors);
    }

    /// <summary>
    /// Copy всегда требует места в назначении. Move — только при переносе
    /// между томами: rename внутри тома места не занимает.
    /// </summary>
    internal virtual bool IsFreeSpaceCheckRequired(FileTask task, ResolvedPath source, ResolvedPath destination) =>
        task.Type == FileTaskType.Copy
        || !FileSystemVolume.AreOnSameVolume(source.PhysicalPath, destination.PhysicalPath);

    internal virtual long GetAvailableFreeSpace(string physicalPath) =>
        FileSystemVolume.GetAvailableFreeSpace(physicalPath);

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
                await repository.UpdateTaskItemAsync(task.Id, error.Source, "failed", error.Code, 0, null, cancellationToken);
            }
        }

        foreach (var source in task.Sources.Where(source => !sourcesWithSpecificError.Contains(source)))
        {
            await repository.UpdateTaskItemAsync(task.Id, source, "failed", FileTaskErrorCodes.PreflightFailed, 0, null, cancellationToken);
        }

        throw new FileTaskExecutionException(FileTaskErrorCodes.PreflightFailed);
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
            catch (SqliteException exception) when (SqliteErrors.IsUniqueConstraint(exception))
            {
                // A random name collision is harmless; choose another registered path.
            }
        }

        throw new FileTaskExecutionException(FileTaskErrorCodes.TemporaryArtifactFailed);
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
            throw new FileTaskExecutionException(FileTaskErrorCodes.NameConflict, exception);
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

    internal Task CancelItemAsync(Guid taskId, FileTransferPlanEntry entry, CancellationToken cancellationToken) =>
        repository.UpdateTaskItemAsync(taskId, entry.Source, "not_processed", FileTaskErrorCodes.CancelledByUser, 0, entry.ItemCount, cancellationToken);

    internal async Task SkipRemainingAsync(Guid taskId, FileTransferPlan plan, int currentIndex, string skipErrorCode, CancellationToken cancellationToken)
    {
        for (var remaining = currentIndex + 1; remaining < plan.Entries.Count; remaining++)
        {
            var pending = plan.Entries[remaining];
            await repository.UpdateTaskItemAsync(taskId, pending.Source, "not_processed", skipErrorCode, 0, pending.ItemCount, cancellationToken);
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
            throw new FileTaskExecutionException(FileTaskErrorCodes.NameConflict, exception);
        }
    }

    /// <summary>
    /// Maps an execution exception to a stable error code persisted in
    /// task_items.error and tasks.error. Never produces user-facing text:
    /// presentation localizes known codes and passes legacy text through.
    /// </summary>
    internal static string ToErrorCode(Exception exception) =>
        exception switch
        {
            FileTaskExecutionException taskException => taskException.SafeCode,
            FileSystemNameConflictException => FileTaskErrorCodes.NameConflict,
            FileSystemResolutionException => FileTaskErrorCodes.OperationFailed,
            UnauthorizedAccessException => FileTaskErrorCodes.OperationAccessDenied,
            IOException => FileTaskErrorCodes.IoError,
            _ => FileTaskErrorCodes.IoError,
        };

    private static async Task CopyDirectoryAsync(string sourcePath, string destinationPath, TransferProgress progress, CancellationToken cancellationToken)
    {
        foreach (var node in FileSystemTreeWalker.EnumerateValidated(sourcePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourcePath, node.Path);
            var nodeDestinationPath = relativePath == "."
                ? destinationPath
                : Path.Combine(destinationPath, relativePath);
            if (node.IsDirectory)
            {
                if (relativePath != ".")
                {
                    Directory.CreateDirectory(nodeDestinationPath);
                }

                await progress.ItemCompletedAsync(cancellationToken);
            }
            else
            {
                await CopyFileAsync(node.Path, nodeDestinationPath, progress, cancellationToken);
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

    private static ScanResult ScanObject(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        long items = 0;
        foreach (var node in FileSystemTreeWalker.EnumerateValidated(path, cancellationToken))
        {
            items++;
            if (!node.IsDirectory)
            {
                bytes += new FileInfo(node.Path).Length;
            }
        }

        return new ScanResult(bytes, items);
    }

    private static void EnsureDirectoryTreeSupported(string directoryPath)
    {
        foreach (var _ in FileSystemTreeWalker.EnumerateValidated(directoryPath))
        {
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

internal sealed record FileTransferPlanError(FileRef? Source, string Code);

internal sealed record TransferArtifact(TemporaryArtifact Record, string PhysicalPath);

internal sealed class TransferProgress(
    TaskRepository repository,
    TaskProgressService runtimeProgress,
    Guid taskId,
    FileTaskType taskType,
    long? bytesTotal,
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
