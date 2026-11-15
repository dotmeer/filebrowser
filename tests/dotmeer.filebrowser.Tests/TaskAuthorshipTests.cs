using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Auth;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.Uploads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Итерация 3: авторство задач, видимость по автору и безопасные точки
/// отзыва прав в worker и HTTP-загрузке.
/// Политика прав проверяется прямыми вызовами сервисов и реальным worker:
/// публичный допуск обычных учёток в LOCAL ещё закрыт.
/// </summary>
[Collection("Web application factory")]
public sealed class TaskAuthorshipTests
{
    private const string RootName = "files";
    private const string Password = "User-Pass-1!";

    [Fact]
    public async Task Migration_AddsAuthorColumn_PreservesTasksItemsAndArtifacts()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "app.db");
        var database = new TaskDatabase(databasePath);
        database.Migrate();

        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO tasks(id, type, status, stage, destination_root, destination_path,
                                  bytes_done, items_done, created_at_utc, updated_at_utc)
                VALUES ('legacy-task', 'copy', 'completed', 'completed', 'files', '',
                        0, 0, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO task_items(task_id, source_root, source_path, status, items_done)
                VALUES ('legacy-task', 'files', 'legacy.txt', 'completed', 1);
                INSERT INTO temporary_artifacts(task_id, root, path, created_at_utc)
                VALUES ('legacy-task', 'files', '.filebrowser-legacy', '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        // Схема предыдущей версии: колонки автора ещё нет, миграция 6 не применена.
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX ix_tasks_author;
                ALTER TABLE tasks DROP COLUMN author_user_id;
                DELETE FROM schema_migrations WHERE version = 6;
                """;
            command.ExecuteNonQuery();
        }

        new TaskDatabase(databasePath).Migrate();

        var repository = new TaskRepository(database);
        Assert.Equal(
            1,
            Count(database, "SELECT COUNT(*) FROM tasks WHERE id = 'legacy-task' AND type = 'copy' AND status = 'completed' AND error IS NULL;"));
        Assert.Equal(1, Count(database, "SELECT COUNT(*) FROM task_items WHERE task_id = 'legacy-task';"));
        Assert.Equal(
            1,
            Count(database, "SELECT COUNT(*) FROM task_items WHERE task_id = 'legacy-task' AND source_path = 'legacy.txt' AND status = 'completed';"));
        Assert.Equal(1, Count(database, "SELECT COUNT(*) FROM temporary_artifacts WHERE task_id = 'legacy-task';"));
        Assert.Equal(0, Count(database, "SELECT COUNT(*) FROM tasks WHERE author_user_id IS NOT NULL;"));
        Assert.Equal(1, Count(database, "SELECT COUNT(*) FROM schema_migrations WHERE version = 6;"));

        var author = Guid.NewGuid();
        var created = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "a.txt")],
            new FileRef(RootName, "target"),
            authorUserId: author);
        Assert.Equal(author, created.AuthorUserId);
        Assert.Equal(author, (await repository.GetAsync(created.Id))!.AuthorUserId);
    }

    [Fact]
    public async Task Visibility_ScopesActiveHistoryPaginationPointReadsAndCleanup()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var activeA = await repository.CreateQueuedAsync(
            FileTaskType.Copy, [new FileRef(RootName, "active-a.txt")], new FileRef(RootName, "target"), authorUserId: userA);
        var deleteA = await repository.CreateQueuedAsync(
            FileTaskType.Delete, [new FileRef(RootName, "delete-a.txt")], new FileRef(RootName, string.Empty), authorUserId: userA);
        var activeB = await repository.CreateQueuedAsync(
            FileTaskType.Move, [new FileRef(RootName, "active-b.txt")], new FileRef(RootName, "target"), authorUserId: userB);
        var legacyActive = await repository.CreateActiveAsync(
            FileTaskType.Upload,
            [new FileRef(RootName, "legacy.bin")],
            new FileRef(RootName, string.Empty),
            FileTaskStatus.Scanning,
            "scanning",
            1,
            1);

        var completedA1 = await CreateTerminalAsync(repository, FileTaskStatus.Completed, "a1.txt", userA);
        var completedA2 = await CreateTerminalAsync(repository, FileTaskStatus.Failed, "a2.txt", userA);
        var completedA3 = await CreateTerminalAsync(repository, FileTaskStatus.Interrupted, "a3.txt", userA);
        var completedB = await CreateTerminalAsync(repository, FileTaskStatus.Completed, "b1.txt", userB);
        var legacy = await CreateTerminalAsync(repository, FileTaskStatus.Completed, "legacy.txt", null);

        var visibleA = TaskVisibility.ForUser(userA);
        var active = await repository.GetActiveAsync(visibleA);
        Assert.Equal(
            new HashSet<Guid> { activeA.Id, deleteA.Id },
            active.Select(task => task.Id).ToHashSet());

        var historyA = await repository.GetHistoryPageAsync(0, 50, visibleA);
        Assert.Equal(
            new HashSet<Guid> { completedA1.Id, completedA2.Id, completedA3.Id },
            historyA.Tasks.Select(task => task.Id).ToHashSet());

        var firstPage = await repository.GetHistoryPageAsync(0, 2, visibleA);
        var secondPage = await repository.GetHistoryPageAsync(2, 2, visibleA);
        Assert.Equal(2, firstPage.Tasks.Count);
        Assert.True(firstPage.HasMore);
        Assert.Single(secondPage.Tasks);
        Assert.False(secondPage.HasMore);

        var pageA = await repository.GetPageAsync(0, 50, visibleA);
        Assert.Equal(5, pageA.Tasks.Count);
        Assert.All(pageA.Tasks, task => Assert.Equal(userA, task.AuthorUserId));

        Assert.Null(await repository.GetAsync(completedB.Id, visibleA));
        Assert.Null(await repository.GetAsync(legacy.Id, visibleA));
        Assert.NotNull(await repository.GetAsync(completedA1.Id, visibleA));
        Assert.Equal(userA, (await repository.GetAsync(completedA1.Id, visibleA))!.AuthorUserId);

        Assert.Equal(3, await repository.DeleteInactiveAsync(visibleA));
        Assert.Null(await repository.GetAsync(completedA1.Id));
        Assert.Null(await repository.GetAsync(completedA2.Id));
        Assert.Null(await repository.GetAsync(completedA3.Id));
        Assert.NotNull(await repository.GetAsync(completedB.Id));
        Assert.NotNull(await repository.GetAsync(legacy.Id));

        // Admin/ANONYMOUS видят и очищают всё, включая старые задачи без автора.
        Assert.Equal(4, (await repository.GetActiveAsync()).Count);
        Assert.Equal(2, await repository.DeleteInactiveAsync());
        Assert.Empty((await repository.GetHistoryPageAsync(0, 50)).Tasks);
        Assert.NotNull(await repository.GetAsync(activeB.Id));
        Assert.NotNull(await repository.GetAsync(legacyActive.Id));
    }

    [Fact]
    public async Task Visibility_DeletedAndRecreatedLogin_KeepsOldAuthorAndHidesOldTasks()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var database = host.Services.GetRequiredService<TaskDatabase>();

        var oldUserId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy, [new FileRef(RootName, "old.txt")], new FileRef(RootName, "target"), authorUserId: oldUserId);

        DeleteUser(database, oldUserId);
        var newUserId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        Assert.NotEqual(oldUserId, newUserId);

        var stored = await repository.GetAsync(task.Id);
        Assert.NotNull(stored);
        Assert.Equal(oldUserId, stored!.AuthorUserId);
        Assert.Null(await repository.GetAsync(task.Id, TaskVisibility.ForUser(newUserId)));
        Assert.NotNull(await repository.GetAsync(task.Id, TaskVisibility.ForUser(oldUserId)));
        Assert.NotNull(await repository.GetAsync(task.Id, TaskVisibility.Everything));

        var access = host.Services.GetRequiredService<FileAccessService>();
        Assert.False((await access.CreateForTaskAsync(oldUserId, CancellationToken.None)).IsFullAccess);
    }

    [Fact]
    public async Task Dispatcher_PersistsAuthorFromActor_AndAnonymousKeepsNull()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();

        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var task = await dispatcher.EnqueueAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "first.txt")],
            new FileRef(RootName, "target"),
            actor: actor);
        Assert.Equal(userId, task.AuthorUserId);

        using var anonymousWorkspace = new TestWorkspace();
        using var anonymousHost = TestHostFactory.Create(anonymousWorkspace.WriteConfiguration(anonymousWorkspace.RootPath));
        var anonymousTask = await anonymousHost.Services.GetRequiredService<TaskDispatcher>().EnqueueAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "first.txt")],
            new FileRef(RootName, "target"));
        Assert.Null(anonymousTask.AuthorUserId);
    }

    [Fact]
    public async Task NewTasksOfAllFourTypes_PersistAuthor()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var author = Guid.NewGuid();

        foreach (var type in new[] { FileTaskType.Copy, FileTaskType.Move, FileTaskType.Delete })
        {
            var task = await repository.CreateQueuedAsync(
                type,
                [new FileRef(RootName, $"{type}.txt")],
                new FileRef(RootName, "target"),
                authorUserId: author);
            Assert.Equal(type, task.Type);
            Assert.Equal(author, task.AuthorUserId);
            Assert.Equal(author, (await repository.GetAsync(task.Id))!.AuthorUserId);
        }

        var upload = await repository.CreateActiveAsync(
            FileTaskType.Upload,
            [new FileRef(RootName, "upload.bin")],
            new FileRef(RootName, string.Empty),
            FileTaskStatus.Scanning,
            "scanning",
            1,
            1,
            authorUserId: author);
        Assert.Equal(author, upload.AuthorUserId);
        Assert.Equal(author, (await repository.GetAsync(upload.Id))!.AuthorUserId);
    }

    [Fact]
    public async Task TaskAccess_AllRootsReferenceUsesRealRootPermissions()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var access = host.Services.GetRequiredService<TaskAccessService>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);

        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef(VirtualRoot.VirtualRootName, $"{RootName}/first.txt")],
            new FileRef(VirtualRoot.VirtualRootName, $"{RootName}/target"),
            authorUserId: userId);

        var actor = await access.EnsureTaskCanStartAsync(task, CancellationToken.None);
        Assert.Equal(userId, actor.UserId);

        await permissions.SetAsync(userId, RootName, RootAccess.Read, CancellationToken.None);
        await Assert.ThrowsAsync<FileTaskAccessRevokedException>(
            () => access.EnsureTaskCanStartAsync(task, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RechecksRightsImmediatelyAfterArtifactRegistration()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var database = host.Services.GetRequiredService<TaskDatabase>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var sourcePath = Path.Combine(workspace.RootPath, "before-revoke.txt");
        await File.WriteAllTextAsync(sourcePath, "preserve me");
        var task = await repository.CreateQueuedAsync(FileTaskType.Delete, [new FileRef(RootName, "before-revoke.txt")], new FileRef(RootName, ""), authorUserId: userId);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");

        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER revoke_delete_right AFTER INSERT ON temporary_artifacts BEGIN DELETE FROM user_root_permissions WHERE user_id = '" + userId.ToString("D") + "'; END;";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<FileTaskAccessRevokedException>(() =>
            host.Services.GetRequiredService<DeleteTaskExecutor>().ExecuteAsync(task, CancellationToken.None, CancellationToken.None));
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.Equal(FileTaskErrorCodes.AccessDenied, (await repository.GetAsync(task.Id))!.Items[0].Error);
    }

    [Fact]
    public async Task Cancel_ForeignTaskAndForeignPendingRequestDoNotChangeOwnerState()
    {
        using var workspace = new TestWorkspace();
        var repository = CreateRepository(workspace);
        var registry = new TaskCancellationRegistry();
        var service = new TaskCancellationService(repository, registry, new TaskOperationGate());
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy, [new FileRef(RootName, "a.txt")], new FileRef(RootName, "target"), authorUserId: owner);

        await service.CancelAsync(task.Id, TaskVisibility.ForUser(other));
        Assert.Equal(FileTaskStatus.Queued, (await repository.GetAsync(task.Id))!.Status);

        await service.CancelAsync(task.Id, TaskVisibility.ForUser(owner));
        Assert.Equal(FileTaskStatus.Cancelled, (await repository.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public void Registry_PendingCancelIsBoundToRequester()
    {
        var registry = new TaskCancellationRegistry();
        var uploadId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        registry.RequestCancel(uploadId, owner);
        registry.RequestCancel(uploadId, other);
        Assert.True(registry.TryRegister(uploadId, owner, out var ownerSource));
        Assert.True(ownerSource!.IsCancellationRequested);
        registry.Unregister(uploadId);

        var foreignOnlyId = Guid.NewGuid();
        registry.RequestCancel(foreignOnlyId, other);
        Assert.True(registry.TryRegister(foreignOnlyId, owner, out var foreignOnlySource));
        Assert.False(foreignOnlySource!.IsCancellationRequested);
        registry.Unregister(foreignOnlyId);

        Assert.True(registry.TryRegister(uploadId, owner, out ownerSource));
        Assert.False(ownerSource!.IsCancellationRequested);
        Assert.False(registry.TryRegister(uploadId, owner, out _));
        Assert.False(registry.RequestCancel(uploadId, other));
        Assert.False(ownerSource.IsCancellationRequested);
        Assert.True(registry.RequestCancel(uploadId, owner));
        Assert.True(ownerSource.IsCancellationRequested);
        registry.Unregister(uploadId);

        var adminCancelId = Guid.NewGuid();
        Assert.True(registry.TryRegister(adminCancelId, owner, out var ownerSource2));
        Assert.True(registry.RequestCancel(adminCancelId, requesterUserId: null));
        Assert.True(ownerSource2!.IsCancellationRequested);
        registry.Unregister(adminCancelId);

        registry.RequestCancel(uploadId, owner);
        using (var ownSource = registry.Register(uploadId, owner))
        {
            Assert.True(ownSource.IsCancellationRequested);
        }

        registry.Unregister(uploadId);

        // Заявка admin/системного доступа действует для любого автора.
        registry.RequestCancel(uploadId, requesterUserId: null);
        using (var anySource = registry.Register(uploadId, other))
        {
            Assert.True(anySource.IsCancellationRequested);
        }

        registry.Unregister(uploadId);
    }

    [Theory]
    [InlineData("permissions")]
    [InlineData("deleted")]
    public async Task Worker_RevokedOrDeletedAuthorFailsBeforeStart(string revokeKind)
    {
        using var workspace = new TestWorkspace();
        var executor = new GatedExecutor();
        using var host = CreateLocalHost(
            workspace.WriteConfiguration(workspace.RootPath, workers: 1),
            services =>
            {
                services.RemoveAll<IFileTaskExecutor>();
                services.AddSingleton<IFileTaskExecutor>(executor);
            });
        await SetupAdminAsync(host);

        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var database = host.Services.GetRequiredService<TaskDatabase>();
        var dispatcher = host.Services.GetRequiredService<TaskDispatcher>();

        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var blocker = await dispatcher.EnqueueAsync(
            FileTaskType.Copy, [new FileRef(RootName, "blocker.txt")], new FileRef(RootName, "target"));
        await executor.WaitForStartedAsync(1);

        var target = await dispatcher.EnqueueAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "second.txt")],
            new FileRef(RootName, "target"),
            actor: actor);

        if (revokeKind == "permissions")
        {
            await permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None);
        }
        else
        {
            DeleteUser(database, userId);
        }

        executor.Release();
        await WaitForStatusAsync(repository, blocker.Id, FileTaskStatus.Completed);
        var failed = await WaitForStatusAsync(repository, target.Id, FileTaskStatus.Failed);

        Assert.Equal(FileTaskErrorCodes.AccessDenied, failed.Error);
        Assert.Equal("not_processed", Assert.Single(failed.Items).Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, failed.Items[0].Error);
        Assert.DoesNotContain(target.Id, executor.StartedIds);
    }

    [Fact]
    public async Task Move_RevokedBetweenObjects_KeepsCompletedObjectAndStopsBeforeNext()
    {
        using var workspace = new TestWorkspace();
        var destination = Path.Combine(workspace.RootPath, "target");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "a.txt"), "aaa");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "b.txt"), "bbb");
        using var host = await CreateConfiguredHostAsync(workspace);

        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var engine = new RevokingMoveEngine(
            host.Services.GetRequiredService<PathResolver>(),
            repository,
            new TaskProgressService(),
            () => permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None).GetAwaiter().GetResult(),
            "a.txt");
        var executor = new MoveTaskExecutor(engine, host.Services.GetRequiredService<TaskAccessService>());
        var task = await repository.CreateQueuedAsync(
            FileTaskType.Move,
            [new FileRef(RootName, "a.txt"), new FileRef(RootName, "b.txt")],
            new FileRef(RootName, "target"),
            authorUserId: userId);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");

        await Assert.ThrowsAsync<FileTaskAccessRevokedException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None, actor));

        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "b.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "b.txt")));

        var stored = (await repository.GetAsync(task.Id))!;
        Assert.Equal("completed", stored.Items[0].Status);
        Assert.Equal("not_processed", stored.Items[1].Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, stored.Items[1].Error);
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.Empty(Directory.GetFiles(destination, ".filebrowser-*"));
    }

    [Fact]
    public async Task Copy_RevokedBeforePublish_LeavesFinalNameFreeAndCleansArtifact()
    {
        using var workspace = new TestWorkspace();
        var destination = Path.Combine(workspace.RootPath, "target");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "a.txt"), "aaa");
        using var host = await CreateConfiguredHostAsync(workspace);

        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var engine = new RevokingCopyEngine(
            host.Services.GetRequiredService<PathResolver>(),
            repository,
            new TaskProgressService(),
            () => permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None).GetAwaiter().GetResult(),
            "a.txt");
        var executor = new CopyTaskExecutor(engine, host.Services.GetRequiredService<TaskAccessService>());
        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "a.txt")],
            new FileRef(RootName, "target"),
            authorUserId: userId);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");

        await Assert.ThrowsAsync<FileTaskAccessRevokedException>(
            () => executor.ExecuteAsync(task, CancellationToken.None, CancellationToken.None, actor));

        Assert.False(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "a.txt")));
        var stored = (await repository.GetAsync(task.Id))!;
        Assert.Equal("not_processed", Assert.Single(stored.Items).Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, stored.Items[0].Error);
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.Empty(Directory.GetFiles(destination, ".filebrowser-*"));
    }

    [Fact]
    public async Task Upload_RevokedDuringStalledBody_StopsWithoutWaitingForNextByte()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var uploads = host.Services.GetRequiredService<UploadService>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var preflight = uploads.Prepare(new FileRef(RootName, string.Empty), "stalled.bin", 1024 * 1024, actor);
        var session = await uploads.StartAsync(preflight, CancellationToken.None, taskId: null, authorUserId: userId);
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        await pipe.Writer.FlushAsync();

        var receive = uploads.ReceiveAsync(session, pipe.Reader, CancellationToken.None);
        await WaitForStatusAsync(repository, session.TaskId, FileTaskStatus.Running);

        // Байты больше не поступают: отзыв прав должен прервать ожидание чтения.
        await permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<FileTaskAccessRevokedException>(exception);

        await uploads.AbortAsync(session, UploadAbortReason.AccessRevoked, FileTaskErrorCodes.AccessDenied);
        var stored = (await repository.GetAsync(session.TaskId))!;
        Assert.Equal(FileTaskStatus.Failed, stored.Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, stored.Error);
        Assert.Equal("failed", Assert.Single(stored.Items).Status);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "stalled.bin")));
        Assert.Empty(Directory.GetFiles(workspace.RootPath, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Upload_RevokedBeforeBodyStarts_FailsWithoutTemporaryFile()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var uploads = host.Services.GetRequiredService<UploadService>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var preflight = uploads.Prepare(new FileRef(RootName, string.Empty), "early.bin", 3, actor);
        var session = await uploads.StartAsync(preflight, CancellationToken.None, taskId: null, authorUserId: userId);
        await permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None);

        await Assert.ThrowsAsync<FileTaskAccessRevokedException>(
            () => uploads.ReceiveAsync(session, new MemoryStream(Encoding.UTF8.GetBytes("abc")), CancellationToken.None));

        await uploads.AbortAsync(session, UploadAbortReason.AccessRevoked, FileTaskErrorCodes.AccessDenied);
        var stored = (await repository.GetAsync(session.TaskId))!;
        Assert.Equal(FileTaskStatus.Failed, stored.Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, stored.Error);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "early.bin")));
        Assert.Empty(Directory.GetFiles(workspace.RootPath, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
    }

    [Fact]
    public async Task Upload_RevokedBeforePublish_KeepsFinalNameFreeAndFailsTask()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var uploads = host.Services.GetRequiredService<UploadService>();
        var userId = await users.CreateUserAsync("alice", Password, mustChangePassword: false, CancellationToken.None);
        await permissions.SetAsync(userId, RootName, RootAccess.Write, CancellationToken.None);
        var actor = await access.CreateForUserAsync(userId, CancellationToken.None);

        var preflight = uploads.Prepare(new FileRef(RootName, string.Empty), "publish.bin", 3, actor);
        var session = await uploads.StartAsync(preflight, CancellationToken.None, taskId: null, authorUserId: userId);
        var body = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        var bytes = await uploads.ReceiveAsync(session, body, CancellationToken.None);

        try
        {
            uploads.PublishCheckpointAsync = async checkpoint =>
            {
                if (checkpoint == UploadPublishCheckpoint.BeforeFileMove)
                {
                    await permissions.SetAsync(userId, RootName, RootAccess.None, CancellationToken.None);
                }
            };

            await Assert.ThrowsAsync<FileTaskAccessRevokedException>(
                () => uploads.PublishAsync(session, bytes, CancellationToken.None));
        }
        finally
        {
            uploads.PublishCheckpointAsync = null;
        }

        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "publish.bin")));
        Assert.Empty(Directory.GetFiles(workspace.RootPath, ".filebrowser-*"));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        var stored = (await repository.GetAsync(session.TaskId))!;
        Assert.Equal(FileTaskStatus.Failed, stored.Status);
        Assert.Equal(FileTaskErrorCodes.AccessDenied, stored.Error);
    }

    [Fact]
    public async Task UploadEndpoint_PersistsAdminAsTaskAuthor()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var admin = await host.Services.GetRequiredService<UserStore>()
            .FindByLoginAsync(UserStore.AdminLogin, CancellationToken.None);
        Assert.NotNull(admin);

        using var client = host.CreateClient(allowAutoRedirect: false);
        var loginToken = await GetAntiforgeryTokenAsync(client, "/login");
        using (var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = loginToken,
                   ["login"] = UserStore.AdminLogin,
                   ["password"] = "Admin-Pass-1!"
               })))
        {
            Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        }

        var token = await GetAntiforgeryTokenAsync(client, "/");
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("data"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/fs/upload?root=files&path=&name=author.txt")
        {
            Content = content
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var upload = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var task = Assert.Single((await host.Services.GetRequiredService<TaskRepository>()
            .GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(admin!.Id, task.AuthorUserId);
        Assert.Equal(FileTaskType.Upload, task.Type);
    }

    private static int Count(TaskDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TaskRepository CreateRepository(TestWorkspace workspace)
    {
        var database = new TaskDatabase(Path.Combine(workspace.ConfigDirectory, "tasks.db"));
        database.Migrate();
        return new TaskRepository(database);
    }

    private static async Task<FileTask> CreateTerminalAsync(
        TaskRepository repository,
        FileTaskStatus status,
        string sourceName,
        Guid? authorUserId)
    {
        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, sourceName)],
            new FileRef(RootName, "target"),
            authorUserId: authorUserId);
        await repository.TransitionAsync(task.Id, FileTaskStatus.Scanning, "scanning");
        await repository.TransitionAsync(task.Id, FileTaskStatus.Running, "running");
        await repository.TransitionAsync(task.Id, status, status.ToString().ToLowerInvariant());
        return (await repository.GetAsync(task.Id))!;
    }

    private static void DeleteUser(TaskDatabase database, Guid userId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE id = $id;";
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static TestHostFactory.TestHost CreateLocalHost(
        string configPath,
        Action<IServiceCollection>? configureServices = null) =>
        TestHostFactory.Create(
            configPath,
            settings: new Dictionary<string, string?> { [AuthModeReader.SettingName] = "LOCAL" },
            configureServices: configureServices);

    private static async Task<TestHostFactory.TestHost> CreateConfiguredHostAsync(TestWorkspace workspace)
    {
        var host = CreateLocalHost(workspace.WriteConfiguration(workspace.RootPath));
        try
        {
            await SetupAdminAsync(host);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task SetupAdminAsync(TestHostFactory.TestHost host)
    {
        using var client = host.CreateClient(allowAutoRedirect: false);
        var token = await GetAntiforgeryTokenAsync(client, "/setup");
        using var response = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["password"] = "Admin-Pass-1!",
            ["confirmPassword"] = "Admin-Pass-1!"
        }));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid taskId, FileTaskStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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

    private sealed class GatedExecutor : IFileTaskExecutor
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _startedIds = new();
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Guid> StartedIds => _startedIds.ToArray();

        public async Task ExecuteAsync(FileTask task, CancellationToken cancellationToken, CancellationToken shutdownToken)
        {
            _startedIds.Enqueue(task.Id);
            _started.Release();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStartedAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                if (!await _started.WaitAsync(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("Задача не была запущена вовремя.");
                }
            }
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class RevokingMoveEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        Action revoke,
        string revokeAfterEntry) : FileTransferEngine(resolver, repository, progress)
    {
        internal override bool TryRenameEntry(FileTransferPlanEntry entry)
        {
            var moved = base.TryRenameEntry(entry);
            if (moved && entry.Name == revokeAfterEntry)
            {
                revoke();
            }

            return moved;
        }
    }

    private sealed class RevokingCopyEngine(
        PathResolver resolver,
        TaskRepository repository,
        TaskProgressService progress,
        Action revoke,
        string revokeAfterEntry) : FileTransferEngine(resolver, repository, progress)
    {
        internal override async Task CopyEntryAsync(
            FileTransferPlanEntry entry,
            string destinationPath,
            TransferProgress progress,
            CancellationToken cancellationToken)
        {
            await base.CopyEntryAsync(entry, destinationPath, progress, cancellationToken);
            if (entry.Name == revokeAfterEntry)
            {
                revoke();
            }
        }
    }
}
