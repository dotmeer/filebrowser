using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Tests;

public sealed class PreviewCatalogTests
{
    [Theory]
    [InlineData("photo.png", PreviewKind.Image, "image/png")]
    [InlineData("photo.jpg", PreviewKind.Image, "image/jpeg")]
    [InlineData("photo.jpeg", PreviewKind.Image, "image/jpeg")]
    [InlineData("anim.gif", PreviewKind.Image, "image/gif")]
    [InlineData("pic.webp", PreviewKind.Image, "image/webp")]
    [InlineData("scan.bmp", PreviewKind.Image, "image/bmp")]
    [InlineData("favicon.ico", PreviewKind.Image, "image/x-icon")]
    [InlineData("report.pdf", PreviewKind.Pdf, "application/pdf")]
    [InlineData("notes.txt", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("README.md", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("app.log", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("data.csv", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("appsettings.json", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("project.xml", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("compose.yml", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("compose.yaml", PreviewKind.Text, "text/plain; charset=utf-8")]
    public void Find_KnownExtension_ReturnsDefinition(string fileName, PreviewKind kind, string contentType)
    {
        var definition = PreviewCatalog.Find(fileName);

        Assert.NotNull(definition);
        Assert.Equal(kind, definition!.Kind);
        Assert.Equal(contentType, definition.ContentType);
    }

    [Theory]
    [InlineData("program.cs", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("component.razor", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("app.csproj", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("site.js", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("site.css", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("helper.ps1", PreviewKind.Text, "text/plain; charset=utf-8")]
    [InlineData("settings.ini", PreviewKind.Text, "text/plain; charset=utf-8")]
    public void Find_SourceCodeExtensions_ArePreviewedAsPlainText(string fileName, PreviewKind kind, string contentType)
    {
        // Код отдаётся строго как text/plain: браузер показывает его как
        // текст, исполнения нет. SVG/HTML в whitelist отсутствуют.
        var definition = PreviewCatalog.Find(fileName);

        Assert.NotNull(definition);
        Assert.Equal(kind, definition!.Kind);
        Assert.Equal(contentType, definition.ContentType);
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("program.exe")]
    [InlineData("no-extension")]
    [InlineData("trailing-dot.")]
    [InlineData(".gitignore")]
    [InlineData("")]
    public void Find_UnknownOrMissingExtension_ReturnsNull(string fileName)
    {
        Assert.Null(PreviewCatalog.Find(fileName));
    }

    [Theory]
    [InlineData("vector.svg")]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    public void Find_ExcludesInlineExecutableMarkup(string fileName)
    {
        // SVG/HTML исключены из whitelist: inline-отдача с того же origin
        // привела бы к XSS (решение D084, SPEC §16).
        Assert.Null(PreviewCatalog.Find(fileName));
    }

    [Fact]
    public void Find_MatchesExtensionCaseInsensitively()
    {
        var definition = PreviewCatalog.Find("ГОДОВОЙ ОТЧЁТ 2026.PDF");

        Assert.NotNull(definition);
        Assert.Equal(PreviewKind.Pdf, definition!.Kind);
    }

    [Fact]
    public void TextPreviewLimit_Is512KiB()
    {
        Assert.Equal(512 * 1024, PreviewCatalog.TextPreviewLimitBytes);
    }
}
