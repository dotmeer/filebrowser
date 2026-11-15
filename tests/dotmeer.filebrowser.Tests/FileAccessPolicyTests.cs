using System.Net;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.Auth;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.Uploads;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Политика прав на корни и защита служебных каталогов. Тесты вызывают прикладные сервисы напрямую с явно
/// заданным актором.
/// </summary>
[Collection("Web application factory")]
public sealed class FileAccessPolicyTests
{
    private const string RootName = "files";
    private const string Password = "User-Pass-1!";

    [Fact]
    public async Task Matrix_NoAccessReadOnlyAndWrite_IsEnforcedByServiceEntryPoints()
    {
        using var workspace = new TestWorkspace();
        var docs = Path.Combine(workspace.RootPath, "docs");
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(Path.Combine(docs, "report.txt"), "report");
        using var host = await CreateConfiguredHostAsync(workspace);

        var noAccess = await CreateActorAsync(host, "none", RootAccess.None);
        var readOnly = await CreateActorAsync(host, "reader", RootAccess.Read);
        var writer = await CreateActorAsync(host, "writer", RootAccess.Write);

        var readerService = host.Services.GetRequiredService<FileSystemReadService>();
        var writerService = host.Services.GetRequiredService<FileSystemWriteService>();
        var searchService = host.Services.GetRequiredService<FileSystemSearchService>();
        var archiveService = host.Services.GetRequiredService<FileSystemArchiveService>();
        var uploadService = host.Services.GetRequiredService<UploadService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var resolver = host.Services.GetRequiredService<PathResolver>();
        var dispatcher = new TaskDispatcher(repository, new TaskQueue(), resolver);

        Assert.Empty(readerService.GetRoots(noAccess));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => readerService.List(new FileRef(RootName, string.Empty), noAccess));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => readerService.OpenFile(new FileRef(RootName, "docs/report.txt"), noAccess));
        var deniedExisting = Assert.Throws<FileSystemAccessDeniedException>(
            () => readerService.OpenFile(new FileRef(RootName, "docs/report.txt"), noAccess));
        var deniedMissing = Assert.Throws<FileSystemAccessDeniedException>(
            () => readerService.OpenFile(new FileRef(RootName, "docs/missing.txt"), noAccess));
        Assert.Equal(deniedExisting.GetType(), deniedMissing.GetType());
        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(new FileRef(RootName, string.Empty), [], CancellationToken.None, noAccess));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => uploadService.Prepare(new FileRef(RootName, string.Empty), "new.txt", null, noAccess));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => writerService.CreateDirectory(new FileRef(RootName, string.Empty), "new", noAccess));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "new")));

        var readRoots = readerService.GetRoots(readOnly);
        var readRoot = Assert.Single(readRoots);
        Assert.False(readRoot.CanWrite);
        Assert.Equal(
            ["docs"],
            readerService.List(new FileRef(RootName, string.Empty), readOnly).Entries.Select(entry => entry.Name));
        var opened = readerService.OpenFile(new FileRef(RootName, "docs/report.txt"), readOnly);
        Assert.Equal("report.txt", opened.Name);
        opened.Stream.Dispose();

        var search = searchService.Search(
            new FileRef(RootName, string.Empty),
            "report",
            ext: null,
            extGlobs: null,
            limit: FileSystemSearchService.DefaultResultLimit,
            cancellationToken: CancellationToken.None,
            actor: readOnly);
        Assert.Single(search.Results);
        Assert.NotNull(archiveService.Plan(new FileRef(RootName, string.Empty), [], CancellationToken.None, readOnly));

        Assert.Throws<FileSystemAccessDeniedException>(
            () => writerService.CreateDirectory(new FileRef(RootName, string.Empty), "reader-cannot", readOnly));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => uploadService.Prepare(new FileRef(RootName, string.Empty), "reader-cannot.txt", null, readOnly));
        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "docs/report.txt")],
            new FileRef(RootName, string.Empty),
            actor: readOnly));
        Assert.Empty((await repository.GetPageAsync(0, 10)).Tasks);
        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "missing.txt")],
            new FileRef(RootName, string.Empty),
            actor: noAccess));
        Assert.Empty((await repository.GetPageAsync(0, 10)).Tasks);

        var writeRoot = Assert.Single(readerService.GetRoots(writer));
        Assert.True(writeRoot.CanWrite);
        writerService.CreateDirectory(new FileRef(RootName, string.Empty), "created", writer);
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "created")));
        var preflight = uploadService.Prepare(new FileRef(RootName, string.Empty), "upload.txt", 4, writer);
        Assert.Equal("upload.txt", preflight.FinalPath.RelativePath);

        var task = await dispatcher.EnqueueAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "docs/report.txt")],
            new FileRef(RootName, string.Empty),
            actor: writer);
        Assert.Equal(FileTaskType.Copy, task.Type);
        Assert.Single((await repository.GetPageAsync(0, 10)).Tasks);
    }

    [Fact]
    public async Task VirtualRoot_ShowsOnlyReadableRoots_AndHiddenRootIsNotUnavailable()
    {
        using var workspace = new TestWorkspace();
        var secondRoot = Path.Combine(workspace.ConfigDirectory, "hidden-root");
        Directory.CreateDirectory(secondRoot);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "needle.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "visible.txt"), "visible");
        var configPath = workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: hidden\n    path: '{secondRoot}'\n");
        using var host = await CreateConfiguredHostAsync(configPath);
        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var searchService = host.Services.GetRequiredService<FileSystemSearchService>();
        var readerActor = await CreateActorAsync(host, "reader", RootAccess.Read);

        Assert.Equal([RootName], reader.GetRoots(readerActor).Select(root => root.Name));
        Assert.Equal(
            [RootName],
            reader.List(new FileRef(VirtualRoot.VirtualRootName, string.Empty), readerActor)
                .Entries.Select(entry => entry.Name));
        Assert.Equal(
            ["visible.txt"],
            reader.List(new FileRef(VirtualRoot.VirtualRootName, RootName), readerActor)
                .Entries.Select(entry => entry.Name));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => reader.List(new FileRef(VirtualRoot.VirtualRootName, "hidden"), readerActor));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => reader.List(new FileRef("hidden", string.Empty), readerActor));

        var search = searchService.Search(
            new FileRef(VirtualRoot.VirtualRootName, string.Empty),
            "needle",
            ext: null,
            extGlobs: null,
            limit: FileSystemSearchService.DefaultResultLimit,
            cancellationToken: CancellationToken.None,
            actor: readerActor);
        Assert.Empty(search.Results);
        Assert.Empty(search.UnavailableRoots);
    }

    [Fact]
    public async Task ServiceDirectories_HideBothParents_AndProtectBranchesForRegularUsers()
    {
        using var workspace = new TestWorkspace();
        var configDirectory = Path.Combine(workspace.RootPath, "config");
        var databaseDirectory = Path.Combine(workspace.RootPath, "db");
        var neighbor = Path.Combine(workspace.RootPath, "db2");
        var docs = Path.Combine(workspace.RootPath, "docs");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(databaseDirectory);
        Directory.CreateDirectory(neighbor);
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "secret.txt"), "config-secret");
        await File.WriteAllTextAsync(Path.Combine(databaseDirectory, "secret.db"), "db-secret");
        await File.WriteAllTextAsync(Path.Combine(neighbor, "visible.txt"), "visible");
        await File.WriteAllTextAsync(Path.Combine(docs, "note.txt"), "note");

        var configPath = Path.Combine(configDirectory, "config.yaml");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        await File.WriteAllTextAsync(configPath, $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        using var host = await CreateConfiguredHostAsync(configPath, databasePath);

        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var writer = host.Services.GetRequiredService<FileSystemWriteService>();
        var searchService = host.Services.GetRequiredService<FileSystemSearchService>();
        var archiveService = host.Services.GetRequiredService<FileSystemArchiveService>();
        var uploadService = host.Services.GetRequiredService<UploadService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var dispatcher = new TaskDispatcher(repository, new TaskQueue(), host.Services.GetRequiredService<PathResolver>());
        var regular = await CreateActorAsync(host, "regular", RootAccess.Write);
        var admin = await GetAdminActorAsync(host);

        var names = reader.List(new FileRef(RootName, string.Empty), regular).Entries.Select(entry => entry.Name).ToArray();
        Assert.Contains("docs", names);
        Assert.Contains("db2", names);
        Assert.DoesNotContain("config", names);
        Assert.DoesNotContain("db", names);
        Assert.Throws<FileSystemAccessDeniedException>(
            () => reader.List(new FileRef(RootName, "config"), regular));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => reader.List(new FileRef(RootName, "db"), regular));

        var hiddenSearch = searchService.Search(
            new FileRef(RootName, string.Empty), "secret", null, null,
            FileSystemSearchService.DefaultResultLimit, CancellationToken.None, regular);
        Assert.Empty(hiddenSearch.Results);
        var visibleSearch = searchService.Search(
            new FileRef(RootName, string.Empty), "visible", null, null,
            FileSystemSearchService.DefaultResultLimit, CancellationToken.None, regular);
        Assert.Single(visibleSearch.Results);

        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(new FileRef(RootName, string.Empty), [], CancellationToken.None, regular));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(new FileRef(RootName, string.Empty), ["config"], CancellationToken.None, regular));
        Assert.NotNull(archiveService.Plan(new FileRef(RootName, string.Empty), ["db2"], CancellationToken.None, regular));
        Assert.NotNull(archiveService.Plan(new FileRef(RootName, string.Empty), ["docs"], CancellationToken.None, regular));

        writer.CreateDirectory(new FileRef(RootName, string.Empty), "newdir", regular);
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "newdir")));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => writer.Rename(new FileRef(RootName, "config"), "config-renamed", regular));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => writer.Rename(new FileRef(RootName, "db"), "db-renamed", regular));
        writer.Rename(new FileRef(RootName, "db2"), "db3", regular);
        Assert.True(Directory.Exists(Path.Combine(workspace.RootPath, "db3")));

        Assert.Throws<FileSystemAccessDeniedException>(
            () => uploadService.Prepare(new FileRef(RootName, "config"), "upload.txt", 1, regular));
        uploadService.Prepare(new FileRef(RootName, string.Empty), "upload.txt", 1, regular);

        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "config/secret.txt")],
            new FileRef(RootName, string.Empty),
            actor: regular));
        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "docs/note.txt"), new FileRef(RootName, "config/secret.txt")],
            new FileRef(RootName, string.Empty),
            actor: regular));
        Assert.Empty((await repository.GetPageAsync(0, 10)).Tasks);
        Assert.True(File.Exists(Path.Combine(configDirectory, "secret.txt")));
        Assert.True(File.Exists(Path.Combine(docs, "note.txt")));

        var adminNames = reader.List(new FileRef(RootName, string.Empty), admin).Entries.Select(entry => entry.Name).ToArray();
        Assert.Contains("config", adminNames);
        Assert.Contains("db", adminNames);
        Assert.Contains(
            "secret.txt",
            reader.List(new FileRef(RootName, "config"), admin).Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task AncestorOfServiceDirectory_IsProtected_ButVisibleSiblingWorks()
    {
        using var workspace = new TestWorkspace();
        var docs = Path.Combine(workspace.RootPath, "docs");
        var configDirectory = Path.Combine(docs, "config");
        var databaseDirectory = Path.Combine(docs, "db");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(databaseDirectory);
        await File.WriteAllTextAsync(Path.Combine(docs, "note.txt"), "note");
        await File.WriteAllTextAsync(Path.Combine(docs, "sibling.txt"), "sibling");
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "secret.txt"), "secret");

        var configPath = Path.Combine(configDirectory, "config.yaml");
        await File.WriteAllTextAsync(configPath, $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        var databasePath = Path.Combine(databaseDirectory, "app.db");
        using var host = await CreateConfiguredHostAsync(configPath, databasePath);

        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var writer = host.Services.GetRequiredService<FileSystemWriteService>();
        var archiveService = host.Services.GetRequiredService<FileSystemArchiveService>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var dispatcher = new TaskDispatcher(repository, new TaskQueue(), host.Services.GetRequiredService<PathResolver>());
        var regular = await CreateActorAsync(host, "regular", RootAccess.Write);

        Assert.Contains("docs", reader.List(new FileRef(RootName, string.Empty), regular).Entries.Select(entry => entry.Name));
        var docNames = reader.List(new FileRef(RootName, "docs"), regular).Entries.Select(entry => entry.Name).ToArray();
        Assert.Contains("note.txt", docNames);
        Assert.Contains("sibling.txt", docNames);
        Assert.DoesNotContain("config", docNames);
        Assert.DoesNotContain("db", docNames);

        Assert.Throws<FileSystemAccessDeniedException>(
            () => writer.Rename(new FileRef(RootName, "docs"), "docs2", regular));
        Assert.True(Directory.Exists(docs));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(new FileRef(RootName, "docs"), [], CancellationToken.None, regular));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(new FileRef(RootName, string.Empty), ["docs"], CancellationToken.None, regular));
        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "docs")],
            new FileRef(RootName, string.Empty),
            actor: regular));
        await Assert.ThrowsAsync<FileSystemAccessDeniedException>(() => dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "docs/note.txt"), new FileRef(RootName, "docs")],
            new FileRef(RootName, string.Empty),
            actor: regular));
        Assert.Empty((await repository.GetPageAsync(0, 10)).Tasks);
        Assert.True(File.Exists(Path.Combine(docs, "note.txt")));

        await dispatcher.EnqueueAsync(
            FileTaskType.Delete,
            [new FileRef(RootName, "docs/note.txt")],
            new FileRef(RootName, "docs"),
            actor: regular);
        Assert.Single((await repository.GetPageAsync(0, 10)).Tasks);
    }

    [Fact]
    public async Task ServicePathConfiguredThroughSymlink_ProtectsActualParent()
    {
        using var workspace = new TestWorkspace();
        var configDirectory = Path.Combine(workspace.RootPath, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "secret.txt"), "secret");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "visible.txt"), "visible");
        var targetConfig = Path.Combine(configDirectory, "config.yaml");
        await File.WriteAllTextAsync(targetConfig, $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        var linkedConfig = Path.Combine(workspace.ConfigDirectory, "linked-config.yaml");
        try
        {
            File.CreateSymbolicLink(linkedConfig, targetConfig);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        using var host = await CreateConfiguredHostAsync(linkedConfig);
        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var regular = await CreateActorAsync(host, "regular", RootAccess.Read);
        var names = reader.List(new FileRef(RootName, string.Empty), regular).Entries.Select(entry => entry.Name).ToArray();
        Assert.Contains("visible.txt", names);
        Assert.DoesNotContain("config", names);
    }

    [Fact]
    public async Task ServicePathThroughChainedIntermediateSymlinks_ProtectsActualDirectory()
    {
        using var workspace = new TestWorkspace();
        var actual = Path.Combine(workspace.RootPath, "actual", "config");
        Directory.CreateDirectory(actual);
        await File.WriteAllTextAsync(Path.Combine(actual, "config.yaml"),
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        await File.WriteAllTextAsync(Path.Combine(actual, "secret.txt"), "secret");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "visible.txt"), "visible");
        var firstLink = Path.Combine(workspace.RootPath, "alias-one");
        var secondLink = Path.Combine(workspace.RootPath, "alias-two");
        try
        {
            Directory.CreateSymbolicLink(firstLink, Path.Combine(workspace.RootPath, "actual"));
            Directory.CreateSymbolicLink(secondLink, firstLink);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        using var host = await CreateConfiguredHostAsync(Path.Combine(secondLink, "config", "config.yaml"));
        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var writer = host.Services.GetRequiredService<FileSystemWriteService>();
        var regular = await CreateActorAsync(host, "chain-reader", RootAccess.Write);
        Assert.Contains("actual", reader.List(new FileRef(RootName, string.Empty), regular).Entries.Select(entry => entry.Name));
        Assert.Throws<FileSystemAccessDeniedException>(() => reader.List(new FileRef(RootName, "actual/config"), regular));
        Assert.Throws<FileSystemAccessDeniedException>(() => writer.Rename(new FileRef(RootName, "actual/config"), "renamed", regular));
        Assert.Contains("visible.txt", reader.List(new FileRef(RootName, string.Empty), regular).Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task ServiceDirectoryAtRoot_HidesWholeRootFromRegularUser()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "visible.txt"), "visible");
        var configPath = Path.Combine(workspace.RootPath, "config.yaml");
        var databasePath = Path.Combine(workspace.RootPath, "app.db");
        await File.WriteAllTextAsync(configPath, $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        using var host = await CreateConfiguredHostAsync(configPath, databasePath);

        var reader = host.Services.GetRequiredService<FileSystemReadService>();
        var searchService = host.Services.GetRequiredService<FileSystemSearchService>();
        var regular = await CreateActorAsync(host, "regular", RootAccess.Write);
        var admin = await GetAdminActorAsync(host);

        Assert.Empty(reader.GetRoots(regular));
        Assert.Throws<FileSystemAccessDeniedException>(
            () => reader.List(new FileRef(RootName, string.Empty), regular));
        var search = searchService.Search(
            new FileRef(VirtualRoot.VirtualRootName, string.Empty),
            "visible",
            null,
            null,
            FileSystemSearchService.DefaultResultLimit,
            CancellationToken.None,
            regular);
        Assert.Empty(search.Results);
        Assert.Empty(search.UnavailableRoots);
        Assert.Single(reader.GetRoots(admin));
        Assert.Contains(
            "visible.txt",
            reader.List(new FileRef(RootName, string.Empty), admin).Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task ZipTicket_IsBoundToCreator_AndRedemptionRechecksRights()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "report.txt"), "report");
        using var host = await CreateConfiguredHostAsync(workspace);
        var tickets = host.Services.GetRequiredService<ArchiveTicketStore>();
        var archiveService = host.Services.GetRequiredService<FileSystemArchiveService>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var first = await CreateActorAsync(host, "first", RootAccess.Read);
        var second = await CreateActorAsync(host, "second", RootAccess.Read);

        var token = tickets.Create(new ArchiveSelection(RootName, string.Empty, [], first.UserId));
        Assert.False(tickets.TryRedeem(token, second, out _));
        Assert.True(tickets.TryRedeem(token, first, out var selection));
        Assert.NotNull(archiveService.Plan(selection.ToFileRef(), selection.Names, CancellationToken.None, first));

        var staleToken = tickets.Create(new ArchiveSelection(RootName, string.Empty, [], first.UserId));
        Assert.True(tickets.TryRedeem(staleToken, first, out var staleSelection));
        await permissions.SetAsync(first.UserId!.Value, RootName, RootAccess.None, CancellationToken.None);
        var withoutRights = await host.Services.GetRequiredService<FileAccessService>()
            .CreateForUserAsync(first.UserId.Value, CancellationToken.None);
        Assert.Throws<FileSystemAccessDeniedException>(
            () => archiveService.Plan(staleSelection.ToFileRef(), staleSelection.Names, CancellationToken.None, withoutRights));
    }

    private static async Task<TestHostFactory.TestHost> CreateConfiguredHostAsync(TestWorkspace workspace)
    {
        var configPath = workspace.WriteConfiguration(workspace.RootPath);
        return await CreateConfiguredHostAsync(configPath, null);
    }

    private static async Task<TestHostFactory.TestHost> CreateConfiguredHostAsync(
        string configPath,
        string? databasePath = null)
    {
        var host = TestHostFactory.Create(
            configPath,
            databasePath,
            settings: new Dictionary<string, string?> { [AuthModeReader.SettingName] = "LOCAL" });
        try
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
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task<FileActor> CreateActorAsync(
        TestHostFactory.TestHost host,
        string login,
        RootAccess access)
    {
        var users = host.Services.GetRequiredService<UserStore>();
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        var userId = await users.CreateUserAsync(login, Password, mustChangePassword: false, CancellationToken.None);
        if (access != RootAccess.None)
        {
            await permissions.SetAsync(userId, RootName, access, CancellationToken.None);
        }

        return await host.Services.GetRequiredService<FileAccessService>()
            .CreateForUserAsync(userId, CancellationToken.None);
    }

    private static async Task<FileActor> GetAdminActorAsync(TestHostFactory.TestHost host)
    {
        var admin = await host.Services.GetRequiredService<UserStore>()
            .FindByLoginAsync("admin", CancellationToken.None)
            ?? throw new InvalidOperationException("admin не создан.");
        return await host.Services.GetRequiredService<FileAccessService>()
            .CreateForUserAsync(admin.Id, CancellationToken.None);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
