using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.Auth;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

public class AuthModeTests
{
    [Fact]
    public void Read_MissingValue_DefaultsToAnonymous()
    {
        var options = AuthModeReader.Read(new ConfigurationBuilder().Build());

        Assert.Equal(AuthMode.Anonymous, options.Mode);
    }

    [Theory]
    [InlineData("LOCAL", AuthMode.Local)]
    [InlineData("local", AuthMode.Local)]
    [InlineData("ANONYMOUS", AuthMode.Anonymous)]
    [InlineData("  anonymous  ", AuthMode.Anonymous)]
    public void Read_AcceptsKnownValues(string raw, AuthMode expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AuthModeReader.SettingName] = raw })
            .Build();

        Assert.Equal(expected, AuthModeReader.Read(configuration).Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING")]
    public void Read_RejectsEmptyOrUnknownValue(string raw)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AuthModeReader.SettingName] = raw })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => AuthModeReader.Read(configuration));
        Assert.Contains("AUTH_MODE", exception.Message);
    }

    [Fact]
    public void InvalidMode_StopsHostStartup()
    {
        using var workspace = new TestWorkspace();

        var exception = Assert.ThrowsAny<Exception>(() => TestHostFactory.Create(
            workspace.WriteConfiguration(workspace.RootPath),
            settings: new Dictionary<string, string?> { [AuthModeReader.SettingName] = "INVALID" }));

        Assert.Contains("AUTH_MODE", exception.ToString());
    }

    [Fact]
    public void Recovery_RejectsPasswordInCommandLineArguments()
    {
        Assert.True(AdminPasswordRecovery.IsCommand(["reset-admin-password"]));
        Assert.True(AdminPasswordRecovery.IsCommand(["RESET-ADMIN-PASSWORD"]));
        Assert.Equal(2, AdminPasswordRecovery.Run(["reset-admin-password", "secret"]));
    }
}

[Collection("Web application factory")]
public sealed class LocalAuthTests
{
    private const string AdminPassword = "Admin-Pass-1!";
    private const string AuthCookieName = "filebrowser.auth";

    [Fact]
    public async Task AnonymousMode_KeepsWorkingWithoutLogin_AndAuthPagesRedirectHome()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "readme.txt"), "hello");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        using var client = host.CreateClient(allowAutoRedirect: false);

        var html = await client.GetStringAsync("/");
        Assert.Contains("placeholder=\"Search in current folder\"", html);

        using var login = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.Equal("/", login.Headers.LocationPath());

        using var setup = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Found, setup.StatusCode);
        Assert.Equal("/", setup.Headers.LocationPath());

        using var download = await client.GetAsync("/api/fs/download?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.False(download.Headers.Contains("Cache-Control"));
    }

    [Fact]
    public async Task Local_FirstVisitor_SeesSetup_LogsIn_AndKeepsAccountAfterRestart()
    {
        using var workspace = new TestWorkspace();
        var configPath = workspace.WriteConfiguration(workspace.RootPath);
        var databasePath = Path.Combine(workspace.ConfigDirectory, "app.db");

        string cookie;
        using (var host = CreateLocalHost(configPath))
        {
            using var client = host.CreateClient(allowAutoRedirect: false);
            using var root = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, root.StatusCode);
            Assert.StartsWith("/setup", root.Headers.LocationPath());

            var setupToken = await GetAntiforgeryTokenAsync(client, "/setup");
            using var setup = await PostFormAsync(client, "/auth/setup", new()
            {
                ["__RequestVerificationToken"] = setupToken,
                ["password"] = AdminPassword,
                ["confirmPassword"] = AdminPassword
            });
            Assert.Equal(HttpStatusCode.Found, setup.StatusCode);
            Assert.Equal("/", setup.Headers.LocationPath());
            cookie = Assert.IsType<string>(ReadCookie(setup, AuthCookieName));

            var html = await client.GetStringAsync("/");
            Assert.Contains("placeholder=\"Search in current folder\"", html);

            using var closedSetup = await client.GetAsync("/setup");
            Assert.Equal(HttpStatusCode.Found, closedSetup.StatusCode);
            Assert.Equal("/login", closedSetup.Headers.LocationPath());
        }

        // Перезапуск с той же SQLite: учётка сохраняется, сессия проверяется
        // заново cookie-аутентификацией.
        using (var restarted = TestHostFactory.Create(configPath, databasePath, settings: LocalSettings()))
        {
            using var anonymous = restarted.CreateClient(allowAutoRedirect: false);
            using var root = await anonymous.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, root.StatusCode);
            Assert.StartsWith("/login", root.Headers.LocationPath());

            using var loginPage = await anonymous.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
            var loginToken = await GetAntiforgeryTokenAsync(anonymous, "/login");
            using var login = await PostFormAsync(anonymous, "/auth/login", new()
            {
                ["__RequestVerificationToken"] = loginToken,
                ["login"] = "admin",
                ["password"] = AdminPassword
            });
            Assert.Equal(HttpStatusCode.Found, login.StatusCode);
            Assert.Equal("/", login.Headers.LocationPath());
            cookie = Assert.IsType<string>(ReadCookie(login, AuthCookieName));

            var html = await anonymous.GetStringAsync("/");
            Assert.Contains("placeholder=\"Search in current folder\"", html);

            // Выход прекращает доступ текущей сессии.
            var logoutToken = await GetAntiforgeryTokenAsync(anonymous, "/");
            using var logout = await PostFormAsync(anonymous, "/auth/logout", new()
            {
                ["__RequestVerificationToken"] = logoutToken
            });
            Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
            Assert.Equal("/login", logout.Headers.LocationPath());

            using var afterLogout = await anonymous.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, afterLogout.StatusCode);
            Assert.StartsWith("/login", afterLogout.Headers.LocationPath());
        }

        // Cookie из прошлого запуска (до выхода) больше не действует.
        using (var restarted = TestHostFactory.Create(configPath, databasePath, settings: LocalSettings()))
        {
            using var stale = restarted.CreateClient(allowAutoRedirect: false);
            stale.DefaultRequestHeaders.Add("Cookie", cookie);
            using var download = await stale.GetAsync("/api/fs/download?root=files&path=missing.txt");
            Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);
        }
    }

    [Fact]
    public async Task Local_Setup_ConcurrentPosts_CreateSingleAdmin()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateLocalHost(workspace.WriteConfiguration(workspace.RootPath));

        // Старая задача не мешает первичной настройке.
        InsertTask(host.Services.GetRequiredService<TaskDatabase>(), "old-task");

        using var first = host.CreateClient(allowAutoRedirect: false);
        using var second = host.CreateClient(allowAutoRedirect: false);

        using (var noToken = host.CreateClient(allowAutoRedirect: false))
        using (var rejected = await PostFormAsync(noToken, "/auth/setup", new()
               {
                   ["password"] = AdminPassword,
                   ["confirmPassword"] = AdminPassword
               }))
        {
            Assert.Equal(HttpStatusCode.Found, rejected.StatusCode);
            Assert.Contains("error=antiforgery", rejected.Headers.LocationPath());
            var storeBefore = host.Services.GetRequiredService<UserStore>();
            Assert.False(await storeBefore.AdminExistsAsync(CancellationToken.None));
        }

        var firstToken = await GetAntiforgeryTokenAsync(first, "/setup");
        var secondToken = await GetAntiforgeryTokenAsync(second, "/setup");

        var responses = await Task.WhenAll(
            PostFormAsync(first, "/auth/setup", new()
            {
                ["__RequestVerificationToken"] = firstToken,
                ["password"] = AdminPassword,
                ["confirmPassword"] = AdminPassword
            }),
            PostFormAsync(second, "/auth/setup", new()
            {
                ["__RequestVerificationToken"] = secondToken,
                ["password"] = "Second-Pass-2!",
                ["confirmPassword"] = "Second-Pass-2!"
            }));

        using var winner = responses[0];
        using var loser = responses[1];
        var locations = new[] { winner.Headers.LocationPath(), loser.Headers.LocationPath() };
        Assert.Contains("/", locations);
        Assert.Contains("/login", locations);
        Assert.Single(responses, response => ReadCookie(response, AuthCookieName) is not null);

        var store = host.Services.GetRequiredService<UserStore>();
        Assert.True(await store.AdminExistsAsync(CancellationToken.None));
        Assert.Equal(1, CountUsers(host.Services.GetRequiredService<TaskDatabase>()));
        Assert.Equal(1, CountTasks(host.Services.GetRequiredService<TaskDatabase>(), "old-task"));
    }

    [Fact]
    public async Task Local_LoginError_IsSameForUnknownLoginAndWrongPassword_AndRequiresAntiforgery()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);

        using var unknown = host.CreateClient(allowAutoRedirect: false);
        var unknownToken = await GetAntiforgeryTokenAsync(unknown, "/login");
        using var unknownResponse = await PostFormAsync(unknown, "/auth/login", new()
        {
            ["__RequestVerificationToken"] = unknownToken,
            ["login"] = "nobody",
            ["password"] = AdminPassword
        });

        using var wrong = host.CreateClient(allowAutoRedirect: false);
        var wrongToken = await GetAntiforgeryTokenAsync(wrong, "/login");
        using var wrongResponse = await PostFormAsync(wrong, "/auth/login", new()
        {
            ["__RequestVerificationToken"] = wrongToken,
            ["login"] = "admin",
            ["password"] = "wrong-password"
        });

        Assert.Equal(HttpStatusCode.Found, unknownResponse.StatusCode);
        Assert.Equal(unknownResponse.Headers.LocationPath(), wrongResponse.Headers.LocationPath());
        Assert.Contains("error=credentials", unknownResponse.Headers.LocationPath());
        Assert.Null(ReadCookie(unknownResponse, AuthCookieName));
        Assert.Null(ReadCookie(wrongResponse, AuthCookieName));

        using var noToken = host.CreateClient(allowAutoRedirect: false);
        using var rejected = await PostFormAsync(noToken, "/auth/login", new()
        {
            ["login"] = "admin",
            ["password"] = AdminPassword
        });
        Assert.Equal(HttpStatusCode.Found, rejected.StatusCode);
        Assert.Contains("error=antiforgery", rejected.Headers.LocationPath());
        Assert.Null(ReadCookie(rejected, AuthCookieName));
    }

    [Fact]
    public async Task Local_Login_RejectsTabPrefixedExternalReturnUrl()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        using var client = host.CreateClient(allowAutoRedirect: false);
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        using var response = await PostFormAsync(client, "/auth/login", new()
        {
            ["__RequestVerificationToken"] = token,
            ["login"] = "admin",
            ["password"] = AdminPassword,
            ["returnUrl"] = "/\t/example.org/"
        });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.LocationPath());
    }

    [Fact]
    public async Task UserStore_RejectsStaleLoginAndPasswordChangeAfterLogoutOrConcurrentChange()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        var original = await store.FindByLoginAsync("admin", CancellationToken.None);
        Assert.NotNull(original);

        var session = await store.TryCreateSessionAsync(
            original!, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(session);
        await store.RevokeSessionAsync(session.Value, CancellationToken.None);
        Assert.Null(await store.TryChangePasswordAsync(
            original!, session.Value, "Admin-Pass-2!", DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None));

        var fresh = await store.FindByLoginAsync("admin", CancellationToken.None);
        Assert.NotNull(fresh);
        var activeSession = await store.TryCreateSessionAsync(
            fresh!, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(activeSession);
        var changedSession = await store.TryChangePasswordAsync(
            fresh!, activeSession.Value, "Admin-Pass-3!", DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None);
        Assert.NotNull(changedSession);
        Assert.Null(await store.GetActiveSessionAsync(activeSession.Value, CancellationToken.None));
        Assert.NotNull(await store.GetActiveSessionAsync(changedSession.Value, CancellationToken.None));
        Assert.Null(await store.TryChangePasswordAsync(
            fresh!, activeSession.Value, "Admin-Pass-4!", DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None));
        Assert.Null(await store.TryCreateSessionAsync(
            fresh!, DateTimeOffset.UtcNow.Add(AuthService.SessionLifetime), CancellationToken.None));
        var afterRaces = await store.FindByLoginAsync("admin", CancellationToken.None);
        Assert.NotNull(afterRaces);
        Assert.True(host.Services.GetRequiredService<PasswordService>().Verify(afterRaces.PasswordHash, "Admin-Pass-3!"));
    }

    [Fact]
    public async Task Local_FileRoutes_Return401WithoutSession_And403ForRegularAccounts()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "readme.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "report.pdf"), "%PDF-12345");
        using var host = await CreateConfiguredHostAsync(workspace);

        using var anonymous = host.CreateClient(allowAutoRedirect: false);
        using var anonymousDownload = await anonymous.GetAsync("/api/fs/download?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDownload.StatusCode);
        Assert.Equal("application/problem+json", anonymousDownload.Content.Headers.ContentType?.MediaType);
        Assert.Equal("authentication_required", await ReadProblemCodeAsync(anonymousDownload));

        var protectedRequests = new[]
        {
            "/api/fs/preview?root=files&path=readme.txt",
            "/api/fs/zip?ticket=unknown",
            "/api/fs/upload/check?root=files&path=&name=other.txt"
        };
        foreach (var url in protectedRequests)
        {
            using var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("authentication_required", await ReadProblemCodeAsync(response));
        }

        using (var upload = await anonymous.PostAsync("/api/fs/upload?root=files&path=&name=other.txt", new ByteArrayContent([])))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
            Assert.Equal("authentication_required", await ReadProblemCodeAsync(upload));
        }

        using var admin = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(admin, "admin", AdminPassword);
        using var adminDownload = await admin.GetAsync("/api/fs/download?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.OK, adminDownload.StatusCode);
        Assert.Equal("no-store", adminDownload.Headers.CacheControl?.ToString());
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?root=files&path=readme.txt");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        using var rangedDownload = await admin.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangedDownload.StatusCode);
        Assert.Equal("no-store", rangedDownload.Headers.CacheControl?.ToString());
        using var adminPreview = await admin.GetAsync("/api/fs/preview?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.OK, adminPreview.StatusCode);
        Assert.Equal("no-store", adminPreview.Headers.CacheControl?.ToString());
        using var previewRangeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/fs/preview?root=files&path=report.pdf");
        previewRangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
        using var rangedPreview = await admin.SendAsync(previewRangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangedPreview.StatusCode);
        Assert.Equal("no-store", rangedPreview.Headers.CacheControl?.ToString());

        var store = host.Services.GetRequiredService<UserStore>();
        await store.CreateUserAsync("alice", "Alice-Pass-1!", mustChangePassword: false, CancellationToken.None);

        using var regular = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(regular, "alice", "Alice-Pass-1!");
        using var regularDownload = await regular.GetAsync("/api/fs/download?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.Forbidden, regularDownload.StatusCode);
        Assert.Equal("access_denied", await ReadProblemCodeAsync(regularDownload));

        // Обычная учётка допускается к рабочему UI, но без прав на корни
        // видит пустое состояние, а файловый HTTP отвечает 403.
        using var regularRoot = await regular.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, regularRoot.StatusCode);

        // Админская страница управления учётками закрыта по прямому адресу.
        using var usersPage = await regular.GetAsync("/users");
        Assert.Equal(HttpStatusCode.Found, usersPage.StatusCode);
        Assert.Equal("/access-denied", usersPage.Headers.LocationPath());

        await store.CreateUserAsync("bob", "Bob-Pass-1!", mustChangePassword: true, CancellationToken.None);
        using var temporary = host.CreateClient(allowAutoRedirect: false);
        using var temporaryLogin = await PostLoginAsync(temporary, "bob", "Bob-Pass-1!");
        Assert.Equal("/change-password", temporaryLogin.Headers.LocationPath());
        using var temporaryDownload = await temporary.GetAsync("/api/fs/download?root=files&path=readme.txt");
        Assert.Equal(HttpStatusCode.Forbidden, temporaryDownload.StatusCode);
    }

    [Fact]
    public async Task Local_RegularAccountCannotDownloadTemporaryArtifactCaseAliasesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        const string artifactName = ".filebrowser-temp-fixture";
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, artifactName), "secret temporary bytes");
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "visible.txt"), "ordinary content");
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        await store.CreateUserAsync(
            "alice",
            "Alice-Pass-1!",
            mustChangePassword: false,
            new Dictionary<string, RootAccess> { ["files"] = RootAccess.Write },
            CancellationToken.None);

        using var regular = host.CreateClient(allowAutoRedirect: false);
        await LoginAsync(regular, "alice", "Alice-Pass-1!");
        using (var visible = await regular.GetAsync("/api/fs/download?root=files&path=visible.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
            Assert.Equal("ordinary content", await visible.Content.ReadAsStringAsync());
        }
        foreach (var (root, path) in new[]
                 {
                     ("files", ".FILEBROWSER-temp-fixture"),
                     ("AllRoots", "files/.FILEBROWSER-temp-fixture")
                 })
        {
            var url = $"/api/fs/download?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}";
            using var response = await regular.GetAsync(url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_request", await ReadProblemCodeAsync(response));
        }
    }

    [Fact]
    public async Task Local_ChangePassword_RevokesOldSessions_AndAllowsNewSession()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);

        using var client = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(client, "admin", AdminPassword);
        var oldCookie = Assert.IsType<string>(ReadCookie(login, AuthCookieName));

        var changeToken = await GetAntiforgeryTokenAsync(client, "/change-password");
        using var change = await PostFormAsync(client, "/auth/change-password", new()
        {
            ["__RequestVerificationToken"] = changeToken,
            ["currentPassword"] = AdminPassword,
            ["newPassword"] = "Admin-Pass-2!",
            ["confirmPassword"] = "Admin-Pass-2!"
        });
        Assert.Equal(HttpStatusCode.Found, change.StatusCode);
        Assert.Equal("/", change.Headers.LocationPath());
        var newCookie = Assert.IsType<string>(ReadCookie(change, AuthCookieName));
        Assert.NotEqual(oldCookie, newCookie);

        using var newSession = host.CreateClient(allowAutoRedirect: false);
        newSession.DefaultRequestHeaders.Add("Cookie", newCookie);
        using var newRoot = await newSession.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, newRoot.StatusCode);

        using var oldSession = host.CreateClient(allowAutoRedirect: false);
        oldSession.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var oldDownload = await oldSession.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, oldDownload.StatusCode);

        using var oldPassword = host.CreateClient(allowAutoRedirect: false);
        var oldLogin = await PostLoginAsync(oldPassword, "admin", AdminPassword);
        Assert.Contains("error=credentials", oldLogin.Headers.LocationPath());
    }

    [Fact]
    public async Task Local_RegularAccount_CanChangeTemporaryPassword_AndNeedsRootRightsForFiles()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        await store.CreateUserAsync("carol", "Carol-Temp-1!", mustChangePassword: true, CancellationToken.None);

        using var client = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(client, "carol", "Carol-Temp-1!");
        Assert.Equal("/change-password", login.Headers.LocationPath());
        var oldCookie = Assert.IsType<string>(ReadCookie(login, AuthCookieName));

        var changeToken = await GetAntiforgeryTokenAsync(client, "/change-password");
        using var change = await PostFormAsync(client, "/auth/change-password", new()
        {
            ["__RequestVerificationToken"] = changeToken,
            ["currentPassword"] = "Carol-Temp-1!",
            ["newPassword"] = "Carol-Own-2!",
            ["confirmPassword"] = "Carol-Own-2!"
        });
        Assert.Equal(HttpStatusCode.Found, change.StatusCode);
        Assert.Equal("/", change.Headers.LocationPath());

        // Временный пароль сменён: учётка получает рабочий UI, но без выданных
        // прав на корни файловый HTTP по-прежнему отвечает 403.
        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        using var download = await client.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);

        using var stale = host.CreateClient(allowAutoRedirect: false);
        stale.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var staleDownload = await stale.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, staleDownload.StatusCode);
    }

    [Fact]
    public async Task Local_TemporaryPasswordCannotBeReusedDuringRequiredChange()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);
        var store = host.Services.GetRequiredService<UserStore>();
        await store.CreateUserAsync("carol", "Carol-Temp-1!", mustChangePassword: true, CancellationToken.None);

        using var client = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(client, "carol", "Carol-Temp-1!");
        Assert.Equal("/change-password", login.Headers.LocationPath());
        var token = await GetAntiforgeryTokenAsync(client, "/change-password");
        using var repeated = await PostFormAsync(client, "/auth/change-password", new()
        {
            ["__RequestVerificationToken"] = token,
            ["currentPassword"] = "Carol-Temp-1!",
            ["newPassword"] = "Carol-Temp-1!",
            ["confirmPassword"] = "Carol-Temp-1!"
        });

        Assert.Equal("/change-password?error=same_password", repeated.Headers.Location?.OriginalString);
        var unchanged = await store.FindByLoginAsync("carol", CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.True(unchanged!.MustChangePassword);
        Assert.True(host.Services.GetRequiredService<PasswordService>().Verify(unchanged.PasswordHash, "Carol-Temp-1!"));
        Assert.False(repeated.Headers.Contains("Set-Cookie"));
        using var stillRestricted = await client.GetAsync("/");
        Assert.Equal("/change-password", stillRestricted.Headers.LocationPath());

        token = await GetAntiforgeryTokenAsync(client, "/change-password");
        using var changed = await PostFormAsync(client, "/auth/change-password", new()
        {
            ["__RequestVerificationToken"] = token,
            ["currentPassword"] = "Carol-Temp-1!",
            ["newPassword"] = "Carol-Own-2!",
            ["confirmPassword"] = "Carol-Own-2!"
        });
        Assert.Equal("/", changed.Headers.LocationPath());
        Assert.False((await store.FindByLoginAsync("carol", CancellationToken.None))!.MustChangePassword);
    }

    [Fact]
    public async Task Local_Admin_CanUseAllFileHttpRoutes()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "readme.txt"), "hello");
        using var host = await CreateConfiguredHostAsync(workspace);
        using var client = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(client, "admin", AdminPassword);
        Assert.Equal("/", login.Headers.LocationPath());

        using (var download = await client.GetAsync("/api/fs/download?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        using (var preview = await client.GetAsync("/api/fs/preview?root=files&path=readme.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        using (var check = await client.GetAsync("/api/fs/upload/check?root=files&path=&name=checked.txt&size=1"))
        {
            Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);
        }

        var token = await GetAntiforgeryTokenAsync(client, "/");
        var body = Encoding.UTF8.GetBytes("uploaded");
        using (var content = new ByteArrayContent(body))
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/fs/upload?root=files&path=&name=uploaded.txt")
               {
                   Content = content
               })
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
            using var upload = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        var admin = await host.Services.GetRequiredService<UserStore>()
            .FindByLoginAsync("admin", CancellationToken.None);
        var ticket = host.Services.GetRequiredService<ArchiveTicketStore>()
            .Create(new ArchiveSelection("files", string.Empty, [], admin!.Id));
        using (var zip = await client.GetAsync($"/api/fs/zip?ticket={ticket}"))
        {
            Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        }
    }

    [Fact]
    public async Task Local_Recovery_ChangesOnlyAdminPassword_AndRevokesSessions()
    {
        using var workspace = new TestWorkspace();
        var configPath = workspace.WriteConfiguration(workspace.RootPath);
        var databasePath = Path.Combine(workspace.ConfigDirectory, "app.db");

        string cookie;
        using (var host = await CreateConfiguredHostAsync(workspace))
        {
            using var client = host.CreateClient(allowAutoRedirect: false);
            using var login = await PostLoginAsync(client, "admin", AdminPassword);
            cookie = Assert.IsType<string>(ReadCookie(login, AuthCookieName));
        }

        Assert.True(AdminPasswordRecovery.ResetPassword(databasePath, "Recovered-Pass-9!"));

        using var restarted = TestHostFactory.Create(configPath, databasePath, settings: LocalSettings());
        using var stale = restarted.CreateClient(allowAutoRedirect: false);
        stale.DefaultRequestHeaders.Add("Cookie", cookie);
        using var staleDownload = await stale.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, staleDownload.StatusCode);

        using var oldPassword = restarted.CreateClient(allowAutoRedirect: false);
        var oldLogin = await PostLoginAsync(oldPassword, "admin", AdminPassword);
        Assert.Contains("error=credentials", oldLogin.Headers.LocationPath());

        using var newPassword = restarted.CreateClient(allowAutoRedirect: false);
        var newLogin = await PostLoginAsync(newPassword, "admin", "Recovered-Pass-9!");
        Assert.Equal("/", newLogin.Headers.LocationPath());
    }

    [Fact]
    public async Task Local_Logout_RevokesSession_AndOldCookieIsRejected()
    {
        using var workspace = new TestWorkspace();
        using var host = await CreateConfiguredHostAsync(workspace);

        using var client = host.CreateClient(allowAutoRedirect: false);
        using var login = await PostLoginAsync(client, "admin", AdminPassword);
        var cookie = Assert.IsType<string>(ReadCookie(login, AuthCookieName));

        var token = await GetAntiforgeryTokenAsync(client, "/");
        using var logout = await PostFormAsync(client, "/auth/logout", new()
        {
            ["__RequestVerificationToken"] = token
        });
        Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.LocationPath());

        using var stale = host.CreateClient(allowAutoRedirect: false);
        stale.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await stale.GetAsync("/api/fs/download?root=files&path=missing.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Migration_AddsUsersAndSessions_WithoutTouchingExistingData()
    {
        using var workspace = new TestWorkspace();
        var databasePath = Path.Combine(workspace.ConfigDirectory, "app.db");
        var database = new TaskDatabase(databasePath);
        database.Migrate();
        InsertTask(database, "kept-task");
        using (var connection = database.OpenConnection())
        using (var settings = connection.CreateCommand())
        {
            settings.CommandText = """
                INSERT INTO app_settings(key, value) VALUES ('ui.language', 'ru');
                DROP TABLE user_root_permissions;
                DROP TABLE sessions;
                DROP TABLE users;
                DELETE FROM schema_migrations WHERE version IN (4, 5);
                """;
            settings.ExecuteNonQuery();
        }

        new TaskDatabase(databasePath).Migrate();

        Assert.Equal(1, CountTasks(database, "kept-task"));
        using var verify = database.OpenConnection();
        using (var language = verify.CreateCommand())
        {
            language.CommandText = "SELECT value FROM app_settings WHERE key = 'ui.language';";
            Assert.Equal("ru", language.ExecuteScalar());
        }

        using (var users = verify.CreateCommand())
        {
            users.CommandText = "SELECT COUNT(*) FROM users;";
            Assert.Equal(0L, users.ExecuteScalar());
        }

        using (var migration = verify.CreateCommand())
        {
            migration.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 5;";
            Assert.Equal(1L, migration.ExecuteScalar());
        }

        using (var permissions = verify.CreateCommand())
        {
            permissions.CommandText = "SELECT COUNT(*) FROM user_root_permissions;";
            Assert.Equal(0L, permissions.ExecuteScalar());
        }
    }

    private static TestHostFactory.TestHost CreateLocalHost(string configPath) =>
        TestHostFactory.Create(configPath, settings: LocalSettings());

    private static Dictionary<string, string?> LocalSettings() => new()
    {
        [AuthModeReader.SettingName] = "LOCAL"
    };

    private static async Task<TestHostFactory.TestHost> CreateConfiguredHostAsync(TestWorkspace workspace)
    {
        var host = CreateLocalHost(workspace.WriteConfiguration(workspace.RootPath));
        try
        {
            using var client = host.CreateClient(allowAutoRedirect: false);
            var token = await GetAntiforgeryTokenAsync(client, "/setup");
            using var response = await PostFormAsync(client, "/auth/setup", new()
            {
                ["__RequestVerificationToken"] = token,
                ["password"] = AdminPassword,
                ["confirmPassword"] = AdminPassword
            });
            if (response.StatusCode != HttpStatusCode.Found)
            {
                throw new InvalidOperationException($"Не удалось создать admin: {response.StatusCode}");
            }

            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string login,
        string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        return await PostFormAsync(client, "/auth/login", new()
        {
            ["__RequestVerificationToken"] = token,
            ["login"] = login,
            ["password"] = password
        });
    }

    private static async Task LoginAsync(HttpClient client, string login, string password)
    {
        using var response = await PostLoginAsync(client, login, password);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.LocationPath());
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token was not rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> values) =>
        client.PostAsync(path, new FormUrlEncodedContent(values));

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

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("code").GetString();
    }

    private static void InsertTask(TaskDatabase database, string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks(id, type, status, stage, destination_root, destination_path,
                              bytes_done, items_done, created_at_utc, updated_at_utc)
            VALUES ($id, 'copy', 'completed', 'completed', 'files', '',
                    0, 0, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static long CountUsers(TaskDatabase database) => Count(database, "SELECT COUNT(*) FROM users;");

    private static long CountTasks(TaskDatabase database, string id) => Count(
        database,
        "SELECT COUNT(*) FROM tasks WHERE id = $id;",
        id);

    private static long Count(TaskDatabase database, string sql, string? parameter = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$id", parameter);
        }

        return (long)command.ExecuteScalar()!;
    }
}

internal static class HttpResponseHeaderExtensions
{
    public static string? LocationPath(this System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        var location = headers.Location;
        return location is null ? null : location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }
}
