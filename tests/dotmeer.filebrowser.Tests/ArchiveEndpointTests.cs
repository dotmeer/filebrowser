using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// HTTP-эндпоинт скачивания архивом (SPEC §12.2, §16, §17): тикет,
/// структура архива, отсутствие Content-Length и Range, ошибки до
/// начала ответа, ограничение одновременных архивов.
/// </summary>
[Collection("Web application factory")]
public sealed class ArchiveEndpointTests
{
    [Fact]
    public async Task Zip_ReturnsStoredArchiveWithBaseRelativeEntries()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(sub, "empty"));
        File.WriteAllText(Path.Combine(sub, "b.txt"), "beta");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var ticket = CreateTicket(host, "files", string.Empty, []);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal("files.zip", disposition.FileNameStar ?? disposition.FileName);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.DoesNotContain("bytes", response.Headers.AcceptRanges);

        using var archive = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        Assert.Equal(
            new[] { "a.txt", "sub/", "sub/b.txt", "sub/empty/" },
            archive.Entries.Select(entry => entry.FullName).ToArray());
        var stored = archive.Entries.Single(entry => entry.FullName == "a.txt");
        Assert.Equal(stored.CompressedLength, stored.Length);
        using var reader = new StreamReader(stored.Open(), Encoding.UTF8);
        Assert.Equal("alpha", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Zip_WithSelectedNames_ContainsOnlySelection()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "beta");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var ticket = CreateTicket(host, "files", string.Empty, ["a.txt"]);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var body = await response.Content.ReadAsByteArrayAsync();

        using var archive = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        Assert.Equal(new[] { "a.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task Zip_WithoutTicket_ReturnsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/zip");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Zip_WithUnknownTicket_ReturnsTicketExpired()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        using var response = await host.Client.GetAsync("/api/fs/zip?ticket=unknown-ticket");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("ticket_expired", code);
    }

    [Fact]
    public async Task Zip_WithExpiredTicket_ReturnsTicketExpired()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        var clock = new ManualTimeProvider();
        using var host = TestHostFactory.Create(
            workspace.WriteConfiguration(workspace.RootPath),
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });

        var ticket = CreateTicket(host, "files", string.Empty, []);
        clock.Advance(ArchiveLimits.TicketLifetime + TimeSpan.FromSeconds(1));
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("ticket_expired", code);
    }

    [Fact]
    public async Task Zip_ForVirtualRootTopLevel_WithSeveralRoots_ReturnsInvalidRequest()
    {
        using var workspace = new TestWorkspace();
        var secondRoot = Path.Combine(Path.GetDirectoryName(workspace.RootPath)!, "beta");
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(secondRoot, "b.txt"), "beta");
        using var host = TestHostFactory.Create(workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRoot}'\n"));

        var ticket = CreateTicket(host, VirtualRoot.VirtualRootName, string.Empty, ["files", "beta"]);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", code);
    }

    [Fact]
    public async Task Zip_ForVirtualRootTopLevel_WithSingleRoot_ReturnsRootArchive()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "beta");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var ticket = CreateTicket(host, VirtualRoot.VirtualRootName, string.Empty, ["files"]);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "files.zip",
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName);
        using var archive = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        Assert.Equal(
            new[] { "a.txt", "sub/", "sub/b.txt" },
            archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public async Task Zip_UnderVirtualRoot_ReturnsArchiveOfRealRootSubtree()
    {
        using var workspace = new TestWorkspace();
        var documents = Path.Combine(workspace.RootPath, "documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "notes.txt"), "alpha");
        File.WriteAllText(Path.Combine(documents, "contract.md"), "beta");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var ticket = CreateTicket(host, VirtualRoot.VirtualRootName, "files/documents", ["notes.txt", "contract.md"]);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
        Assert.Equal(
            new[] { "contract.md", "notes.txt" },
            archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.Equal(
            "documents.zip",
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Zip_ReturnsTooManyRequestsWhenAllSlotsAreBusy()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var service = host.Services.GetRequiredService<FileSystemArchiveService>();
        using var first = service.TryAcquireSlot();
        using var second = service.TryAcquireSlot();
        Assert.NotNull(first);
        Assert.NotNull(second);

        var ticket = CreateTicket(host, "files", string.Empty, []);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("archive_busy", code);
    }

    [Fact]
    public async Task Zip_WithMissingObject_ReturnsNotFoundWithoutPhysicalPath()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "alpha");
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var ticket = CreateTicket(host, "files", string.Empty, ["absent.txt"]);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(workspace.RootPath, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zip_WithSymlinkInsideTree_ReturnsUnsupportedObject()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target.txt");
        File.WriteAllText(target, "alpha");
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

        var ticket = CreateTicket(host, "files", string.Empty, []);
        using var response = await host.Client.GetAsync($"/api/fs/zip?ticket={ticket}");
        var code = await ReadProblemCodeAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_object", code);
    }

    [Fact]
    public async Task BrowsePage_ShowsArchiveDownloadAction()
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

        var english = await host.Client.GetStringAsync("/?root=files");
        Assert.Contains("Download ZIP", english, StringComparison.Ordinal);
    }

    private static string CreateTicket(
        TestHostFactory.TestHost host,
        string root,
        string path,
        IReadOnlyList<string> names)
    {
        var store = host.Services.GetRequiredService<ArchiveTicketStore>();
        return store.Create(new ArchiveSelection(root, path, names));
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
