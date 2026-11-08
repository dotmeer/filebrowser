using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Settings;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly TaskDatabase _database;

    public AppSettingsServiceTests()
    {
        _database = new TaskDatabase(Path.Combine(_workspace.ConfigDirectory, "app.db"));
        _database.Migrate();
    }

    [Fact]
    public void GetLanguage_ReturnsEnglishByDefault()
    {
        var settings = new AppSettingsService(_database);

        Assert.Equal("en", settings.GetLanguage());
    }

    [Fact]
    public async Task SetLanguage_PersistsToDatabaseAndNewInstanceReadsIt()
    {
        var settings = new AppSettingsService(_database);
        await settings.SetLanguageAsync("ru", CancellationToken.None);

        var reloaded = new AppSettingsService(_database);
        Assert.Equal("ru", reloaded.GetLanguage());
    }

    [Fact]
    public async Task SetLanguage_UpdatesCachedValueImmediately()
    {
        var settings = new AppSettingsService(_database);

        await settings.SetLanguageAsync("ru", CancellationToken.None);

        Assert.Equal("ru", settings.GetLanguage());
    }

    [Fact]
    public async Task SetLanguage_RejectsUnsupportedLanguage()
    {
        var settings = new AppSettingsService(_database);

        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetLanguageAsync("de", CancellationToken.None));
        Assert.Equal("en", settings.GetLanguage());
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}

[Collection("Web application factory")]
public sealed class LocalizationTests
{
    [Fact]
    public async Task SettingsPage_UsesEnglishByDefault()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/settings");

        Assert.Contains("Settings", html);
        Assert.Contains("Language", html);
        Assert.Contains("lang=\"en\"", html);
        Assert.Contains("<option value=\"en\" selected", html);
    }

    [Fact]
    public async Task BrowsePage_UsesEnglishByDefault()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse");

        Assert.Contains(">Tasks</h2>", html);
        Assert.Contains("Active tasks (0)", WebUtility.HtmlDecode(html));
        Assert.Contains("History is empty.", WebUtility.HtmlDecode(html));
        Assert.Contains("Folder is empty.", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task ApiError_UsesEnglishByDefault()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/list?root=missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("Object not found", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("not_found", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ChangeLanguageToRussian_RendersRussianSettingsAndApiErrors()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var token = await GetAntiforgeryTokenAsync(host.Client);

        var changeResponse = await host.Client.PostAsync("/settings/language", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("language", "ru"), new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        var html = await changeResponse.Content.ReadAsStringAsync();
        var visibleText = WebUtility.HtmlDecode(html);
        Assert.Contains("Настройки", visibleText);
        Assert.Contains("Язык", visibleText);
        Assert.Contains("lang=\"ru\"", html);
        Assert.Contains("<option value=\"ru\" selected", html);

        var apiResponse = await host.Client.GetAsync("/api/fs/list?root=missing");
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        var problem = await ReadProblemAsync(apiResponse);
        Assert.Equal("Объект не найден", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ChangeLanguage_WithoutAntiforgeryToken_Returns400()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.PostAsync("/settings/language", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("language", "ru")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangeLanguage_ToUnsupportedValue_Returns400()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var token = await GetAntiforgeryTokenAsync(host.Client);

        var response = await host.Client.PostAsync("/settings/language", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("language", "de"), new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/settings");
        var match = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"(?<token>[^\\\"]+)\\\"");
        Assert.True(match.Success, "На странице настроек не найден antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static TestHostFactory.TestHost CreateHost(TestWorkspace workspace) =>
        TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
}
