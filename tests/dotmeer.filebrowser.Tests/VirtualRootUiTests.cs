using System.Net;
using System.Text.RegularExpressions;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class VirtualRootUiTests
{
    [Fact]
    public async Task BrowsePage_TreeContainsOnlyAllRootsNodeAtTopLevel()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse");

        // Единственный узел верхнего уровня — «Все корни»; настроенные
        // корни не дублируются отдельными узлами (SPEC §9.1): их узлы
        // рендерятся лениво как дети свёрнутого узла «Все корни».
        Assert.Contains("title=\"All roots\"", html);
        Assert.DoesNotContain("title=\"alpha\"", html);
        Assert.DoesNotContain("title=\"beta\"", html);
        Assert.DoesNotContain("title=\"offline\"", html);
    }

    [Fact]
    public async Task BrowsePage_TreeContainsAllRootsNodeInRussian()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        await SetLanguageAsync(host.Client, "ru");

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse"));

        Assert.Contains("title=\"Все корни\"", html);
    }

    [Fact]
    public async Task BrowsePage_VirtualRootListsConfiguredRootsAsDirectories()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=AllRoots"));

        // Доступные корни — каталоги, недоступный — неподдерживаемый элемент.
        Assert.Contains("📁", html);
        Assert.Contains("📁</span> alpha", html);
        Assert.Contains("📁</span> beta", html);
        Assert.Contains("⚠", html);
        Assert.Contains("unsupported-entry", html);
        // Breadcrumbs показывают виртуальный корень.
        Assert.Contains(">AllRoots<", html);
    }

    [Fact]
    public async Task BrowsePage_VirtualRootUnderRealRoot_RendersPrefixedEntries()
    {
        using var workspace = new TestWorkspace();
        var sub = Path.Combine(workspace.RootPath, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "note.txt"), "note");
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse?root=AllRoots&path=alpha/sub");

        Assert.Contains("note.txt", html);
        Assert.Contains("title=\"note.txt\"", html);
        // Breadcrumbs ведут через виртуальный корень по сегментам пути.
        Assert.Contains(">AllRoots</button>", html);
        Assert.Contains(">alpha</button>", html);
        Assert.Contains(">sub</button>", html);
    }

    [Fact]
    public async Task BrowsePage_VirtualRoot_MutationButtonsDisabledWithReadOnlyHint()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);

        var html = await host.Client.GetStringAsync("/browse?root=AllRoots");

        foreach (var label in new[] { "Create folder", "Rename", "Copy", "Move", "Delete" })
        {
            Assert.Matches(
                $"<button[^>]*disabled[^>]*title=\"The virtual root is read-only\\.\"[^>]*>\\s*{label}</button>",
                html);
        }

        // Чтение, обновление и поиск доступны.
        Assert.DoesNotMatch("<button[^>]*disabled[^>]*>\\s*Refresh</button>", html);
        Assert.Contains("placeholder=\"Search in current folder\"", html);
        Assert.DoesNotMatch("<input class=\"form-control search-input\"[^>]*disabled", html);
    }

    [Fact]
    public async Task BrowsePage_VirtualRootSearch_RendersUnavailableRootsNotice()
    {
        using var workspace = new TestWorkspace();
        using var host = CreateHost(workspace);
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");
        File.WriteAllText(Path.Combine(SecondRootPath(workspace), "hit-b.txt"), "b");

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=AllRoots&q=hit"));

        Assert.Contains("hit-a.txt", html);
        Assert.Contains("hit-b.txt", html);
        // Подзаголовки начинаются с имени реального корня — совпадения
        // из разных корней различимы (SPEC §9.7).
        Assert.Contains(">/alpha</span>", html);
        Assert.Contains(">/beta</span>", html);
        // Полный путь с префиксом реального корня — в tooltip ячейки (I22-3).
        Assert.Contains("title=\"alpha/hit-a.txt\"", html);
        Assert.Contains("Some roots are unavailable and were not searched: offline", html);
    }

    [Fact]
    public async Task BrowsePage_VirtualRootSearch_AllRootsAvailable_RendersNoNotice()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RootPath, "hit-a.txt"), "a");
        using var host = CreateAvailableRootsHost(workspace);

        var html = WebUtility.HtmlDecode(await host.Client.GetStringAsync("/browse?root=AllRoots&q=hit"));

        Assert.Contains("hit-a.txt", html);
        Assert.DoesNotContain("search-unavailable-roots", html);
        Assert.DoesNotContain("were not searched", html);
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

    private static TestHostFactory.TestHost CreateAvailableRootsHost(TestWorkspace workspace)
    {
        var secondRootPath = SecondRootPath(workspace);
        Directory.CreateDirectory(secondRootPath);
        return TestHostFactory.Create(workspace.WriteRawConfiguration(
            $"roots:\n  - name: alpha\n    path: '{workspace.RootPath}'\n  - name: beta\n    path: '{secondRootPath}'\n"));
    }

    private static async Task SetLanguageAsync(HttpClient client, string language)
    {
        var html = await client.GetStringAsync("/settings");
        var match = Regex.Match(html, "name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"(?<token>[^\\\"]+)\\\"");
        Assert.True(match.Success, "На странице настроек не найден antiforgery token.");

        var response = await client.PostAsync("/settings/language", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("language", language),
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups["token"].Value))
        ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
