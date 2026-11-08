namespace dotmeer.filebrowser;

/// <summary>
/// Marker type for shared UI string resources. The type lives in the project
/// root namespace on purpose: with LocalizationOptions.ResourcesPath set to
/// "Localization", the localizer factory resolves the resource base name
/// "dotmeer.filebrowser.Localization.SharedResources", which matches the
/// embedded resx files (Localization/SharedResources.resx and
/// Localization/SharedResources.ru.resx).
/// </summary>
public sealed class SharedResources
{
}
