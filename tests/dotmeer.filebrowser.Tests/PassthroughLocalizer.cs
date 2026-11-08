using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Tests;

/// <summary>
/// Localizer stub for direct middleware/service instantiation in tests:
/// returns the resource key unchanged.
/// </summary>
internal sealed class PassthroughLocalizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments] =>
        new(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, name, arguments), resourceNotFound: true);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
