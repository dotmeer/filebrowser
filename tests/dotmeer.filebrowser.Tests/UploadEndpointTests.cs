using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// HTTP-контракт загрузки (SPEC §11, §16, §17): antiforgery, Content-Length,
/// лимит размера, конфликт имени, виртуальный корень, временные артефакты
/// и запись задачи в историю.
/// </summary>
[Collection("Web application factory")]
public sealed class UploadEndpointTests
{
    [Fact]
    public async Task Upload_StoresFileAndCompletesUploadTask()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);
        var content = Encoding.UTF8.GetBytes("uploaded content");

        using var response = await SendUploadAsync(host, token, "files", string.Empty, "report.txt", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("files", json.RootElement.GetProperty("root").GetString());
        Assert.Equal("report.txt", json.RootElement.GetProperty("path").GetString());
        Assert.Equal(content.Length, json.RootElement.GetProperty("size").GetInt64());
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(workspace.RootPath, "report.txt")));

        var repository = host.Services.GetRequiredService<TaskRepository>();
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskType.Upload, task.Type);
        Assert.Equal(FileTaskStatus.Completed, task.Status);
        Assert.Equal(content.Length, task.BytesDone);
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());
        Assert.Empty(Directory.GetFiles(workspace.RootPath, ".filebrowser-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_NonAsciiName_IsAccepted()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);
        var content = Encoding.UTF8.GetBytes("данные");

        using var response = await SendUploadAsync(host, token, "files", string.Empty, "отчёт 2026.txt", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(workspace.RootPath, "отчёт 2026.txt")));
    }

    [Fact]
    public async Task Upload_EmptyFile_IsAccepted()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(host, token, "files", string.Empty, "empty.bin", []);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "empty.bin")));
    }

    [Fact]
    public async Task Upload_ExistingName_ReturnsNameConflictAndKeepsOriginal()
    {
        using var workspace = new TestWorkspace();
        var originalPath = Path.Combine(workspace.RootPath, "same.txt");
        await File.WriteAllTextAsync(originalPath, "original");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(
            host,
            token,
            "files",
            string.Empty,
            "same.txt",
            Encoding.UTF8.GetBytes("replacement"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("name_conflict", code);
        Assert.Equal("original", await File.ReadAllTextAsync(originalPath));
        Assert.Empty(Directory.GetFiles(workspace.RootPath, ".filebrowser-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_InvalidName_ReturnsInvalidName()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(
            host,
            token,
            "files",
            string.Empty,
            "../escape.txt",
            Encoding.UTF8.GetBytes("x"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_name", code);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "..", "escape.txt")));
    }

    [Fact]
    public async Task Upload_WithoutAntiforgeryToken_IsRejected()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await SendUploadAsync(
            host,
            token: null,
            "files",
            string.Empty,
            "no-token.txt",
            Encoding.UTF8.GetBytes("x"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery_failed", code);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "no-token.txt")));
    }

    [Fact]
    public async Task Upload_WithoutContentLength_ReturnsLengthRequired()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendChunkedUploadAsync(
            host,
            token,
            "files",
            string.Empty,
            "chunked.txt",
            Encoding.UTF8.GetBytes("chunked"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.LengthRequired, response.StatusCode);
        Assert.Equal("length_required", code);
    }

    [Fact]
    public async Task Upload_OverConfiguredLimit_ReturnsFileTooLarge()
    {
        using var workspace = new TestWorkspace();
        var configPath = workspace.WriteRawConfiguration(
            $"uploads:\n  maxFileBytes: 4\nroots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        using var host = TestHostFactory.Create(configPath);
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(
            host,
            token,
            "files",
            string.Empty,
            "big.bin",
            new byte[5]);
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", code);
        Assert.False(File.Exists(Path.Combine(workspace.RootPath, "big.bin")));
    }

    [Fact]
    public async Task Upload_ToVirtualRootTopLevel_ReturnsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(
            host,
            token,
            "AllRoots",
            string.Empty,
            "a.txt",
            Encoding.UTF8.GetBytes("x"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Upload_UnderVirtualRoot_DelegatesToRealRoot()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);
        var content = Encoding.UTF8.GetBytes("virtual upload");

        using var response = await SendUploadAsync(
            host,
            token,
            "AllRoots",
            "files",
            "via-virtual.txt",
            content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(workspace.RootPath, "via-virtual.txt")));
        var repository = host.Services.GetRequiredService<TaskRepository>();
        var task = Assert.Single((await repository.GetHistoryPageAsync(0, 10)).Tasks);
        Assert.Equal(FileTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task Upload_ToMissingRoot_ReturnsNotFound()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));
        var token = await GetAntiforgeryTokenAsync(host);

        using var response = await SendUploadAsync(
            host,
            token,
            "missing",
            string.Empty,
            "a.txt",
            Encoding.UTF8.GetBytes("x"));
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", code);
    }

    [Fact]
    public async Task Check_ReportsFreeNameConflictAndOversize()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "taken.txt"), "x");
        var configPath = workspace.WriteRawConfiguration(
            $"uploads:\n  maxFileBytes: 8\nroots:\n  - name: files\n    path: '{workspace.RootPath}'\n");
        using var host = TestHostFactory.Create(configPath);

        using var free = await host.Client.GetAsync("/api/fs/upload/check?root=files&path=&name=free.txt&size=8");
        Assert.Equal(HttpStatusCode.NoContent, free.StatusCode);

        using var conflict = await host.Client.GetAsync("/api/fs/upload/check?root=files&path=&name=taken.txt&size=1");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("name_conflict", await ReadProblemCodeAsync(conflict));

        using var oversized = await host.Client.GetAsync("/api/fs/upload/check?root=files&path=&name=big.bin&size=9");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal("file_too_large", await ReadProblemCodeAsync(oversized));

        using var invalid = await host.Client.GetAsync("/api/fs/upload/check?root=files&path=&name=..%2Fescape&size=1");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_name", await ReadProblemCodeAsync(invalid));
    }

    [Fact]
    public async Task BrowsePage_ShowsUploadActionAndScript()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var english = await host.Client.GetStringAsync("/?root=files");

        Assert.Contains("Upload", english, StringComparison.Ordinal);
        Assert.Contains("upload.js", english, StringComparison.Ordinal);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(TestHostFactory.TestHost host)
    {
        var html = await host.Client.GetStringAsync("/");
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Antiforgery token was not rendered on the page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> SendUploadAsync(
        TestHostFactory.TestHost host,
        string? token,
        string root,
        string path,
        string name,
        byte[] content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUploadUrl(root, path, name))
        {
            Content = new ByteArrayContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (token is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }

        return host.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendChunkedUploadAsync(
        TestHostFactory.TestHost host,
        string? token,
        string root,
        string path,
        string name,
        byte[] content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUploadUrl(root, path, name))
        {
            Content = new StreamContent(new NonSeekableStream(content))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TransferEncodingChunked = true;
        if (token is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }

        return host.Client.SendAsync(request);
    }

    private static string BuildUploadUrl(string root, string path, string name) =>
        $"/api/fs/upload?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}&name={Uri.EscapeDataString(name)}";

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
