namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Health check endpoint.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps GET /healthz.
    /// </summary>
    /// <remarks>
    /// I01: the application has no external dependencies yet and is ready
    /// as soon as it responds. Later iterations return 503 when a critical
    /// dependency (for example SQLite) is unavailable. The response body
    /// never contains physical paths.
    /// </remarks>
    public static void MapHealthz(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new HealthzResponse("ok")));
    }
}

/// <summary>
/// Health check response.
/// </summary>
public sealed record HealthzResponse(string Status);