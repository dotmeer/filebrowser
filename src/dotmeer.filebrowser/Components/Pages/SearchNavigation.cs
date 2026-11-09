using Microsoft.AspNetCore.WebUtilities;

namespace dotmeer.filebrowser.Components.Pages;

/// <summary>
/// Построение URL навигации из результатов поиска (SPEC §9.7):
/// каталог открывается по своему пути, файл — через родительскую папку
/// с параметром подсветки highlight. Имена кодируются в URL.
/// </summary>
public static class SearchNavigation
{
    /// <summary>
    /// Родительский путь объекта (всё до последнего '/' в его пути).
    /// Пустая строка, если сепаратора нет, — объект лежит непосредственно
    /// в корне поисковой области; имя корня для подзаголовка добавляет
    /// вызывающий код (SPEC §9.7).
    /// </summary>
    public static string GetParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    public static string BuildBrowseUrl(string root, string path, string? highlight = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["root"] = root,
            ["path"] = path
        };
        if (!string.IsNullOrEmpty(highlight))
        {
            query["highlight"] = highlight;
        }

        return QueryHelpers.AddQueryString("/browse", query);
    }

    public static string BuildSearchUrl(string root, string path, string query)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["root"] = root,
            ["path"] = path,
            ["q"] = query
        };
        return QueryHelpers.AddQueryString("/browse", parameters);
    }
}
