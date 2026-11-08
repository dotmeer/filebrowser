using System.Globalization;
using dotmeer.filebrowser.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Localization;

/// <summary>
/// Resolves the request culture from the server-side UI language setting
/// stored in SQLite. The language is a server-wide application setting,
/// so no browser-supplied headers or cookies are consulted.
/// </summary>
public sealed class StoredLanguageProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var settings = httpContext.RequestServices.GetRequiredService<AppSettingsService>();
        var language = settings.GetLanguage();
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(language));
    }

    public static RequestLocalizationOptions CreateOptions() =>
        new()
        {
            DefaultRequestCulture = new RequestCulture(new CultureInfo("en")),
            SupportedCultures = [new CultureInfo("en"), new CultureInfo("ru")],
            SupportedUICultures = [new CultureInfo("en"), new CultureInfo("ru")],
            RequestCultureProviders = [new StoredLanguageProvider()]
        };
}
