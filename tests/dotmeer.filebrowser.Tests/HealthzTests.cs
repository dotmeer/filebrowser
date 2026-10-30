using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace dotmeer.filebrowser.Tests;

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
}
