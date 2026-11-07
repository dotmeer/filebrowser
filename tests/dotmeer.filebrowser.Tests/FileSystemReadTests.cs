using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class FileSystemReadTests
{
    [Fact]
    public async Task GetRoots_ReturnsPublicNamesAndAvailabilityWithoutPhysicalPaths()
    {
        using var workspace = new TestWorkspace();
        var missingRoot = $"{workspace.RootPath}-missing";
        using var host = CreateHost(workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: offline\n    path: '{missingRoot}'\n"));
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/roots");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"name\":\"files\"", body);
        Assert.Contains("\"available\":true", body);
        Assert.Contains("\"name\":\"offline\"", body);
        Assert.Contains("\"available\":false", body);
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetList_ReturnsDirectoriesBeforeFilesAndHidesInternalEntries()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "z-folder"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "a-folder"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "file");
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, ".filebrowser-private"), "hidden");
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/list?root=files&path=");
        var listing = await response.Content.ReadFromJsonAsync<ListingResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(listing);
        Assert.Equal(["a-folder", "z-folder", "a.txt", "b.txt"], listing.Entries.Select(entry => entry.Name));
        Assert.Equal(["directory", "directory", "file", "file"], listing.Entries.Select(entry => entry.Kind));
        Assert.Equal(1, listing.Entries.Single(entry => entry.Name == "a.txt").Size);
        Assert.Null(listing.Entries.Single(entry => entry.Name == "a-folder").Size);
        Assert.All(listing.Entries, entry => Assert.DoesNotContain(workspace.RootPath, entry.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetList_ReportsUnsupportedSymlinkWithoutFollowingIt()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(workspace.RootPath, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));
        var client = host.Client;
        var response = await client.GetAsync("/api/fs/list?root=files");
        var listing = await response.Content.ReadFromJsonAsync<ListingResponse>();

        var linkEntry = Assert.Single(listing!.Entries, entry => entry.Name == "link");
        Assert.Equal("unsupported", linkEntry.Kind);
        Assert.Equal("link", linkEntry.Path);
    }

    [Fact]
    public async Task GetList_ErrorReturnsProblemDetailsWithStableCodeAndTraceIdWithoutPhysicalPath()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/list?root=files&path=absent");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetList_InvalidPathReturnsBadRequestProblemDetails()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/api/fs/list?root=files&path=..%2Foutside");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task GetList_TemporaryArtifactPathReturnsBadRequestWithoutExposingIt()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, ".filebrowser-temp"), "temporary");
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/api/fs/list?root=files&path=.filebrowser-temp");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("temporary", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiErrors_InProduction_KeepProblemDetailsContract()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, ".filebrowser-temp"), "temporary");
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath), environment: "Production");

        var missing = await host.Client.GetAsync("/api/fs/list?root=files&path=absent");
        var missingBody = await missing.Content.ReadAsStringAsync();
        using var missingJson = JsonDocument.Parse(missingBody);
        var hidden = await host.Client.GetAsync("/api/fs/list?root=files&path=.filebrowser-temp");
        var hiddenBody = await hidden.Content.ReadAsStringAsync();
        using var hiddenJson = JsonDocument.Parse(hiddenBody);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", missingJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        Assert.Equal("application/problem+json", hidden.Content.Headers.ContentType?.MediaType);
        Assert.Equal("invalid_request", hiddenJson.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(workspace.RootPath, missingBody + hiddenBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowsePage_RendersRequestedDirectoryFromItsPublicUrl()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "folder");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.txt"), "content");
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files&path=folder");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Status: {response.StatusCode}; body: {html}");
        Assert.Contains("Файл", html);
        Assert.Contains("note.txt", html);
        Assert.Contains("Обновить", html);
        Assert.DoesNotContain(workspace.RootPath, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsPage_ExplainsConfigFileIsManagedOnServer()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("config.yaml", html);
        Assert.Contains("перезапустите приложение", html);
        Assert.Contains("<legend>Тема</legend>", html);
        Assert.Contains("Системная", html);
        Assert.Contains("Светлая", html);
        Assert.Contains("Тёмная", html);
        Assert.Contains("name=\"theme-preference\"", html);
        Assert.Contains("data-nav-back", html);
    }

    [Fact]
    public async Task BrowsePage_ShowsEmptyStateForEmptyDirectory()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Папка пуста.", html);
    }

    [Fact]
    public async Task BrowsePage_ShowsSafeErrorForMissingDirectory()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files&path=missing");
        var html = await response.Content.ReadAsStringAsync();
        var visibleText = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("не найден", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("К корню", visibleText);
        Assert.DoesNotContain(workspace.RootPath, html, StringComparison.OrdinalIgnoreCase);
    }

    private static TestHost CreateHost(string configPath, string environment = "Development")
    {
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", configPath);
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        var keyDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services => services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)));
        });
        try
        {
            var client = factory.CreateClient();
            return new TestHost(factory, client);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    private sealed class TestHost(WebApplicationFactory<Program> factory, HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }
    }

    private sealed record ListingResponse(string Root, string Path, IReadOnlyList<EntryResponse> Entries);

    private sealed record EntryResponse(string Name, string Path, string Kind, long? Size, DateTimeOffset ModifiedAtUtc);
}
