using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class SearchEndpointTests
{
    [Fact]
    public async Task Search_ReturnsResultContractWithoutPhysicalPaths()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "folder"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "folder", "note-a.txt"), "a");
        File.WriteAllText(Path.Combine(workspace.RootPath, "note-b.txt"), "b");
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&q=note");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("files", root.GetProperty("root").GetString());
        Assert.Equal(string.Empty, root.GetProperty("path").GetString());
        Assert.Equal("note", root.GetProperty("query").GetString());
        Assert.False(root.GetProperty("ext").ValueKind is JsonValueKind.String);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var results = root.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal(["folder/note-a.txt", "note-b.txt"], results.EnumerateArray().Select(entry => entry.GetProperty("path").GetString()));
        Assert.All(results.EnumerateArray(), entry =>
        {
            Assert.True(entry.TryGetProperty("name", out _));
            Assert.True(entry.TryGetProperty("kind", out _));
            Assert.True(entry.TryGetProperty("size", out _));
            Assert.True(entry.TryGetProperty("modifiedAtUtc", out _));
        });
        Assert.All(results.EnumerateArray(), entry => Assert.Equal("file", entry.GetProperty("kind").GetString()));
        Assert.All(results.EnumerateArray(), entry => Assert.Equal(1, entry.GetProperty("size").GetInt64()));
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_WithPathSearchesInsideStartDirectory()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "root-hit.txt"), "x");
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "sub-hit.txt"), "x");
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&path=sub&q=hit");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sub", json.RootElement.GetProperty("path").GetString());
        var results = json.RootElement.GetProperty("results");
        var entry = Assert.Single(results.EnumerateArray());
        Assert.Equal("sub/sub-hit.txt", entry.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Search_WithExt_ReturnsExtAndFiltersResults()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "clip.mkv"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, "clip.txt"), "x");
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&q=clip&ext=*.mkv");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*.mkv", json.RootElement.GetProperty("ext").GetString());
        var entry = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("clip.mkv", entry.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_WithoutAntiforgeryToken_Succeeds()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "x");
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&q=note");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("501")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task Search_InvalidLimit_ReturnsBadRequest(string limit)
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync($"/api/fs/search?root=files&q=x&limit={limit}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsBadRequest()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var missing = await host.Client.GetAsync("/api/fs/search?root=files");
        var whitespace = await host.Client.GetAsync("/api/fs/search?root=files&q=%20%20");

        using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        using var whitespaceJson = JsonDocument.Parse(await whitespace.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_request", missingJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, whitespace.StatusCode);
        Assert.Equal("invalid_request", whitespaceJson.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("%5B")]
    [InlineData(",,")]
    public async Task Search_InvalidExt_ReturnsBadRequest(string ext)
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync($"/api/fs/search?root=files&q=x&ext={ext}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_UnknownRoot_ReturnsNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=missing&q=x");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_StartIsFile_ReturnsBadRequest()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "file.txt"), "x");
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&path=file.txt&q=file");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_UnknownStartPath_ReturnsNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var response = await host.Client.GetAsync("/api/fs/search?root=files&path=absent&q=x");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
    }

    private static TestHostFactory.TestHost CreateHost(TestWorkspace workspace) =>
        TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
}
