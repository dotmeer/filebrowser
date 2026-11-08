using dotmeer.filebrowser.Settings;
using Microsoft.AspNetCore.Antiforgery;

namespace dotmeer.filebrowser.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettings(this WebApplication app)
    {
        app.MapPost("/settings/language", async (HttpContext context, AppSettingsService settings, CancellationToken cancellationToken) =>
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                var language = form["language"].ToString();
                if (!AppSettingsService.IsSupportedLanguage(language))
                {
                    return Results.BadRequest(new { code = "invalid_request" });
                }

                await settings.SetLanguageAsync(language, cancellationToken);
                // Full navigation (not enhanced) so the new culture applies to
                // the re-rendered page and future Blazor circuits.
                return Results.Redirect("/settings", permanent: false, preserveMethod: false);
            })
            .RequireCsrfValidation();
    }

    private static RouteHandlerBuilder RequireCsrfValidation(this RouteHandlerBuilder endpoint) =>
        endpoint
            .WithMetadata(new RequireAntiforgeryTokenAttribute(required: true))
            .AddEndpointFilter(async (context, next) =>
            {
                var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(context.HttpContext);
                }
                catch (Exception)
                {
                    // This endpoint is outside /api, so ApiExceptionMiddleware does not
                    // translate exceptions; respond directly instead of rethrowing.
                    return Results.BadRequest(new { code = "antiforgery_validation_failed" });
                }

                return await next(context);
            });
}
