using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Auth;

/// <summary>
/// Проверка доступа для файловых HTTP-маршрутов. В LOCAL без действующей
/// сессии — 401, учётной записи без допуска (временный пароль, отозванная
/// или удалённая запись) — 403; права на корень проверяются прикладными
/// сервисами. Ответ всегда ProblemDetails, без редиректа на HTML.
/// </summary>
public static class HttpAccess
{
    public static async Task<IResult?> RejectIfDeniedAsync(
        HttpContext httpContext,
        AuthOptions options,
        AuthService auth,
        IStringLocalizer<SharedResources> localizer)
    {
        if (options.IsAnonymous)
        {
            return null;
        }

        var level = await auth.EvaluateAsync(httpContext.User, httpContext.RequestAborted);
        return level switch
        {
            AccessLevel.Full => null,
            AccessLevel.Anonymous => Problem(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Error_AuthenticationRequired_Title",
                "Error_AuthenticationRequired_Detail",
                httpContext,
                localizer),
            _ => Problem(
                StatusCodes.Status403Forbidden,
                "access_denied",
                "Error_AccessDenied_Title",
                "Error_AccessDenied_Detail",
                httpContext,
                localizer)
        };
    }

    private static IResult Problem(
        int status,
        string code,
        string titleKey,
        string detailKey,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer) =>
        Endpoints.FileSystemProblemMapper.ToJsonResult(
            Endpoints.FileSystemProblemMapper.CreateProblem(
                status,
                code,
                titleKey,
                detailKey,
                httpContext,
                localizer));
}
