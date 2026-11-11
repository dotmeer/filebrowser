using System.Text;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.Uploads;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Приём файла: предпроверка, временный артефакт, публикация без перезаписи
/// и очистка на любой неудаче (SPEC §11, §13.10).
/// </summary>
public sealed class UploadServiceTests
{
    [Fact]
    public async Task ReceiveAndPublish_WritesFileAndCompletesTask()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);
        var content = Encoding.UTF8.GetBytes("hello upload");

        var preflight = service.Prepare(new FileRef("files", string.Empty), "hello.txt", content.Length);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        var received = await service.ReceiveAsync(session, new MemoryStream(content), CancellationToken.None);
        await service.PublishAsync(session, received, CancellationToken.None);

        var finalPath = Path.Combine(workspace.RootPath, "hello.txt");
        Assert.Equal(content, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());

        var history = await repository.GetHistoryPageAsync(0, 10);
        var task = Assert.Single(history.Tasks);
        Assert.Equal(FileTaskType.Upload, task.Type);
        Assert.Equal(FileTaskStatus.Completed, task.Status);
        Assert.Equal(content.Length, task.BytesDone);
        Assert.Equal(content.Length, task.BytesTotal);
        Assert.Equal("completed", Assert.Single(task.Items).Status);
    }

    [Fact]
    public async Task ReceiveAndPublish_EmptyFile_CreatesZeroByteFile()
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace);

        var preflight = service.Prepare(new FileRef("files", string.Empty), "empty.bin", 0);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        var received = await service.ReceiveAsync(session, new MemoryStream(), CancellationToken.None);
        await service.PublishAsync(session, received, CancellationToken.None);

        var finalPath = Path.Combine(workspace.RootPath, "empty.bin");
        Assert.True(File.Exists(finalPath));
        Assert.Equal(0, new FileInfo(finalPath).Length);
    }

    [Fact]
    public async Task ReceiveAndPublish_NonAsciiName_PreservesName()
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace);
        var content = Encoding.UTF8.GetBytes("данные");

        var preflight = service.Prepare(new FileRef("files", string.Empty), "отчёт 2026.txt", content.Length);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        await service.ReceiveAsync(session, new MemoryStream(content), CancellationToken.None);
        await service.PublishAsync(session, content.Length, CancellationToken.None);

        var finalPath = Path.Combine(workspace.RootPath, "отчёт 2026.txt");
        Assert.Equal(content, await File.ReadAllBytesAsync(finalPath));
    }

    [Fact]
    public async Task Start_RegistersArtifactBeforeTemporaryFileExists()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);

        var preflight = service.Prepare(new FileRef("files", string.Empty), "a.bin", 10);
        var session = await service.StartAsync(preflight, CancellationToken.None);

        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        var artifact = Assert.Single(await repository.GetTemporaryArtifactsAsync());
        Assert.Equal(session.TaskId, artifact.TaskId);
        Assert.StartsWith(".filebrowser-", Path.GetFileName(artifact.File.Path), StringComparison.Ordinal);

        var active = Assert.Single(await repository.GetActiveAsync());
        Assert.Equal(FileTaskStatus.Scanning, active.Status);
        Assert.Equal(10, active.BytesTotal);
    }

    [Fact]
    public async Task Receive_ShorterBodyThanDeclared_IsRejectedAndCleanedUp()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);
        var preflight = service.Prepare(new FileRef("files", string.Empty), "short.bin", 10);
        var session = await service.StartAsync(preflight, CancellationToken.None);

        await Assert.ThrowsAsync<UploadIncompleteException>(() =>
            service.ReceiveAsync(session, new MemoryStream(new byte[4]), CancellationToken.None));
        await service.AbortAsync(session, UploadAbortReason.Error, FileTaskErrorCodes.UploadIncomplete);

        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "short.bin")));
        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskStatus.Failed, task.Status);
        Assert.Equal(FileTaskErrorCodes.UploadIncomplete, task.Error);
    }

    [Fact]
    public async Task Receive_ThrowingBody_LeavesNoTemporaryFileAfterAbort()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);
        var preflight = service.Prepare(new FileRef("files", string.Empty), "boom.bin", 10);
        var session = await service.StartAsync(preflight, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            service.ReceiveAsync(session, new FailingStream(Encoding.UTF8.GetBytes("ab")), CancellationToken.None));
        await service.AbortAsync(session, UploadAbortReason.Error, FileTaskErrorCodes.IoError);

        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskStatus.Failed, task.Status);
        Assert.Equal(FileTaskErrorCodes.IoError, task.Error);
    }

    [Fact]
    public async Task Abort_UserCancel_MarksTaskCancelled()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);
        var preflight = service.Prepare(new FileRef("files", string.Empty), "cancel.bin", 10);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        var received = service.ReceiveAsync(session, new MemoryStream(new byte[4]), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => received);

        await service.AbortAsync(session, UploadAbortReason.UserCancelled, FileTaskErrorCodes.CancelledByUser);

        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskStatus.Cancelled, task.Status);
        Assert.Equal(FileTaskErrorCodes.CancelledByUser, task.Error);
    }

    [Fact]
    public async Task Publish_WithConcurrentNameConflict_KeepsExistingFileAndFails()
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace);
        var content = Encoding.UTF8.GetBytes("new");
        var preflight = service.Prepare(new FileRef("files", string.Empty), "race.txt", content.Length);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        await service.ReceiveAsync(session, new MemoryStream(content), CancellationToken.None);

        var finalPath = Path.Combine(workspace.RootPath, "race.txt");
        await File.WriteAllTextAsync(finalPath, "existing");

        await Assert.ThrowsAsync<FileSystemNameConflictException>(() =>
            service.PublishAsync(session, content.Length, CancellationToken.None));

        Assert.Equal("existing", await File.ReadAllTextAsync(finalPath));
        await service.AbortAsync(session, UploadAbortReason.Error, FileTaskErrorCodes.NameConflict);
        Assert.False(File.Exists(session.TemporaryPhysicalPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".filebrowser-hidden")]
    public void Prepare_RejectsInvalidName(string name)
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Prepare(new FileRef("files", string.Empty), name, 1));
    }

    [Fact]
    public void Prepare_RejectsExistingName()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "exists.txt"), "x");
        var (service, _) = CreateServices(workspace);

        Assert.Throws<FileSystemNameConflictException>(() =>
            service.Prepare(new FileRef("files", string.Empty), "exists.txt", 1));
    }

    [Fact]
    public void Prepare_RejectsFileAsParent()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "file.txt"), "x");
        var (service, _) = CreateServices(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Prepare(new FileRef("files", "file.txt"), "child.txt", 1));
    }

    [Fact]
    public void Prepare_RejectsVirtualRootTopLevelAndMissingRoot()
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace);

        Assert.Throws<InvalidFileSystemRequestException>(() =>
            service.Prepare(new FileRef(VirtualRoot.VirtualRootName, string.Empty), "a.txt", 1));
        Assert.Throws<FileSystemObjectNotFoundException>(() =>
            service.Prepare(new FileRef("missing", string.Empty), "a.txt", 1));
    }

    [Fact]
    public async Task Prepare_UnderVirtualRoot_DelegatesToRealRoot()
    {
        using var workspace = new TestWorkspace();
        var (service, repository) = CreateServices(workspace);
        var content = Encoding.UTF8.GetBytes("via virtual root");

        var preflight = service.Prepare(
            new FileRef(VirtualRoot.VirtualRootName, "files"),
            "virtual.txt",
            content.Length);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        await service.ReceiveAsync(session, new MemoryStream(content), CancellationToken.None);
        await service.PublishAsync(session, content.Length, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(workspace.RootPath, "virtual.txt")));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskStatus.Completed, task.Status);
    }

    [Fact]
    public void Prepare_RejectsOversizedFileByConfiguredLimit()
    {
        using var workspace = new TestWorkspace();
        var (service, _) = CreateServices(workspace, maxFileBytes: 4);

        Assert.Throws<FileTooLargeException>(() =>
            service.Prepare(new FileRef("files", string.Empty), "big.bin", 5));
        var allowed = service.Prepare(new FileRef("files", string.Empty), "small.bin", 4);
        Assert.Equal(4, allowed.ContentLength);
    }

    [Fact]
    public async Task Prepare_AllowsNestedParent()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "nested");
        Directory.CreateDirectory(directory);
        var (service, _) = CreateServices(workspace);
        var content = Encoding.UTF8.GetBytes("nested");

        var preflight = service.Prepare(new FileRef("files", "nested"), "a.txt", content.Length);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        await service.ReceiveAsync(session, new MemoryStream(content), CancellationToken.None);
        await service.PublishAsync(session, content.Length, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(directory, "a.txt")));
        Assert.Equal("nested/a.txt", preflight.Final.Path);
    }

    [Fact]
    public async Task StartupInterruptsActiveUploadAndRemovesRegisteredArtifact()
    {
        using var workspace = new TestWorkspace();
        var (service, repository, configuration) = CreateServicesWithConfiguration(workspace);
        var preflight = service.Prepare(new FileRef("files", string.Empty), "partial.bin", 10);
        var session = await service.StartAsync(preflight, CancellationToken.None);
        await File.WriteAllBytesAsync(session.TemporaryPhysicalPath, new byte[4]);

        var interrupted = await repository.InterruptActiveTasksAsync();
        var cleanup = new TemporaryArtifactCleanupService(
            repository,
            new PathResolver(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TemporaryArtifactCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);

        Assert.Equal(1, interrupted);
        Assert.False(File.Exists(session.TemporaryPhysicalPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var task = await repository.GetAsync(session.TaskId);
        Assert.NotNull(task);
        Assert.Equal(FileTaskStatus.Interrupted, task!.Status);
        Assert.Equal(FileTaskErrorCodes.TaskInterruptedRestart, task.Error);
    }

    private static (UploadService Service, TaskRepository Repository) CreateServices(
        TestWorkspace workspace,
        long? maxFileBytes = null)
    {
        var (service, repository, _) = CreateServicesWithConfiguration(workspace, maxFileBytes);
        return (service, repository);
    }

    private static (UploadService Service, TaskRepository Repository, FileBrowserConfiguration Configuration) CreateServicesWithConfiguration(
        TestWorkspace workspace,
        long? maxFileBytes = null)
    {
        var configPath = maxFileBytes is null
            ? workspace.WriteConfiguration(workspace.RootPath)
            : workspace.WriteRawConfiguration(
                $"uploads:\n  maxFileBytes: {maxFileBytes}\nroots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        var configuration = FileBrowserConfigurationLoader.Load(configPath);
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        var repository = new TaskRepository(database);
        var service = new UploadService(
            new PathResolver(configuration),
            repository,
            new TaskProgressService(),
            configuration);
        return (service, repository, configuration);
    }

    private sealed class FailingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= prefix.Length)
            {
                throw new IOException("Имитация обрыва соединения.");
            }

            var length = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, length).CopyTo(buffer.Span);
            _position += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
