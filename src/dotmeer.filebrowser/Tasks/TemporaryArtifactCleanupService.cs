using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tasks;

public sealed class TemporaryArtifactCleanupService(TaskRepository repository, PathResolver pathResolver, ILogger<TemporaryArtifactCleanupService> logger)
{
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var artifacts = await repository.GetTemporaryArtifactsAsync(cancellationToken);
        foreach (var artifact in artifacts)
        {
            if (!Path.GetFileName(artifact.File.Path).StartsWith(".filebrowser-", StringComparison.Ordinal))
            {
                logger.LogWarning("Пропущен временный объект без служебного префикса: {ArtifactId}", artifact.Id);
                continue;
            }

            try
            {
                var resolved = pathResolver.ResolveExistingTemporaryArtifact(artifact.File);
                if (resolved.IsDirectory)
                {
                    EnsureTreeHasNoUnsupportedObjects(resolved.PhysicalPath);
                    Directory.Delete(resolved.PhysicalPath, recursive: true);
                }
                else
                {
                    File.Delete(resolved.PhysicalPath);
                }
                await repository.RemoveTemporaryArtifactAsync(artifact.Id, cancellationToken);
            }
            catch (FileSystemObjectNotFoundException)
            {
                await repository.RemoveTemporaryArtifactAsync(artifact.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is FileSystemResolutionException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Не удалось очистить зарегистрированный временный объект {ArtifactId}", artifact.Id);
            }
        }
    }

    private static void EnsureTreeHasNoUnsupportedObjects(string directoryPath)
    {
        foreach (var childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            PathInspection.ThrowIfUnsupported(childPath);
            if (Directory.Exists(childPath))
            {
                EnsureTreeHasNoUnsupportedObjects(childPath);
            }
        }
    }
}
