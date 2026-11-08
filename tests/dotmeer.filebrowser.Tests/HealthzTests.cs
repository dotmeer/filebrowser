using System.Net;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public class HealthzTests
{
    [Fact]
    public async Task Get_Healthz_ReturnsOk()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_Healthz_ReturnsOk_WhenConfiguredRootIsUnavailable()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(Path.Combine(workspace.RootPath, "missing")));

        var response = await host.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredRootOverlappingConfigurationDirectory_StartsAndShowsServiceFiles()
    {
        using var workspace = new TestWorkspace();
        var configPath = workspace.WriteConfiguration(workspace.ConfigDirectory);
        using var host = TestHostFactory.Create(configPath, databasePath: Path.Combine(workspace.ConfigDirectory, "app.db"));

        var response = await host.Client.GetAsync("/healthz");
        var listing = await host.Client.GetStringAsync("/api/fs/list?root=files&path=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(workspace.ConfigDirectory, "app.db")));
        Assert.Contains("\"name\":\"config.yaml\"", listing);
        Assert.Contains("\"name\":\"app.db\"", listing);
    }
}
