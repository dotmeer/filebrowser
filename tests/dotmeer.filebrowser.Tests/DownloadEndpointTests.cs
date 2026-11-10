using System.Net;
using System.Text;
using System.Text.Json;
using dotmeer.filebrowser.Configuration;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class DownloadEndpointTests
{
    [Fact]
    public async Task Download_ReturnsBytesContentTypeDispositionAndLength()
    {
        using var workspace = new TestWorkspace();
        var content = Encoding.UTF8.GetBytes("file content for download");
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.txt"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=report.txt");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal("report.txt", disposition.FileNameStar ?? disposition.FileName);
        Assert.Equal(content, body);
    }

    [Fact]
    public async Task Download_UsesUtf8EncodedFilenameForNonAsciiName()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "файл.txt"), "content");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=" + Uri.EscapeDataString("файл.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = response.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.Contains("attachment", header, StringComparison.Ordinal);
        Assert.Contains("UTF-8''", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_SupportsClosedRange()
    {
        using var workspace = new TestWorkspace();
        var content = Encoding.UTF8.GetBytes("0123456789");
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "ranged.bin"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?root=files&path=ranged.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes", response.Content.Headers.ContentRange?.Unit);
        Assert.Equal(2, response.Content.Headers.ContentRange?.From);
        Assert.Equal(5, response.Content.Headers.ContentRange?.To);
        Assert.Equal(content.Length, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Equal(content[2..6], body);
    }

    [Fact]
    public async Task Download_SupportsOpenEndedRange()
    {
        using var workspace = new TestWorkspace();
        var content = Encoding.UTF8.GetBytes("0123456789");
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "ranged.bin"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?root=files&path=ranged.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(7, null);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(7, response.Content.Headers.ContentRange?.From);
        Assert.Equal(content.Length - 1, response.Content.Headers.ContentRange?.To);
        Assert.Equal(content[7..], body);
    }

    [Fact]
    public async Task Download_RejectsDirectoryWithInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "folder"));
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=folder");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Download_RejectsRootPath()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("nested/../secret.txt")]
    [InlineData("a\\b.txt")]
    [InlineData(".filebrowser-temp")]
    public async Task Download_RejectsInvalidPublicPaths(string path)
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync(
            "/api/fs/download?root=files&path=" + Uri.EscapeDataString(path));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Download_RejectsMissingFileWithNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=missing.txt");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", code);
    }

    [Fact]
    public async Task Download_RejectsUnknownRootWithNotFound()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "file.txt"), "content");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=unknown&path=file.txt");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", code);
    }

    [Fact]
    public async Task Download_RejectsSymlinkWithUnsupportedObject()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "content");
        var link = Path.Combine(workspace.RootPath, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=link.txt");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_object", code);
        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public async Task Download_InProduction_KeepsProblemDetailsContractWithoutPhysicalPath()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(
            workspace.WriteConfiguration(workspace.RootPath),
            environment: "Production");

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=absent.txt");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_VirtualRoot_ReturnsSameFileAsDirectDownload()
    {
        using var workspace = new TestWorkspace();
        var secondRootPath = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "beta");
        Directory.CreateDirectory(secondRootPath);
        var content = "virtual root payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.txt"), content);
        using var host = TestHostFactory.Create(workspace.WriteRawConfiguration(
            $"roots:\n  - name: alpha\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRootPath}'\n"));
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
    public async Task OpenFile_ReturnsLastPathSegmentAsNameForNestedFile()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "documents");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.txt"), "content");
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        var reader = new dotmeer.filebrowser.FileSystem.FileSystemReadService(configuration, new dotmeer.filebrowser.FileSystem.PathResolver(configuration));

        var file = reader.OpenFile(new dotmeer.filebrowser.FileSystem.FileRef("files", "documents/note.txt"));

        Assert.Equal("note.txt", file.Name);
        using (var streamReader = new StreamReader(file.Stream))
        {
            Assert.Equal("content", await streamReader.ReadToEndAsync());
        }
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }
}
