using System.Net;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tests;

public sealed class FileSystemReadServiceTests
{
    [Fact]
    public void GetRoots_ReturnsPublicNamesAndAvailability()
    {
        using var workspace = new TestWorkspace();
        var missingRoot = $"{workspace.RootPath}-missing";
        var reader = CreateReader(workspace, workspace.WriteRawConfiguration(
            $"roots:\n  - name: files\n    path: '{workspace.RootPath}'\n  - name: offline\n    path: '{missingRoot}'\n"));

        var roots = reader.GetRoots();

        Assert.Equal(["files", "offline"], roots.Select(root => root.Name));
        Assert.Equal([true, false], roots.Select(root => root.Available));
    }

    [Fact]
    public void List_ReturnsDirectoriesBeforeFilesAndHidesInternalEntries()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "z-folder"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "a-folder"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "file");
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "x");
        File.WriteAllText(Path.Combine(workspace.RootPath, ".filebrowser-private"), "hidden");
        var reader = CreateReader(workspace);

        var listing = reader.List(new FileRef("files", string.Empty));

        Assert.Equal(["a-folder", "z-folder", "a.txt", "b.txt"], listing.Entries.Select(entry => entry.Name));
        Assert.Equal(["directory", "directory", "file", "file"], listing.Entries.Select(entry => entry.Kind));
        Assert.Equal(1, listing.Entries.Single(entry => entry.Name == "a.txt").Size);
        Assert.Null(listing.Entries.Single(entry => entry.Name == "a-folder").Size);
        Assert.All(listing.Entries, entry => Assert.DoesNotContain(workspace.RootPath, entry.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_ReportsUnsupportedSymlinkWithoutFollowingIt()
    {
        using var workspace = new TestWorkspace();
        var target = Path.Combine(workspace.RootPath, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(workspace.RootPath, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var reader = CreateReader(workspace);

        var linkEntry = Assert.Single(reader.List(new FileRef("files", string.Empty)).Entries, entry => entry.Name == "link");
        Assert.Equal("unsupported", linkEntry.Kind);
        Assert.Equal("link", linkEntry.Path);
    }

    private static FileSystemReadService CreateReader(TestWorkspace workspace, string? rawConfiguration = null)
    {
        var configuration = FileBrowserConfigurationLoader.Load(
            rawConfiguration ?? workspace.WriteConfiguration(workspace.RootPath));
        return new FileSystemReadService(configuration, new PathResolver(configuration));
    }
}

[Collection("Web application factory")]
public sealed class FileSystemReadUiTests
{
    [Fact]
    public async Task BrowsePage_RendersRequestedDirectoryFromItsPublicUrl()
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.RootPath, "folder");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.txt"), "content");
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files&path=folder");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Status: {response.StatusCode}; body: {html}");
        Assert.Contains("File", html);
        Assert.Contains("note.txt", html);
        Assert.Contains("Refresh", html);
        Assert.DoesNotContain(workspace.RootPath, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsPage_ExplainsConfigFileIsManagedOnServer()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("config.yaml", html);
        Assert.Contains("restart the application", html);
        Assert.Contains("<legend>Theme</legend>", html);
        Assert.Contains("System", html);
        Assert.Contains("Light", html);
        Assert.Contains("Dark", html);
        Assert.Contains("name=\"theme-preference\"", html);
        Assert.Contains("data-nav-back", html);
        Assert.Contains("aria-label=\"Close settings\"", html);
    }

    [Fact]
    public async Task BrowsePage_ShowsEmptyStateForEmptyDirectory()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Folder is empty.", html);
    }

    [Fact]
    public async Task BrowsePage_ShowsSafeErrorForMissingDirectory()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace.WriteConfiguration(workspace.RootPath));

        var response = await host.Client.GetAsync("/browse?root=files&path=missing");
        var html = await response.Content.ReadAsStringAsync();
        var visibleText = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not found", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Go to root", visibleText);
        Assert.DoesNotContain(workspace.RootPath, html, StringComparison.OrdinalIgnoreCase);
    }

    private static TestHostFactory.TestHost CreateHost(string configPath, string environment = "Development") =>
        TestHostFactory.Create(configPath, environment: environment);
}
