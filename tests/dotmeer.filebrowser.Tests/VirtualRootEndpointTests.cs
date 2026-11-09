using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class VirtualRootEndpointTests
{
    [Fact]
    public async Task GetList_VirtualRoot_ReturnsConfiguredRootsAsDirectories()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/list?root=AllRoots");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AllRoots", root.GetProperty("root").GetString());
        Assert.Equal(string.Empty, root.GetProperty("path").GetString());
        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["alpha", "beta", "offline"], entries.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Equal(["directory", "directory", "unsupported"], entries.Select(entry => entry.GetProperty("kind").GetString()));
        Assert.All(entries, entry => Assert.True(entry.GetProperty("modifiedAtUtc").ValueKind is JsonValueKind.String or JsonValueKind.Null));
        Assert.Equal(JsonValueKind.Null, entries.Single(entry => entry.GetProperty("name").GetString() == "offline").GetProperty("modifiedAtUtc").ValueKind);
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetList_VirtualRootUnderRealRoot_ReturnsPrefixedPaths()
    {
        using var workspace = new TestWorkspace();
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "note.txt"), "note");
        using var host = CreateHost(workspace);
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/list?root=AllRoots&path=alpha/sub");
        var listing = await response.Content.ReadFromJsonAsync<ListingResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(listing);
        Assert.Equal("AllRoots", listing.Root);
        Assert.Equal("alpha/sub", listing.Path);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal("note.txt", entry.Name);
        Assert.Equal("alpha/sub/note.txt", entry.Path);
    }

    [Fact]
    public async Task GetList_VirtualRootUnknownFirstSegment_ReturnsNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/list?root=AllRoots&path=missing/dir");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetSearch_VirtualRoot_ReturnsPrefixedHitsAndUnavailableRoots()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(SecondRootPath(workspace));
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");
        File.WriteAllText(Path.Combine(SecondRootPath(workspace), "hit-b.txt"), "b");
        using var host = CreateHost(workspace);
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/search?root=AllRoots&q=hit");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AllRoots", root.GetProperty("root").GetString());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(["offline"], root.GetProperty("unavailableRoots").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["alpha/hit-a.txt", "beta/hit-b.txt"],
            root.GetProperty("results").EnumerateArray().Select(entry => entry.GetProperty("path").GetString()));
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDownload_VirtualRoot_ReturnsSameFileAsDirectDownload()
    {
        using var workspace = new TestWorkspace();
        var content = "virtual root payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.txt"), content);
        using var host = CreateHost(workspace);
        var client = host.Client;

        using var direct = await client.GetAsync("/api/fs/download?root=alpha&path=report.txt");
        using var viaVirtual = await client.GetAsync("/api/fs/download?root=AllRoots&path=alpha/report.txt");
        var body = await viaVirtual.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, viaVirtual.StatusCode);
        Assert.Equal(content, body);
        Assert.Equal(
            direct.Content.Headers.ContentDisposition?.FileNameStar ?? direct.Content.Headers.ContentDisposition?.FileName,
            viaVirtual.Content.Headers.ContentDisposition?.FileNameStar ?? viaVirtual.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("report.txt", viaVirtual.Content.Headers.ContentDisposition?.FileNameStar
            ?? viaVirtual.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task PostMkdir_VirtualRoot_ReturnsBadRequestInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var token = await GetAntiforgeryTokenAsync(host.Client);

        using var response = await SendJsonAsync(host.Client, "/api/fs/mkdir", new
        {
            parent = new { root = "AllRoots", path = string.Empty },
            name = "created"
        }, token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "created")));
    }

    [Fact]
    public async Task GetRoots_DoesNotIncludeVirtualRoot()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        var client = host.Client;

        var response = await client.GetAsync("/api/fs/roots");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var names = json.RootElement.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["alpha", "beta", "offline"], names);
        Assert.DoesNotContain("AllRoots", names, StringComparer.Ordinal);
    }

    private static string SecondRootPath(TestWorkspace workspace) =>
        Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "beta");

    private static TestHostFactory.TestHost CreateHost(TestWorkspace workspace)
    {
        var secondRootPath = SecondRootPath(workspace);
        Directory.CreateDirectory(secondRootPath);
        return TestHostFactory.Create(workspace.WriteRawConfiguration(
            $"roots:\n  - name: alpha\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRootPath}'\n  - name: offline\n    path: '{workspace.RootPath}-missing'\n"));
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/browse?root=alpha");
        var match = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"(?<token>[^\\\"]+)\\\"");
        Assert.True(match.Success, "На странице не найден antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string path, object payload, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private sealed record ListingResponse(string Root, string Path, IReadOnlyList<EntryResponse> Entries);

    private sealed record EntryResponse(string Name, string Path, string Kind, long? Size, DateTimeOffset? ModifiedAtUtc);
}
