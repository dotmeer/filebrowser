using System.Net;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Components.Pages;
using dotmeer.filebrowser.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

public sealed class SearchNavigationTests
{
    [Theory]
    [InlineData("a/b/c.txt", "a/b")]
    [InlineData("file.txt", "")]
    [InlineData("", "")]
    [InlineData("a/b/", "a/b")]
    [InlineData("документы/годовой отчёт 2026.pdf", "документы")]
    [InlineData("файл.txt", "")]
    [InlineData("архивы/2024/резерв копия.zip", "архивы/2024")]
    public void GetParentPath_ReturnsPathBeforeLastSeparator(string path, string expected)
    {
        Assert.Equal(expected, SearchNavigation.GetParentPath(path));
    }

    [Fact]
    public void BuildBrowseUrl_EncodesHighlightName()
    {
        var url = SearchNavigation.BuildBrowseUrl("files", "docs/report", "годовой отчёт 2026.txt");

        Assert.StartsWith("/browse?", url);
        Assert.Contains("root=files", url);
        Assert.Contains("path=docs%2Freport", url);
        Assert.Contains($"highlight={Uri.EscapeDataString("годовой отчёт 2026.txt")}", url);
    }

    [Fact]
    public void BuildBrowseUrl_OmitsEmptyHighlight()
    {
        var url = SearchNavigation.BuildBrowseUrl("files", "docs", highlight: null);

        Assert.DoesNotContain("highlight", url);
    }

    [Fact]
    public void BuildSearchUrl_EncodesQuery()
    {
        var url = SearchNavigation.BuildSearchUrl("files", "docs", "мой запрос");

        Assert.Contains("root=files", url);
        Assert.Contains("path=docs", url);
        Assert.Contains($"q={Uri.EscapeDataString("мой запрос")}", url);
    }

    [Fact]
    public void BuildBrowseUrl_ForVirtualRoot_KeepsRootAndPrefixedPath()
    {
        var url = SearchNavigation.BuildBrowseUrl("AllRoots", "alpha/movies");

        Assert.Contains("root=AllRoots", url);
        Assert.Contains("path=alpha%2Fmovies", url);
        Assert.DoesNotContain("highlight", url);
    }

    [Fact]
    public void BuildBrowseUrl_ForVirtualRootFile_OpensParentFolderWithHighlight()
    {
        var url = SearchNavigation.BuildBrowseUrl("AllRoots", "alpha/movies", "report.txt");

        Assert.Contains("root=AllRoots", url);
        Assert.Contains("path=alpha%2Fmovies", url);
        Assert.Contains("highlight=report.txt", url);
    }
}

[Collection("Web application factory")]
public sealed class SearchUiTests
{
    [Fact]
    public async Task BrowsePage_ContainsSearchBoxInEnglishByDefault()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse");

        Assert.Contains("placeholder=\"Search in current folder\"", html);
        Assert.Contains(">Search</button>", html);
    }

    [Fact]
    public async Task BrowsePage_ContainsSearchBoxInRussian()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        await SetLanguageAsync(host, "ru");

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse"));

        Assert.Contains("placeholder=\"Поиск в текущей папке\"", html);
        Assert.Contains(">Найти</button>", html);
    }

    [Fact]
    public async Task BrowsePage_WithQuery_RendersResultsHeaderCloseButtonAndRows()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "reports"));
        File.WriteAllText(Path.Combine(workspace.RootPath, "report-2026.txt"), "data");
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=report"));

        Assert.Contains("Search results: \"report\"", html);
        Assert.Contains("aria-label=\"Close search\"", html);
        Assert.Contains("report-2026.txt", html);
        // Каталог-результат открывается по своему пути.
        Assert.Contains("📁", html);
        // В режиме поиска операции над результатами недоступны.
        Assert.DoesNotContain("Create folder", html);
        Assert.DoesNotContain("form-check-input", html);
    }

    [Fact]
    public async Task BrowsePage_WithQueryInRussian_RendersLocalizedHeader()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "note.txt"), "x");
        using var host = CreateHost(workspace);
        await SetLanguageAsync(host, "ru");

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=note"));

        Assert.Contains("Результаты поиска: «note»", html);
        Assert.Contains("aria-label=\"Закрыть поиск\"", html);
        Assert.DoesNotContain("Создать папку", html);
    }

    [Fact]
    public async Task BrowsePage_SearchWithoutMatches_RendersEmptyState()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "x");
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=zzz"));

        Assert.Contains("Nothing found.", html);
    }

    [Fact]
    public async Task BrowsePage_TruncatedSearch_RendersNoticeBelowResults()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 501; index++)
        {
            File.WriteAllText(Path.Combine(workspace.RootPath, $"match-{index:000}.txt"), string.Empty);
        }

        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=match"));

        Assert.Contains("There are more results than shown.", html);
        Assert.Contains("match-000.txt", html);
    }

    [Fact]
    public async Task BrowsePage_SearchWithUnknownRoot_RendersErrorState()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=missing&q=x"));

        Assert.Contains("The root or directory was not found.", html);
    }

    [Fact]
    public async Task BrowsePage_SearchResultAtRootTop_ParentPathShowsRootName()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "solo.txt"), "x");
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse?root=files&q=solo");

        Assert.Contains("solo.txt", html);
        // Подзаголовок всегда показывает путь от корня, с префиксом /.
        Assert.Contains("class=\"search-parent-path\"", html);
        Assert.Contains(">/files</span>", html);
    }

    [Fact]
    public async Task BrowsePage_SearchResults_ShowParentPathBelowName()
    {
        using var workspace = new TestWorkspace();
        var documents = Path.Combine(workspace.RootPath, "documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "demo.txt"), "x");
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=demo"));

        Assert.Contains("demo.txt", html);
        // Подзаголовок: родительский путь от корня, включая имя корня.
        Assert.Contains("class=\"search-parent-path\"", html);
        Assert.Contains(">/files/documents</span>", html);
        // Tooltip ячейки показывает полный путь объекта от корня.
        Assert.Contains("title=\"files/documents/demo.txt\"", html);
    }

    [Fact]
    public async Task BrowsePage_SearchResults_DirectoriesWithSameNameShowParentPath()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "alpha", "shared"));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "beta", "shared"));
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=files&q=shared"));

        Assert.Contains(">/files/alpha</span>", html);
        Assert.Contains(">/files/beta</span>", html);
        Assert.Equal(2, Regex.Matches(html, "search-parent-path").Count);
    }

    [Fact]
    public async Task BrowsePage_WithHighlight_RendersHighlightedRow()
    {
        using var workspace = new TestWorkspace();
        var subDirectory = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(subDirectory);
        File.WriteAllText(Path.Combine(subDirectory, "report.txt"), "x");
        using var host = CreateHost(workspace);

        var url = $"/browse?root=files&path=sub&highlight={Uri.EscapeDataString("report.txt")}";
        var html = await host.Client.GetStringAsync(url);

        Assert.Contains("row-highlighted", html);
    }

    private static TestHostFactory.TestHost CreateHost(TestWorkspace workspace) =>
        TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath));

    private static async Task SetLanguageAsync(TestHostFactory.TestHost host, string language) =>
        await host.Services.GetRequiredService<AppSettingsService>().SetLanguageAsync(language, CancellationToken.None);
}
