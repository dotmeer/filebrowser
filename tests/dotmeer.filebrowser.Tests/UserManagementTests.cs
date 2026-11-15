using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Auth;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Settings;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Итерация 4: админская страница пользователей, выдача прав, временный
/// пароль, сброс и удаление, ограничение глобального языка и открытие
/// рабочего доступа обычным учётным записям.
/// </summary>
[Collection("Web application factory")]
public sealed class UserManagementTests
{
    private const string RootName = "files";
    private const string AdminPassword = "Admin-Pass-1!";

    [Fact]
    public async Task BrowsePage_RegularUserWithoutRoot_ListsOnlyAccessibleRoots()
    {
        using var workspace = new TestWorkspace();
        var hiddenRoot = Path.Combine(workspace.ConfigDirectory, "hidden-root");
        Directory.CreateDirectory(hiddenRoot);
        File.WriteAllText(Path.Combine(workspace.RootPath, "inside-first-root.txt"), "test");
        var configPath = workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: hidden\n    path: '{hiddenRoot}'\n");
        using var host = await CreateConfiguredHostAsync(workspace, configPath);
        var userId = await host.Services.GetRequiredService<UserStore>()
            .CreateUserAsync("reader", "Reader-Pass-1!", mustChangePassword: false, CancellationToken.None);
        var permissions = host.Services.GetRequiredService<RootPermissionStore>();
        await permissions.SetAllAsync(userId, new Dictionary<string, RootAccess>
        {
            ["files"] = RootAccess.Read
        }, CancellationToken.None);
        using var client = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(client, "reader", "Reader-Pass-1!");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains(">AllRoots<", html);
        Assert.Contains("📁</span> files", html);
        Assert.DoesNotContain("📁</span> hidden", html);
        Assert.DoesNotContain("inside-first-root.txt", html);

        await permissions.SetAllAsync(userId, new Dictionary<string, RootAccess>(), CancellationToken.None);
        html = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("No accessible roots.", html);
    }

    [Fact]
    public async Task SetAllRights_UpdatesConfiguredRootsAndPreservesTemporarilyMissingRoots()
    {
        using var workspace = new TestWorkspace();
        var secondRoot = Path.Combine(workspace.ConfigDirectory, "second-root");
        Directory.CreateDirectory(secondRoot);
        var databasePath = Path.Combine(workspace.ConfigDirectory, "shared.db");
        var bothRootsYaml = $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: archive\n    path: '{secondRoot}'\n";
        var configPath = workspace.WriteRawConfiguration(bothRootsYaml);

        Guid userId;
        using (var firstHost = TestHostFactory.Create(configPath, databasePath))
        {
            var users = firstHost.Services.GetRequiredService<UserStore>();
            userId = await users.CreateUserAsync("bob", "Bob-Pass-1!", mustChangePassword: false, CancellationToken.None);
            await firstHost.Services.GetRequiredService<RootPermissionStore>().SetAllAsync(userId,
                new Dictionary<string, RootAccess>
                {
                    ["files"] = RootAccess.Write,
                    ["archive"] = RootAccess.Read
                }, CancellationToken.None);
        }

        configPath = workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        using (var reducedHost = TestHostFactory.Create(configPath, databasePath))
        {
            var permissions = reducedHost.Services.GetRequiredService<RootPermissionStore>();
            await permissions.SetAllAsync(userId,
                new Dictionary<string, RootAccess> { ["files"] = RootAccess.None }, CancellationToken.None);
            var whileMissing = await permissions.GetForUserAsync(userId, CancellationToken.None);
            Assert.DoesNotContain("files", whileMissing.Keys);
            Assert.Equal(RootAccess.Read, whileMissing["archive"]);
        }

        configPath = workspace.WriteRawConfiguration(bothRootsYaml);
        using (var returnedHost = TestHostFactory.Create(configPath, databasePath))
        {
            var restored = await returnedHost.Services.GetRequiredService<RootPermissionStore>()
                .GetForUserAsync(userId, CancellationToken.None);
            Assert.DoesNotContain("files", restored.Keys);
            Assert.Equal(RootAccess.Read, restored["archive"]);
        }
    }

    [Fact]
    public async Task AdminCreatesUserWithTemporaryPassword_RightsAndForcedChange()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "readme.txt"), "hello");
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);

        var created = await administration.CreateAsync(
            adminPrincipal,
            "bob",
            "Bob-Temp-1!",
            new Dictionary<string, RootAccess> { [RootName] = RootAccess.Write },
            CancellationToken.None);
        Assert.Equal(UserManagementOutcome.Success, created.Outcome);

        var bob = await store.FindByLoginAsync("bob", CancellationToken.None);
        Assert.NotNull(bob);
        Assert.True(bob!.MustChangePassword);
        Assert.False(bob.IsAdmin);

        var list = await administration.ListAsync(adminPrincipal, CancellationToken.None);
        Assert.True(list.Allowed);
        var managed = Assert.Single(list.Users, user => user.Login == "bob");
        Assert.Equal(RootAccess.Write, managed.Rights[RootName]);
        Assert.False(managed.IsAdmin);

        using var client = host.CreateClient(allowAutoRedirect: false);
        using (var login = await PostLoginAsync(client, "bob", "Bob-Temp-1!"))
        {
            Assert.Equal("/change-password", login.Headers.LocationPath());
        }

        using (var root = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.Found, root.StatusCode);
            Assert.StartsWith("/change-password", root.Headers.LocationPath());
        }

        using (var download = await client.GetAsync("/api/fs/download?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
        }

        var changeToken = await GetAntiforgeryTokenAsync(client, "/change-password");
        using (var change = await client.PostAsync("/auth/change-password", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = changeToken,
                   ["currentPassword"] = "Bob-Temp-1!",
                   ["newPassword"] = "Bob-Own-2!",
                   ["confirmPassword"] = "Bob-Own-2!"
               })))
        {
            Assert.Equal(HttpStatusCode.Found, change.StatusCode);
            Assert.Equal("/", change.Headers.LocationPath());
        }

        using (var root = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        }

        using (var download = await client.GetAsync("/api/fs/download?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using (var check = await client.GetAsync("/api/fs/upload/check?root=files&path=&name=new.txt&size=1"))
        {
            Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);
        }

        // Админская страница по прямому адресу для обычной учётки закрыта.
        using (var usersPage = await client.GetAsync("/users"))
        {
            Assert.Equal(HttpStatusCode.Found, usersPage.StatusCode);
            Assert.Equal("/access-denied", usersPage.Headers.LocationPath());
        }
    }

    [Fact]
    public async Task RegularUser_CannotUseAdministrationService()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);

        var aliceId = await store.CreateUserAsync("alice", "Alice-Pass-1!", mustChangePassword: false, CancellationToken.None);
        var alice = (await store.GetByIdAsync(aliceId, CancellationToken.None))!;
        var alicePrincipal = await CreatePrincipalAsync(host, alice);

        Assert.False((await administration.ListAsync(alicePrincipal, CancellationToken.None)).Allowed);
        Assert.Equal(
            UserManagementOutcome.Denied,
            (await administration.CreateAsync(
                alicePrincipal,
                "mallory",
                "Mallory-Pass-1!",
                new Dictionary<string, RootAccess> { [RootName] = RootAccess.Write },
                CancellationToken.None)).Outcome);
        Assert.Null(await store.FindByLoginAsync("mallory", CancellationToken.None));
        Assert.Equal(
            UserManagementOutcome.Denied,
            (await administration.DeleteAsync(alicePrincipal, aliceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.Denied,
            (await administration.ResetPasswordAsync(alicePrincipal, aliceId, "Other-Pass-1!", CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.Denied,
            (await administration.UpdateRightsAsync(
                alicePrincipal,
                aliceId,
                new Dictionary<string, RootAccess> { [RootName] = RootAccess.Write },
                CancellationToken.None)).Outcome);
        Assert.NotNull(await store.GetByIdAsync(aliceId, CancellationToken.None));
        Assert.Empty(await host.Services.GetRequiredService<RootPermissionStore>()
            .GetForUserAsync(aliceId, CancellationToken.None));

        // Проверяем, что администратор остаётся работоспособным после отказов.
        Assert.True((await administration.ListAsync(adminPrincipal, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task AdminAccountCannotBeDeletedResetOrEdited_DuplicateAndReservedLoginsRejected()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);
        var admin = await store.FindByLoginAsync(UserStore.AdminLogin, CancellationToken.None);
        Assert.NotNull(admin);

        Assert.Equal(
            UserManagementOutcome.AdminProtected,
            (await administration.DeleteAsync(adminPrincipal, admin!.Id, CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.AdminProtected,
            (await administration.ResetPasswordAsync(adminPrincipal, admin.Id, "Other-Pass-1!", CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.AdminProtected,
            (await administration.UpdateRightsAsync(
                adminPrincipal,
                admin.Id,
                new Dictionary<string, RootAccess>(),
                CancellationToken.None)).Outcome);
        Assert.NotNull(await store.FindByLoginAsync(UserStore.AdminLogin, CancellationToken.None));

        Assert.Equal(
            UserManagementOutcome.Success,
            (await administration.CreateAsync(
                adminPrincipal,
                "bob",
                "Bob-Temp-1!",
                new Dictionary<string, RootAccess>(),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.DuplicateLogin,
            (await administration.CreateAsync(
                adminPrincipal,
                "BOB",
                "Bob-Temp-2!",
                new Dictionary<string, RootAccess>(),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.InvalidLogin,
            (await administration.CreateAsync(
                adminPrincipal,
                "admin",
                "Admin-Temp-1!",
                new Dictionary<string, RootAccess>(),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            UserManagementOutcome.InvalidLogin,
            (await administration.CreateAsync(
                adminPrincipal,
                "bad login",
                "Bad-Temp-1!",
                new Dictionary<string, RootAccess>(),
                CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ResetPassword_RevokesOldSessionAndRequiresChange()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var passwords = host.Services.GetRequiredService<PasswordService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);

        await administration.CreateAsync(
            adminPrincipal,
            "bob",
            "Bob-Temp-1!",
            new Dictionary<string, RootAccess> { [RootName] = RootAccess.Read },
            CancellationToken.None);
        var bob = (await store.FindByLoginAsync("bob", CancellationToken.None))!;
        var oldSessionId = await store.TryCreateSessionAsync(
            bob, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(oldSessionId);

        using var sessionClient = host.CreateClient(allowAutoRedirect: false);
        using var staleLogin = await PostLoginAsync(sessionClient, "bob", "Bob-Temp-1!");
        var staleCookie = Assert.IsType<string>(ReadCookie(staleLogin, "filebrowser.auth"));

        var result = await administration.ResetPasswordAsync(adminPrincipal, bob.Id, "Bob-Reset-2!", CancellationToken.None);
        Assert.Equal(UserManagementOutcome.Success, result.Outcome);

        Assert.Null(await store.GetActiveSessionAsync(oldSessionId!.Value, CancellationToken.None));

        using var stale = host.CreateClient(allowAutoRedirect: false);
        stale.DefaultRequestHeaders.Add("Cookie", staleCookie);
        using var staleDownload = await stale.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, staleDownload.StatusCode);
        var afterReset = (await store.FindByLoginAsync("bob", CancellationToken.None))!;
        Assert.True(afterReset.MustChangePassword);
        Assert.True(passwords.Verify(afterReset.PasswordHash, "Bob-Reset-2!"));
        Assert.False(passwords.Verify(afterReset.PasswordHash, "Bob-Temp-1!"));

        using var oldPassword = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(oldPassword, "bob", "Bob-Temp-1!");
        Assert.Contains("error=credentials", login.Headers.LocationPath());

        using var temporary = host.CreateClient(allowAutoRedirect: false);
        using var temporaryLogin = await PostLoginAsync(temporary, "bob", "Bob-Reset-2!");
        Assert.Equal("/change-password", temporaryLogin.Headers.LocationPath());
    }

    [Fact]
    public async Task DeleteUser_RevokesSessionAndKeepsTaskAuthorship()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var access = host.Services.GetRequiredService<FileAccessService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);

        await administration.CreateAsync(
            adminPrincipal,
            "bob",
            "Bob-Temp-1!",
            new Dictionary<string, RootAccess>(),
            CancellationToken.None);
        var bob = (await store.FindByLoginAsync("bob", CancellationToken.None))!;
        var sessionId = await store.TryCreateSessionAsync(
            bob, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(sessionId);
        var task = await repository.CreateQueuedAsync(
            FileTaskType.Copy,
            [new FileRef(RootName, "old.txt")],
            new FileRef(RootName, "target"),
            authorUserId: bob.Id);

        var deleted = await administration.DeleteAsync(adminPrincipal, bob.Id, CancellationToken.None);
        Assert.Equal(UserManagementOutcome.Success, deleted.Outcome);
        Assert.Null(await store.GetActiveSessionAsync(sessionId!.Value, CancellationToken.None));
        Assert.Null(await store.FindByLoginAsync("bob", CancellationToken.None));
        Assert.NotNull(await repository.GetAsync(task.Id, TaskVisibility.Everything, CancellationToken.None));
        Assert.Equal(bob.Id, (await repository.GetAsync(task.Id))!.AuthorUserId);
        Assert.False((await access.CreateForTaskAsync(bob.Id, CancellationToken.None)).IsFullAccess);

        var recreatedId = await store.CreateUserAsync("bob", "Bob-New-1!", mustChangePassword: false, CancellationToken.None);
        Assert.NotEqual(bob.Id, recreatedId);
        Assert.Null(await repository.GetAsync(task.Id, TaskVisibility.ForUser(recreatedId), CancellationToken.None));
        Assert.NotNull(await repository.GetAsync(task.Id, TaskVisibility.Everything, CancellationToken.None));
    }

    [Fact]
    public async Task ReadOnlyUser_GetsReadHttpRoutes_ButNotWrite()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "readme.txt"), "hello");
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var administration = host.Services.GetRequiredService<UserAdministrationService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);

        Assert.Equal(
            UserManagementOutcome.Success,
            (await administration.CreateAsync(
                adminPrincipal,
                "reader",
                "Reader-Temp-1!",
                new Dictionary<string, RootAccess> { [RootName] = RootAccess.Read },
                CancellationToken.None)).Outcome);
        var reader = (await store.FindByLoginAsync("reader", CancellationToken.None))!;

        using var client = host.CreateClient(allowAutoRedirect: false);
        using (var login = await PostLoginAsync(client, "reader", "Reader-Temp-1!"))
        {
            Assert.Equal("/change-password", login.Headers.LocationPath());
        }

        var changeToken = await GetAntiforgeryTokenAsync(client, "/change-password");
        using (var change = await client.PostAsync("/auth/change-password", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = changeToken,
                   ["currentPassword"] = "Reader-Temp-1!",
                   ["newPassword"] = "Reader-Own-2!",
                   ["confirmPassword"] = "Reader-Own-2!"
               })))
        {
            Assert.Equal(HttpStatusCode.Found, change.StatusCode);
        }

        using (var download = await client.GetAsync("/api/fs/download?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using (var preview = await client.GetAsync("/api/fs/preview?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        var ticket = host.Services.GetRequiredService<dotmeer.filebrowser.Archive.ArchiveTicketStore>()
            .Create(new dotmeer.filebrowser.Archive.ArchiveSelection(RootName, string.Empty, [], reader.Id));
        using (var zip = await client.GetAsync($"/api/fs/zip?ticket={ticket}"))
        {
            Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        }

        using (var check = await client.GetAsync("/api/fs/upload/check?root=files&path=&name=new.txt&size=1"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
            Assert.Equal("access_denied", await ReadProblemCodeAsync(check));
        }

        var uploadToken = await GetAntiforgeryTokenAsync(client, "/");
        using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("x"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/fs/upload?root=files&path=&name=new.txt")
        {
            Content = content
        };
        request.Headers.Add("X-CSRF-TOKEN", uploadToken);
        using (var upload = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            Assert.Equal("access_denied", await ReadProblemCodeAsync(upload));
        }

        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "new.txt")));
    }

    [Fact]
    public async Task UsersPage_IsAdminOnlyInLocalAndHiddenInAnonymous()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();

        using var admin = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(admin, UserStore.AdminLogin, AdminPassword);
        using (var adminPage = await admin.GetAsync("/users"))
        {
            Assert.Equal(HttpStatusCode.OK, adminPage.StatusCode);
            Assert.Contains("New user", await adminPage.Content.ReadAsStringAsync());
        }

        await store.CreateUserAsync("alice", "Alice-Pass-1!", mustChangePassword: false, CancellationToken.None);
        using var regular = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(regular, "alice", "Alice-Pass-1!");
        using (var regularPage = await regular.GetAsync("/users"))
        {
            Assert.Equal(HttpStatusCode.Found, regularPage.StatusCode);
            Assert.Equal("/access-denied", regularPage.Headers.LocationPath());
        }

        using var anonymousWorkspace = new TestWorkspace();
        using var anonymousHost = TestHostFactory.Create(anonymousWorkspace.WriteConfiguration(anonymousWorkspace.RootPath));
        using var anonymous = anonymousHost.CreateClient(allowAutoRedirect: false);
        using var anonymousPage = await anonymous.GetAsync("/users");
        Assert.Equal(HttpStatusCode.Found, anonymousPage.StatusCode);
        Assert.Equal("/", anonymousPage.Headers.LocationPath());
    }

    [Fact]
    public async Task GlobalLanguage_ChangesOnlyForAdminInLocal()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var language = host.Services.GetRequiredService<LanguageSettingsService>();
        var settings = host.Services.GetRequiredService<AppSettingsService>();
        var adminPrincipal = await CreatePrincipalAsync(host, UserStore.AdminLogin);
        var aliceId = await store.CreateUserAsync("alice", "Alice-Pass-1!", mustChangePassword: false, CancellationToken.None);
        var alice = (await store.GetByIdAsync(aliceId, CancellationToken.None))!;
        var alicePrincipal = await CreatePrincipalAsync(host, alice);

        Assert.True(await language.TrySetAsync(adminPrincipal, "ru", CancellationToken.None));
        Assert.Equal("ru", settings.GetLanguage());

        Assert.False(await language.TrySetAsync(alicePrincipal, "en", CancellationToken.None));
        Assert.Equal("ru", settings.GetLanguage());

        Assert.False(await language.TrySetAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), "en", CancellationToken.None));
        Assert.Equal("ru", settings.GetLanguage());
    }

    [Fact]
    public async Task GlobalLanguage_RemainsAvailableWithoutLoginInAnonymousMode()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var language = host.Services.GetRequiredService<LanguageSettingsService>();
        var settings = host.Services.GetRequiredService<AppSettingsService>();

        Assert.True(await language.TrySetAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), "ru", CancellationToken.None));
        Assert.Equal("ru", settings.GetLanguage());
    }

    [Fact]
    public async Task SettingsPage_ShowsLanguageEditorOnlyForAdmin()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();

        using var admin = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(admin, UserStore.AdminLogin, AdminPassword);
        var adminHtml = await admin.GetStringAsync("/settings");
        Assert.Contains("id=\"language-select\"", adminHtml);

        await store.CreateUserAsync("alice", "Alice-Pass-1!", mustChangePassword: false, CancellationToken.None);
        using var regular = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(regular, "alice", "Alice-Pass-1!");
        var regularHtml = await regular.GetStringAsync("/settings");
        Assert.DoesNotContain("id=\"language-select\"", regularHtml);
        Assert.Contains("Current language", regularHtml);
    }

    private static async Task<TestHostFactory.TestHost> CreateConfiguredHostAsync(TestWorkspace workspace, string? configPath = null)
    {
        var host = TestHostFactory.Create(
            configPath ?? workspace.WriteConfiguration(workspace.RootPath),
            settings: new Dictionary<string, string?> { [AuthModeReader.SettingName] = "LOCAL" });
        try
        {
            using var client = host.CreateClient(allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client, "/setup");
            using var response = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["password"] = AdminPassword,
                ["confirmPassword"] = AdminPassword
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

    private static async Task<ClaimsPrincipal> CreatePrincipalAsync(TestHostFactory.TestHost host, string login)
    {
        var store = host.Services.GetRequiredService<UserStore>();
        var user = await store.FindByLoginAsync(login, CancellationToken.None)
            ?? throw new InvalidOperationException($"Учётная запись {login} не найдена.");
        return await CreatePrincipalAsync(host, user);
    }

    private static async Task<ClaimsPrincipal> CreatePrincipalAsync(TestHostFactory.TestHost host, UserRecord user)
    {
        var store = host.Services.GetRequiredService<UserStore>();
        var sessionId = await store.TryCreateSessionAsync(
            user, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(sessionId);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.Login),
            new Claim(ClaimTypes.Role, user.IsAdmin ? "admin" : "user"),
            new Claim(AuthClaimTypes.SessionId, sessionId!.Value.ToString("D"))
        ], "Cookies"));
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("code").GetString();
    }

    private static string? ReadCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (value.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return value.Split(';', 2)[0];
            }
        }

        return null;
    }

    private static async Task LoginAsync(HttpClient client, string login, string password)
    {
        using var response = await PostLoginAsync(client, login, password);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.LocationPath());
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string login, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["login"] = login,
            ["password"] = password
        }));
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token was not rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
