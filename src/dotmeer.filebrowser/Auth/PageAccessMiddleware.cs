namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Закрывает HTML-страницы приложения в LOCAL на уровне HTTP. Проверка
/// выполняется до рендеринга Blazor, поэтому прямой GET не получает даже
/// разметку рабочего UI без действующей учётки. Файловые маршруты проверяют
/// доступ сами и отвечают ProblemDetails, а не редиректом.
/// </summary>
public sealed class PageAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthOptions _options;
    private readonly AuthService _auth;

    public PageAccessMiddleware(RequestDelegate next, AuthOptions options, AuthService auth)
    {
        _next = next;
        _options = options;
        _auth = auth;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsPageRequest(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        if (_options.IsAnonymous)
        {
            if (IsAuthPage(path) || IsAdminOnlyPage(path))
            {
                context.Response.Redirect("/");
                return;
            }

            await _next(context);
            return;
        }

        var adminExists = await _auth.AdminExistsAsync(context.RequestAborted);
        if (string.Equals(path, "/setup", StringComparison.OrdinalIgnoreCase))
        {
            if (adminExists)
            {
                context.Response.Redirect("/login");
                return;
            }

            await _next(context);
            return;
        }

        if (!adminExists)
        {
            context.Response.Redirect($"/setup?returnUrl={Uri.EscapeDataString(BuildReturnUrl(context.Request))}");
            return;
        }

        if (string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase))
        {
            await RedirectAuthenticatedAsync(context);
            return;
        }

        var level = await _auth.EvaluateAsync(context.User, context.RequestAborted);
        switch (level)
        {
            case AccessLevel.Anonymous:
                context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(BuildReturnUrl(context.Request))}");
                return;
            case AccessLevel.MustChangePassword:
                if (!string.Equals(path, "/change-password", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/change-password");
                    return;
                }

                break;
            case AccessLevel.Denied:
                if (!string.Equals(path, "/change-password", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, "/access-denied", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/access-denied");
                    return;
                }

                break;
            case AccessLevel.Full:
                if (IsAdminOnlyPage(path)
                    && (await _auth.GetCurrentSessionAsync(context.User, context.RequestAborted))?.IsAdmin != true)
                {
                    context.Response.Redirect("/access-denied");
                    return;
                }

                if (string.Equals(path, "/access-denied", StringComparison.OrdinalIgnoreCase))
                {
                    // Показываем причину отказа вместо молчаливого возврата
                    // на главную (например, прямой адрес /users).
                    await _next(context);
                    return;
                }

                if (string.Equals(path, "/change-password", StringComparison.OrdinalIgnoreCase))
                {
                    await _next(context);
                    return;
                }

                break;
        }

        await _next(context);
    }

    private async Task RedirectAuthenticatedAsync(HttpContext context)
    {
        var level = await _auth.EvaluateAsync(context.User, context.RequestAborted);
        if (level == AccessLevel.Anonymous)
        {
            await _next(context);
            return;
        }

        context.Response.Redirect(AccessGuard.GetRedirectPath(level));
    }

    private static bool IsAuthPage(string path) =>
        string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/setup", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/change-password", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/access-denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>Страницы, доступные только встроенной admin.</summary>
    private static bool IsAdminOnlyPage(string path) =>
        string.Equals(path, "/users", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/users/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Страницы приложения — всё, кроме API, health, auth-обработчиков,
    /// инфраструктуры Blazor и статических файлов с расширением.
    /// </summary>
    private static bool IsPageRequest(PathString pathString)
    {
        if (!pathString.HasValue)
        {
            return false;
        }

        var path = pathString.Value!;
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_", StringComparison.Ordinal))
        {
            return false;
        }

        var lastSegment = path.AsSpan(path.LastIndexOf('/') + 1);
        return lastSegment.IndexOf('.') < 0;
    }

    private static string BuildReturnUrl(HttpRequest request) =>
        $"{request.PathBase}{request.Path}{request.QueryString}";
}
