namespace dotmeer.filebrowser.FileSystem;

/// <summary>
/// Виртуальный корень <see cref="VirtualRootName"/> (SPEC §7.1): объединяет все настроенные
/// корни в единое пространство имён. На верхнем уровне он доступен только
/// для чтения; путь под ним делегируется реальному корню для всех операций.
/// Имя намеренно вне алфавита имён корней конфигурации ([a-z0-9_-], SPEC §6.1),
/// поэтому коллизия с реальным корнём невозможна и правила конфигурации не меняются.
/// </summary>
public static class VirtualRoot
{
    public const string VirtualRootName = "AllRoots";

    public static bool IsVirtualRoot(string? root) =>
        string.Equals(root, VirtualRootName, StringComparison.Ordinal);

    /// <summary>
    /// Разделяет непустой путь под виртуальным корнем на имя реального корня
    /// (первый сегмент) и остаток пути. Неизвестный первый сегмент отклоняется
    /// делегированием в resolver реального корня (404/not_found).
    /// </summary>
    public static (string RootName, string RemainingPath) SplitPath(string path)
    {
        var separator = path.IndexOf('/');
        return separator < 0
            ? (path, string.Empty)
            : (path[..separator], path[(separator + 1)..]);
    }
}
