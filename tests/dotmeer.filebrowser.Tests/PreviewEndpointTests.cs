using System.Net;
using System.Text;
using System.Text.Json;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class PreviewEndpointTests
{
    [Fact]
    public async Task Preview_Image_ReturnsInlineContentTypeAndFullBytes()
    {
        using var workspace = new TestWorkspace();
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "photo.png"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=photo.png");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("inline", disposition!.DispositionType);
        Assert.Equal("photo.png", disposition.FileNameStar ?? disposition.FileName);
        Assert.Equal(content, body);
    }

    [Fact]
    public async Task Preview_Pdf_ReturnsInlineAndSupportsRange()
    {
        using var workspace = new TestWorkspace();
        var content = Encoding.UTF8.GetBytes("%PDF-0123456789");
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.pdf"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var rangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/fs/preview?root=files&path=report.pdf");
        rangedRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 8);
        using var response = await host.Client.SendAsync(rangedRequest);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(5, response.Content.Headers.ContentRange?.From);
        Assert.Equal(8, response.Content.Headers.ContentRange?.To);
        Assert.Equal(content[5..9], body);
    }

    [Fact]
    public async Task Preview_Text_ReturnsFullContentWithoutTruncationHeader()
    {
        using var workspace = new TestWorkspace();
        var content = "первая строка\nвторая строка\n";
        File.WriteAllText(Path.Combine(workspace.RootPath, "notes.txt"), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=notes.txt");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.False(response.Headers.Contains("X-Preview-Truncated"));
        Assert.Equal(content, Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Preview_Text_TruncatesToLimitAndSetsHeader()
    {
        using var workspace = new TestWorkspace();
        var content = new string('a', 600 * 1024);
        File.WriteAllText(Path.Combine(workspace.RootPath, "big.log"), content);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=big.log");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(512 * 1024, body.Length);
        Assert.Equal(new string('a', 512 * 1024), Encoding.UTF8.GetString(body));
        Assert.True(response.Headers.TryGetValues("X-Preview-Truncated", out var values));
        Assert.Equal("true", values.Single());
    }

    [Fact]
    public async Task Preview_UnsupportedExtension_Returns415()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "archive.zip"), [1, 2, 3]);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=archive.zip");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("preview_not_supported", code);
    }

    [Fact]
    public async Task Preview_MissingFile_ReturnsNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=missing.txt");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", code);
    }

    [Fact]
    public async Task Preview_Directory_ReturnsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "folder"));
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=folder");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Preview_UsesUtf8EncodedFilenameForNonAsciiName()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "фото.png"), [1, 2, 3]);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=" + Uri.EscapeDataString("фото.png"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = response.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.Contains("inline", header, StringComparison.Ordinal);
        Assert.Contains("UTF-8''", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_VirtualRoot_ReturnsSameContentAsDirectPreview()
    {
        using var workspace = new TestWorkspace();
        var secondRootPath = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "beta");
        Directory.CreateDirectory(secondRootPath);
        var content = "virtual preview payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.txt"), content);
        using var host = TestHostFactory.Create(workspace.WriteRawConfiguration(
            $"roots:\n  - name: alpha\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRootPath}'\n"));

        using var direct = await host.Client.GetAsync("/api/fs/preview?root=alpha&path=report.txt");
        using var viaVirtual = await host.Client.GetAsync("/api/fs/preview?root=AllRoots&path=alpha/report.txt");
        var body = await viaVirtual.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, viaVirtual.StatusCode);
        Assert.Equal(content, body);
        Assert.Equal(
            direct.Content.Headers.ContentType?.MediaType,
            viaVirtual.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Preview_NotSupportedError_UsesRussianLocalizationAfterLanguageSwitch()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "archive.zip"), [1, 2, 3]);
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        await host.Services.GetRequiredService<AppSettingsService>().SetLanguageAsync("ru", CancellationToken.None);

        using var response = await host.Client.GetAsync("/api/fs/preview?root=files&path=archive.zip");
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("Предпросмотр не поддерживается", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("preview_not_supported", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = await ReadProblemAsync(response);
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
