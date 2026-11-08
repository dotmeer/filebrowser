using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Endpoints;
using dotmeer.filebrowser.FileSystem;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class DownloadTests
{
    [Fact]
    public async Task Download_ReturnsBytesContentTypeDispositionAndLength()
    {
        using var workspace = new TestWorkspace();
        var content = Encoding.UTF8.GetBytes("file content for download");
        File.WriteAllBytes(Path.Combine(workspace.RootPath, "report.txt"), content);
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=folder");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Download_RejectsRootPath()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

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

        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/download?root=files&path=link.txt");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_object", code);
        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public async Task OpenFile_ReturnsLastPathSegmentAsNameForNestedFile()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "documents");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.txt"), "content");
        var configuration = FileBrowserConfigurationLoader.Load(workspace.WriteConfiguration(workspace.RootPath));
        var reader = new FileSystemReadService(configuration, new PathResolver(configuration));

        var file = reader.OpenFile(new FileRef("files", "documents/note.txt"));

        Assert.Equal("note.txt", file.Name);
        using (var streamReader = new StreamReader(file.Stream))
        {
            Assert.Equal("content", await streamReader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ApiExceptionMiddleware_RethrowsStreamErrorAfterResponseStarted()
    {
        var middleware = new ApiExceptionMiddleware(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("partial", context.RequestAborted);
                throw new IOException("Сбой чтения файла.");
            },
            NullLogger<ApiExceptionMiddleware>.Instance,
            new PassthroughLocalizer<dotmeer.filebrowser.SharedResources>());
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/fs/download";
        var responseFeature = new StartedResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpResponseBodyFeature>(
            new StartedBodyFeature(new MemoryStream(), responseFeature));

        await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context));

        context.Response.Body.Position = 0;
        Assert.Equal("partial", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public bool Started { get; set; }

        public override bool HasStarted => Started;
    }

    private sealed class StartedBodyFeature(Stream stream, StartedResponseFeature response)
        : StreamResponseBodyFeature(stream)
    {
        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            response.Started = true;
            return base.StartAsync(cancellationToken);
        }
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static TestHost CreateHost(string configPath)
    {
        var originalConfigPath = Environment.GetEnvironmentVariable("ConfigPath");
        var originalEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
        Environment.SetEnvironmentVariable("ConfigPath", configPath);
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        var keyDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))));
        try
        {
            return new TestHost(factory, factory.CreateClient());
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", originalEventLogLevel);
        }
    }

    private sealed class TestHost(WebApplicationFactory<Program> factory, HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }
    }
}
