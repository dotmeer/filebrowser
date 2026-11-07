using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public class HealthzTests
{
    [Fact]
    public async Task Get_Healthz_ReturnsOk()
    {
        using var workspace = new TestWorkspace();
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", workspace.WriteConfiguration(workspace.RootPath));
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/healthz");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    [Fact]
    public async Task Get_Healthz_ReturnsOk_WhenConfiguredRootIsUnavailable()
    {
        using var workspace = new TestWorkspace();
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", workspace.WriteConfiguration(Path.Combine(workspace.RootPath, "missing")));
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/healthz");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    [Fact]
    public async Task ConfiguredRootOverlappingConfigurationDirectory_StartsAndShowsServiceFiles()
    {
        using var workspace = new TestWorkspace();
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalDatabasePath = Environment.GetEnvironmentVariable("DatabasePath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", workspace.WriteConfiguration(workspace.ConfigDirectory));
        Environment.SetEnvironmentVariable("DatabasePath", null);
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/healthz");
            var listing = await client.GetStringAsync("/api/fs/list?root=files&path=");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(File.Exists(Path.Combine(workspace.ConfigDirectory, "app.db")));
            Assert.Contains("\"name\":\"config.yaml\"", listing);
            Assert.Contains("\"name\":\"app.db\"", listing);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("DatabasePath", originalDatabasePath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }
}
