using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public sealed class FileTaskExecutor(CopyTaskExecutor copyExecutor) : IFileTaskExecutor
{
    public Task ExecuteAsync(FileTask task, CancellationToken cancellationToken) => task.Type switch
    {
        FileTaskType.Copy => copyExecutor.ExecuteAsync(task, cancellationToken),
        _ => Task.FromException(new TaskExecutorNotReadyException())
    };
}

public sealed class CopyTaskExecutor(PathResolver pathResolver, TaskRepository repository, TaskProgressService runtimeProgress)
{
    private const int BufferSize = 128 * 1024;

    public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken)
    {
        var plan = BuildPlan(task);
        if (plan.Errors.Count > 0)
        {
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

        await repository.UpdateProgressAsync(task.Id, 0, plan.TotalBytes, 0, plan.TotalItems, cancellationToken);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running", cancellationToken: cancellationToken);
        var progress = new CopyProgress(repository, runtimeProgress, task.Id, plan.TotalBytes, plan.TotalItems);
        var destination = plan.Destination!;

        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            CopyArtifact? artifact = null;
            try
            {
                artifact = await CreateTemporaryArtifactAsync(task, destination, entry.Name, cancellationToken);
                if (entry.SourcePath.IsDirectory)
                {
                    Directory.CreateDirectory(artifact.PhysicalPath);
                    await CopyDirectoryAsync(entry.SourcePath.PhysicalPath, artifact.PhysicalPath, progress, cancellationToken);
                }
                else
                {
                    await CopyFileAsync(entry.SourcePath.PhysicalPath, artifact.PhysicalPath, progress, cancellationToken);
                }

                Publish(artifact.PhysicalPath, entry.FinalPhysicalPath, entry.SourcePath.IsDirectory);
                await repository.RemoveTemporaryArtifactAsync(artifact.Record.Id, cancellationToken);
                artifact = null;
                await repository.UpdateTaskItemAsync(task.Id, entry.Source, "completed", null, entry.ItemCount, entry.ItemCount, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (artifact is not null)
                {
                    await CleanupArtifactAsync(artifact, cancellationToken);
                }

                var error = ToSafeError(entry.Source, exception);
                await repository.UpdateTaskItemAsync(task.Id, entry.Source, "failed", error, 0, entry.ItemCount, cancellationToken);
                for (var remaining = index + 1; remaining < plan.Entries.Count; remaining++)
                {
                    var pending = plan.Entries[remaining];
                    await repository.UpdateTaskItemAsync(task.Id, pending.Source, "not_processed", "Не обработан после ошибки предыдущего объекта.", 0, pending.ItemCount, cancellationToken);
                }

                await progress.ReportAsync(force: true, cancellationToken);
                throw new FileTaskExecutionException(error, exception);
            }
        }

        await progress.ReportAsync(force: true, cancellationToken);
    }

    private CopyPlan BuildPlan(FileTask task)
    {
        var errors = new List<CopyPlanError>();
        ResolvedPath? destination = null;
        try
        {
            destination = pathResolver.ResolveExisting(task.Destination);
            if (!destination.IsDirectory)
            {
                errors.Add(new CopyPlanError(null, "Назначение не является каталогом."));
            }
        }
        catch (FileSystemResolutionException)
        {
            errors.Add(new CopyPlanError(null, "Не удалось открыть каталог назначения."));
        }

        var candidates = new List<CopyPlanEntry>();
        foreach (var source in task.Sources)
        {
            try
            {
                var resolved = pathResolver.ResolveExisting(source);
                if (string.IsNullOrEmpty(resolved.RelativePath))
                {
                    errors.Add(new CopyPlanError(source, $"{Format(source)}: корень нельзя копировать."));
                    continue;
                }

                var name = Path.GetFileName(resolved.PhysicalPath);
                var scan = ScanObject(resolved.PhysicalPath);
                candidates.Add(new CopyPlanEntry(source, resolved, name, string.Empty, scan.Bytes, scan.Items));
            }
            catch (UnsupportedFileSystemObjectException)
            {
                errors.Add(new CopyPlanError(source, $"{Format(source)}: обнаружен неподдерживаемый объект или символическая ссылка."));
            }
            catch (FileSystemResolutionException)
            {
                errors.Add(new CopyPlanError(source, $"{Format(source)}: источник недоступен."));
            }
            catch (IOException)
            {
                errors.Add(new CopyPlanError(source, $"{Format(source)}: не удалось прочитать источник."));
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add(new CopyPlanError(source, $"{Format(source)}: нет доступа к источнику."));
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
                    errors.Add(new CopyPlanError(candidate.Source, $"{Format(candidate.Source)}: источники должны находиться в одной родительской папке."));
                }

                if (SamePath(parent, destination))
                {
                    errors.Add(new CopyPlanError(candidate.Source, $"{Format(candidate.Source)}: назначение совпадает с родительской папкой источников."));
                }

                if (SamePath(candidate.SourcePath, destination)
                    || (candidate.SourcePath.IsDirectory && PathInspection.IsWithin(candidate.SourcePath.PhysicalPath, destination.PhysicalPath)))
                {
                    errors.Add(new CopyPlanError(candidate.Source, $"{Format(candidate.Source)}: каталог назначения находится внутри источника или совпадает с ним."));
                }

                var finalPath = Path.Combine(destination.PhysicalPath, candidate.Name);
                if (PathInspection.Exists(finalPath))
                {
                    errors.Add(new CopyPlanError(candidate.Source, $"{Format(candidate.Source)}: name_conflict."));
                }

                candidate.FinalPhysicalPath = finalPath;
            }
        }

        return new CopyPlan(destination, candidates, errors);
    }

    private async Task<CopyArtifact> CreateTemporaryArtifactAsync(FileTask task, ResolvedPath destination, string sourceName, CancellationToken cancellationToken)
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
                return new CopyArtifact(registered, physicalPath);
            }
            catch (Exception exception) when (exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                // A random name collision is harmless; choose another registered path.
            }
        }

        throw new FileTaskExecutionException($"{sourceName}: не удалось создать временный объект.");
    }

    private async Task CopyDirectoryAsync(string sourcePath, string destinationPath, CopyProgress progress, CancellationToken cancellationToken)
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

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CopyProgress progress, CancellationToken cancellationToken)
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

    private static void Publish(string temporaryPath, string finalPath, bool isDirectory)
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

    private async Task CleanupArtifactAsync(CopyArtifact artifact, CancellationToken cancellationToken)
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

    private static string Format(FileRef file) => $"{file.Root}/{file.Path}";

    private static string ToSafeError(FileRef source, Exception exception) => exception switch
    {
        FileTaskExecutionException taskException => taskException.SafeMessage,
        FileSystemNameConflictException => $"{Format(source)}: name_conflict.",
        FileSystemResolutionException => $"{Format(source)}: файловая операция не выполнена.",
        UnauthorizedAccessException => $"{Format(source)}: нет доступа.",
        IOException => $"{Format(source)}: ошибка копирования.",
        _ => $"{Format(source)}: ошибка копирования."
    };

    private sealed record CopyPlan(ResolvedPath? Destination, List<CopyPlanEntry> Entries, List<CopyPlanError> Errors)
    {
        public long TotalBytes => Entries.Sum(entry => entry.Bytes);

        public long TotalItems => Entries.Sum(entry => entry.ItemCount);
    }

    private sealed class CopyPlanEntry(FileRef source, ResolvedPath sourcePath, string name, string finalPhysicalPath, long bytes, long itemCount)
    {
        public FileRef Source { get; } = source;
        public ResolvedPath SourcePath { get; } = sourcePath;
        public string Name { get; } = name;
        public string FinalPhysicalPath { get; set; } = finalPhysicalPath;
        public long Bytes { get; } = bytes;
        public long ItemCount { get; } = itemCount;
    }

    private sealed record CopyPlanError(FileRef? Source, string Message);

    private sealed record ScanResult(long Bytes, long Items);

    private sealed record CopyArtifact(TemporaryArtifact Record, string PhysicalPath);

    private sealed class FileRefComparer : IEqualityComparer<FileRef>
    {
        public static FileRefComparer Instance { get; } = new();

        public bool Equals(FileRef? x, FileRef? y) => x is not null && y is not null
            && x.Root.Equals(y.Root, StringComparison.OrdinalIgnoreCase)
            && x.Path.Equals(y.Path, StringComparison.Ordinal);

        public int GetHashCode(FileRef value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Root), StringComparer.Ordinal.GetHashCode(value.Path));
    }

    private sealed class CopyProgress(TaskRepository repository, TaskProgressService runtimeProgress, Guid taskId, long bytesTotal, long itemsTotal)
    {
        private long _bytesDone;
        private long _itemsDone;
        private DateTimeOffset _lastPersisted = DateTimeOffset.MinValue;

        public async Task BytesCopiedAsync(int bytes, CancellationToken cancellationToken)
        {
            _bytesDone += bytes;
            runtimeProgress.Report(taskId, _bytesDone);
            await ReportAsync(force: false, cancellationToken);
        }

        public async Task ItemCompletedAsync(CancellationToken cancellationToken)
        {
            _itemsDone++;
            await ReportAsync(force: false, cancellationToken);
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
}
