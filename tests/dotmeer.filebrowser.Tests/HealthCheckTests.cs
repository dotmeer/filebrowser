using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class HealthCheckTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsOk(string path)
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthLive_ReturnsOk_WhenConfiguredRootIsUnavailable()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(Path.Combine(workspace.RootPath, "missing")));

        var response = await host.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredRootOverlappingConfigurationDirectory_StartsAndServesServiceFiles()
    {
        using var workspace = new TestWorkspace();
        var configPath = workspace.WriteConfiguration(workspace.ConfigDirectory);
        using var host = TestHostFactory.Create(configPath, databasePath: Path.Combine(workspace.ConfigDirectory, "app.db"));

        var response = await host.Client.GetAsync("/health/ready");
        var reader = host.Services.GetRequiredService<dotmeer.filebrowser.FileSystem.FileSystemReadService>();
        var listing = reader.List(new dotmeer.filebrowser.FileSystem.FileRef("files", string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(workspace.ConfigDirectory, "app.db")));
        Assert.Contains(listing.Entries, entry => entry.Name == "config.yaml");
        Assert.Contains(listing.Entries, entry => entry.Name == "app.db");
    }
}
