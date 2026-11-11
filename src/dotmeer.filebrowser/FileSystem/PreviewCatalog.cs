namespace dotmeer.filebrowser.FileSystem;

/// <summary>
/// Вид предпросмотра файла. Определяется серверным whitelist расширений
/// (SPEC §16 «Предпросмотр»), никогда — содержимым файла.
/// </summary>
public enum PreviewKind
{
    Image,
    Pdf,
    Text
}

/// <summary>
/// Описание предпросмотра для одного расширения: вид и MIME-тип ответа.
/// </summary>
public sealed record PreviewDefinition(PreviewKind Kind, string ContentType);

/// <summary>
/// Серверный whitelist «расширение → вид предпросмотра и MIME-тип»
/// (SPEC §16 «Предпросмотр»). Единственный источник типизации контента
/// для эндпоинта <c>/api/fs/preview</c> и UI (компоненты и эндпоинты живут
/// в одном проекте и разделяют этот класс, дублирования whitelist нет).
/// </summary>
/// <remarks>
/// SVG и HTML намеренно отсутствуют: их inline-отдача с того же origin
/// приводит к XSS (решение D084). Видео и аудио не поддерживаются и не
/// планируются (SPEC §3, ROADMAP «Намеренно не включено»).
/// </remarks>
public static class PreviewCatalog
{
    /// <summary>
    /// Потолок текстового предпросмотра: первые 512 КиБ файла
    /// (SPEC §16 «Предпросмотр»). Больший объём читается скачиванием.
    /// </summary>
    public const long TextPreviewLimitBytes = 512 * 1024;

    private static readonly IReadOnlyDictionary<string, PreviewDefinition> Definitions =
        new Dictionary<string, PreviewDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["png"] = new(PreviewKind.Image, "image/png"),
            ["jpg"] = new(PreviewKind.Image, "image/jpeg"),
            ["jpeg"] = new(PreviewKind.Image, "image/jpeg"),
            ["gif"] = new(PreviewKind.Image, "image/gif"),
            ["webp"] = new(PreviewKind.Image, "image/webp"),
            ["bmp"] = new(PreviewKind.Image, "image/bmp"),
            ["ico"] = new(PreviewKind.Image, "image/x-icon"),
            ["pdf"] = new(PreviewKind.Pdf, "application/pdf"),
            ["txt"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["md"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["log"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["csv"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["json"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["xml"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["yml"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["yaml"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["cs"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["razor"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["csproj"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["sln"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["js"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["css"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["ts"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["py"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["sh"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["ps1"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["sql"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["ini"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
            ["conf"] = new(PreviewKind.Text, "text/plain; charset=utf-8"),
        };

    /// <summary>
    /// Возвращает описание предпросмотра по имени файла или null, если
    /// расширение отсутствует или не входит в whitelist. Сравнение
    /// расширения регистронезависимое (ordinal-ignore-case).
    /// </summary>
    public static PreviewDefinition? Find(string fileName)
    {
        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == fileName.Length - 1)
        {
            return null;
        }

        var extension = fileName[(dotIndex + 1)..];
        return Definitions.TryGetValue(extension, out var definition) ? definition : null;
    }
}
