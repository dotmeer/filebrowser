using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace dotmeer.filebrowser.Tests;

public sealed class CopyTaskExecutorTests
{
    [Fact]
    public async Task Execute_CopiesFileAndDirectoryAcrossRootsAndPublishesOnlyFinalNames()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "file content");
        var sourceDirectory = Path.Combine(workspace.RootPath, "album");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty"));
        File.WriteAllText(Path.Combine(sourceDirectory, "track.txt"), "track content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "note.txt"), new FileRef("source", "album")],
            new FileRef("destination", "inbox"));

        await executor.ExecuteAsync(task, CancellationToken.None);
        var running = await repository.GetAsync(task.Id);

        Assert.NotNull(running);
        Assert.Equal(FileTaskStatus.Running, running.Status);
        Assert.Equal("file content", File.ReadAllText(Path.Combine(destinationDirectory, "note.txt")));
        Assert.Equal("track content", File.ReadAllText(Path.Combine(destinationDirectory, "album", "track.txt")));
        Assert.True(Directory.Exists(Path.Combine(destinationDirectory, "album", "empty")));
        Assert.Equal(new FileInfo(Path.Combine(workspace.RootPath, "note.txt")).Length + new FileInfo(Path.Combine(sourceDirectory, "track.txt")).Length, running.BytesDone);
        Assert.Equal(running.BytesTotal, running.BytesDone);
        Assert.Equal(running.ItemsTotal, running.ItemsDone);
        Assert.All(running.Items, item => Assert.Equal("completed", item.Status));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
    }

    [Fact]
    public async Task Execute_CollectsPreflightErrorsAndDoesNotCopyAnything()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "first.txt"), "first");
        var other = Path.Combine(workspace.RootPath, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "second.txt"), "second");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(destinationDirectory, "first.txt"), "existing");
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository,
            [new FileRef("source", "first.txt"), new FileRef("source", "other/second.txt")],
            new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));
        var scanned = await repository.GetAsync(task.Id);

        Assert.Contains("name_conflict", exception.SafeMessage);
        Assert.Contains("одной родительской", exception.SafeMessage);
        Assert.NotNull(scanned);
        Assert.Equal(FileTaskStatus.Scanning, scanned.Status);
        Assert.All(scanned.Items, item => Assert.Equal("failed", item.Status));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destinationDirectory, "first.txt")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "second.txt")));
    }

    [Fact]
    public async Task Execute_RejectsSymlinkFoundDuringDirectoryScan()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var sourceDirectory = Path.Combine(workspace.RootPath, "source-directory");
        Directory.CreateDirectory(sourceDirectory);
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(sourceDirectory, "link.txt"), target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "source-directory")], new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Contains("символическая ссылка", exception.SafeMessage);
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "inbox", "source-directory")));
    }

    [Fact]
    public async Task Execute_RejectsDestinationInsideSourceDirectoryBeforeCopying()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var sourceDirectory = Path.Combine(workspace.RootPath, "source-directory");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        File.WriteAllText(Path.Combine(sourceDirectory, "note.txt"), "content");
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "source-directory")], new FileRef("source", "source-directory/nested"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Contains("внутри источника", exception.SafeMessage);
        Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "nested", "source-directory")));
    }

    [Fact]
    public async Task Execute_CleansTemporaryArtifactAfterFileReadFailure()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var source = Path.Combine(workspace.RootPath, "locked.txt");
        File.WriteAllText(source, "content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "locked.txt")], new FileRef("destination", "inbox"));

        using var lockStream = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Contains("ошибка копирования", exception.SafeMessage);
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "locked.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Dispatcher_DeduplicatesAndSortsSourcesByPublicPath()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var dispatcher = new TaskDispatcher(repository, new TaskQueue());

        var task = await dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", "z.txt"), new FileRef("source", "a.txt"), new FileRef("source", "a.txt")],
            new FileRef("destination", "inbox"));

        Assert.Equal(["a.txt", "z.txt"], task.Sources.Select(source => source.Path));
        Assert.True(Directory.Exists(destinationRoot));
    }

    [Fact]
    public async Task Dispatcher_RejectsSourcesFromDifferentParentsBeforePersisting()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var dispatcher = new TaskDispatcher(repository, new TaskQueue());

        await Assert.ThrowsAsync<InvalidFileSystemRequestException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", "first.txt"), new FileRef("source", "other/second.txt")],
            new FileRef("destination", "inbox")));

        var page = await repository.GetPageAsync(0, 50);
        Assert.Empty(page.Tasks);
    }

    [Fact]
    public async Task Dispatcher_RejectsInvalidReferencesBeforePersisting()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var dispatcher = new TaskDispatcher(repository, new TaskQueue());

        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef(string.Empty, "a.txt")], new FileRef("destination", "inbox")));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", null!)], new FileRef("destination", "inbox")));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [null!], new FileRef("destination", "inbox")));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", "a.txt")], new FileRef(string.Empty, "inbox")));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", "a.txt")], new FileRef("destination", null!)));

        Assert.Empty((await repository.GetPageAsync(0, 50)).Tasks);
    }

    [Fact]
    public async Task Dispatcher_RejectsRootSourceBeforePersisting()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var dispatcher = new TaskDispatcher(repository, new TaskQueue());

        await Assert.ThrowsAsync<InvalidFileSystemRequestException>(() => dispatcher.EnqueueAsync(FileTaskType.Copy,
            [new FileRef("source", string.Empty)],
            new FileRef("destination", "inbox")));

        Assert.Empty((await repository.GetPageAsync(0, 50)).Tasks);
    }

    [Fact]
    public async Task Execute_RejectsTemporaryArtifactAsSource()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var temporarySource = Path.Combine(workspace.RootPath, ".filebrowser-source");
        File.WriteAllText(temporarySource, "temporary");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", ".filebrowser-source")], new FileRef("destination", "inbox"));

        var exception = await Assert.ThrowsAsync<FileTaskExecutionException>(() => executor.ExecuteAsync(task, CancellationToken.None));

        Assert.Contains("источник недоступен", exception.SafeMessage);
        Assert.True(File.Exists(temporarySource));
        Assert.False(File.Exists(Path.Combine(destinationRoot, "inbox", ".filebrowser-source")));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_CancellationLeavesRegisteredArtifactForStartupCleanup()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "source.txt"), "content");
        var destinationDirectory = Path.Combine(destinationRoot, "inbox");
        Directory.CreateDirectory(destinationDirectory);
        var unregisteredPath = Path.Combine(destinationDirectory, ".filebrowser-unregistered");
        File.WriteAllText(unregisteredPath, "keep");
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: source
                path: '{workspace.RootPath}'
              - name: destination
                path: '{destinationRoot}'
            """);
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var resolver = new PathResolver(FileBrowserConfigurationLoader.Load(configPath));
        var executor = new CopyTaskExecutor(new FileTransferEngine(resolver, repository, new TaskProgressService()));
        var task = await CreateScanningTaskAsync(repository, [new FileRef("source", "source.txt")], new FileRef("destination", "inbox"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(task, cancellation.Token));

        var running = await repository.GetAsync(task.Id);
        Assert.Equal(FileTaskStatus.Running, running!.Status);
        var artifact = Assert.Single(await repository.GetTemporaryArtifactsAsync());
        Assert.StartsWith(".filebrowser-", Path.GetFileName(artifact.File.Path), StringComparison.Ordinal);
        var artifactPath = Path.Combine(destinationDirectory, Path.GetFileName(artifact.File.Path));
        Assert.True(File.Exists(artifactPath));

        await repository.InterruptActiveTasksAsync();
        var cleanup = new TemporaryArtifactCleanupService(repository, resolver, NullLogger<TemporaryArtifactCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);

        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.False(File.Exists(artifactPath));
        Assert.True(File.Exists(unregisteredPath));
        Assert.Equal(FileTaskStatus.Interrupted, (await repository.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task CopyEndpoint_RejectsSourcesFromDifferentParentsWithBadRequest()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "first.txt"), "first");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "other"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "other", "second.txt"), "second");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "target"));
        using var host = CreateCopyHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1));
        var token = await GetAntiforgeryTokenAsync(host.Client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/copy")
        {
            Content = JsonContent.Create(new
            {
                sources = new[]
                {
                    new { root = "files", path = "first.txt" },
                    new { root = "files", path = "other/second.txt" }
                },
                destination = new { root = "files", path = "target" }
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await host.Client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", body.RootElement.GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "target", "first.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "target", "second.txt")));
    }

    [Fact]
    public async Task TemporaryCleanup_RemovesOnlyRegisteredArtifact()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        var configPath = workspace.WriteRawConfiguration($"""
            roots:
              - name: source
                path: '{workspace.RootPath}'
              - name: destination
                path: '{destinationRoot}'
            """);
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, [new FileRef("source", "missing.txt")], new FileRef("destination", ""));
        var registeredName = ".filebrowser-registered";
        var unregisteredName = ".filebrowser-unregistered";
        File.WriteAllText(Path.Combine(destinationRoot, registeredName), "temporary");
        File.WriteAllText(Path.Combine(destinationRoot, unregisteredName), "keep");
        await repository.RegisterTemporaryArtifactAsync(task.Id, new FileRef("destination", registeredName));
        var cleanup = new TemporaryArtifactCleanupService(repository, new PathResolver(FileBrowserConfigurationLoader.Load(configPath)), NullLogger<TemporaryArtifactCleanupService>.Instance);

        await cleanup.CleanupAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(destinationRoot, registeredName)));
        Assert.True(File.Exists(Path.Combine(destinationRoot, unregisteredName)));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Execute_ConcurrentPublicationKeepsExistingDataAndReportsNameConflict()
    {
        using var workspace = new TestWorkspace();
        var destinationRoot = CreateDestinationRoot(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "source.txt"), "source content");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "inbox"));
        var (repository, executor) = CreateExecutor(workspace, destinationRoot);
        var first = await CreateScanningTaskAsync(repository, [new FileRef("source", "source.txt")], new FileRef("destination", "inbox"));
        var second = await CreateScanningTaskAsync(repository, [new FileRef("source", "source.txt")], new FileRef("destination", "inbox"));

        var outcomes = await Task.WhenAll(ExecuteAndReportAsync(executor, first), ExecuteAndReportAsync(executor, second));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Success));
        Assert.Contains(outcomes, outcome => !outcome.Success && outcome.Error!.Contains("name_conflict", StringComparison.Ordinal));
        Assert.Equal("source content", File.ReadAllText(Path.Combine(destinationRoot, "inbox", "source.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(destinationRoot, "inbox"), ".filebrowser-*"));
    }

    [Fact]
    public async Task CopyEndpoint_QueuesAndCompletesCopyWithAntiforgery()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "source.txt"), "copied by worker");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "target"));
        using var host = CreateCopyHost(workspace.WriteConfiguration(workspace.RootPath, workers: 1));
        var token = await GetAntiforgeryTokenAsync(host.Client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/copy")
        {
            Content = JsonContent.Create(new
            {
                sources = new[] { new { root = "files", path = "source.txt" } },
                destination = new { root = "files", path = "target" }
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await host.Client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var taskId = body.RootElement.GetProperty("id").GetGuid();
        var completed = await WaitForStatusAsync(host.Services.GetRequiredService<TaskRepository>(), taskId, FileTaskStatus.Completed);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/tasks/{taskId:D}", response.Headers.Location?.ToString());
        Assert.Equal("copied by worker", File.ReadAllText(Path.Combine(workspace.RootPath, "target", "source.txt")));
        Assert.Equal("completed", Assert.Single(completed.Items).Status);
    }

    private static (TaskRepository Repository, CopyTaskExecutor Executor) CreateExecutor(TestWorkspace workspace, string destinationRoot)
    {
        var configPath = workspace.WriteRawConfiguration($"""
            workers: 1
            roots:
              - name: source
                path: '{workspace.RootPath}'
              - name: destination
                path: '{destinationRoot}'
            """);
        var repository = CreateRepository(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        var resolver = new PathResolver(FileBrowserConfigurationLoader.Load(configPath));
        var engine = new FileTransferEngine(resolver, repository, new TaskProgressService());
        return (repository, new CopyTaskExecutor(engine));
    }

    private static async Task<FileTask> CreateScanningTaskAsync(TaskRepository repository, IReadOnlyList<FileRef> sources, FileRef destination)
    {
        var task = await repository.CreateQueuedAsync(FileTaskType.Copy, sources, destination);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        return (await repository.GetAsync(task.Id))!;
    }

    private static TaskRepository CreateRepository(string databasePath)
    {
        var database = new TaskDatabase(databasePath);
        database.Migrate();
        return new TaskRepository(database);
    }

    private static string CreateDestinationRoot(TestWorkspace workspace)
    {
        var path = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "destination");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ProgressSpeed_UsesFiveSecondSlidingWindowAndDoesNotPersistState()
    {
        var progress = new TaskProgressService();
        var taskId = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

        progress.Report(taskId, 0, start);
        progress.Report(taskId, 500, start.AddSeconds(2));

        Assert.Equal(250, progress.GetBytesPerSecond(taskId, start.AddSeconds(2)));
        Assert.Equal(0, progress.GetBytesPerSecond(taskId, start.AddSeconds(8)));
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid taskId, FileTaskStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var task = await repository.GetAsync(taskId, timeout.Token);
            if (task?.Status == expected)
            {
                return task;
            }
            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException($"Задача {taskId} не достигла состояния {expected}.");
    }

    private static async Task<CopyOutcome> ExecuteAndReportAsync(CopyTaskExecutor executor, FileTask task)
    {
        try
        {
            await executor.ExecuteAsync(task, CancellationToken.None);
            return new CopyOutcome(true, null);
        }
        catch (FileTaskExecutionException exception)
        {
            return new CopyOutcome(false, exception.SafeMessage);
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/browse?root=files");
        var match = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"(?<token>[^\\\"]+)\\\"");
        Assert.True(match.Success, "На странице не найден antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static CopyHost CreateCopyHost(string configPath)
    {
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalDatabasePath = Environment.GetEnvironmentVariable("DatabasePath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", configPath);
        Environment.SetEnvironmentVariable("DatabasePath", Path.Combine(Path.GetDirectoryName(configPath)!, "app.db"));
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        var keyDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))));
        try
        {
            return new CopyHost(factory, factory.CreateClient());
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("DatabasePath", originalDatabasePath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    private sealed class CopyHost(WebApplicationFactory<Program> factory, HttpClient client) : IDisposable
    {
        public IServiceProvider Services => factory.Services;

        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }
    }

    private sealed record CopyOutcome(bool Success, string? Error);
}
