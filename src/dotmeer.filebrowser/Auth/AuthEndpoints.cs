using dotmeer.filebrowser.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace dotmeer.filebrowser.Auth;

/// <summary>
/// HTTP-формы входа, первичной настройки, выхода и смены пароля. Все
/// изменяющие формы принимают POST с antiforgery; cookie выдаётся и
/// снимается обычным HTTP-ответом, пригодным для работы с заголовками.
/// </summary>
public static class AuthEndpoints
{
    public const string LoginPath = "/login";
    public const string SetupPath = "/setup";
    public const string ChangePasswordPath = "/change-password";
    public const string LogoutPath = "/auth/logout";

    public static void MapAuthentication(this WebApplication app)
    {
        app.MapPost("/auth/login", HandleLoginAsync);
        app.MapPost("/auth/setup", HandleSetupAsync);
        app.MapPost("/auth/logout", HandleLogoutAsync);
        app.MapPost("/auth/change-password", HandleChangePasswordAsync);
    }

    private static async Task<IResult> HandleLoginAsync(
        HttpContext httpContext,
        AuthOptions options,
        AuthService auth,
        IAntiforgery antiforgery)
    {
        if (options.IsAnonymous)
        {
            return Results.Redirect("/");
        }

        if (!await ValidateAntiforgeryAsync(httpContext, antiforgery))
        {
            return RedirectWithError(LoginPath, "antiforgery");
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var login = form["login"].ToString();
        var password = form["password"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

        if (await auth.AdminExistsAsync(httpContext.RequestAborted) is false)
        {
            return Results.Redirect(SetupPath);
        }

        var (outcome, user) = await auth.AuthenticateAsync(login, password, httpContext.RequestAborted);
        if (outcome != LoginOutcome.Success || user is null)
        {
            // Единый ответ для неизвестного логина и неверного пароля.
            return RedirectWithError(LoginPath, "credentials", returnUrl);
        }

        if (!await auth.SignInAsync(httpContext, user))
        {
            return RedirectWithError(LoginPath, "credentials", returnUrl);
        }
        return Results.Redirect(user.MustChangePassword ? ChangePasswordPath : returnUrl);
    }

    private static async Task<IResult> HandleSetupAsync(
        HttpContext httpContext,
        AuthOptions options,
        AuthService auth,
        IAntiforgery antiforgery)
    {
        if (options.IsAnonymous)
        {
            return Results.Redirect("/");
        }

        if (!await ValidateAntiforgeryAsync(httpContext, antiforgery))
        {
            return RedirectWithError(SetupPath, "antiforgery");
        }

        if (await auth.AdminExistsAsync(httpContext.RequestAborted))
        {
            return Results.Redirect(LoginPath);
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var password = form["password"].ToString();
        var confirmation = form["confirmPassword"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

        if (string.IsNullOrWhiteSpace(password))
        {
            return RedirectWithError(SetupPath, "password_required");
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            return RedirectWithError(SetupPath, "password_mismatch");
        }

        var user = await auth.TrySetupAdminAsync(password, httpContext.RequestAborted);
        if (user is null)
        {
            // Параллельная настройка уже создала admin: проигравший идёт на вход.
            return Results.Redirect(LoginPath);
        }

        if (!await auth.SignInAsync(httpContext, user))
        {
            return Results.Redirect(LoginPath);
        }
        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> HandleLogoutAsync(
        HttpContext httpContext,
        AuthOptions options,
        AuthService auth,
        IAntiforgery antiforgery)
    {
        if (!await ValidateAntiforgeryAsync(httpContext, antiforgery))
        {
            return Results.BadRequest();
        }

        await auth.SignOutAsync(httpContext);
        return Results.Redirect(options.IsLocal ? LoginPath : "/");
    }

    private static async Task<IResult> HandleChangePasswordAsync(
        HttpContext httpContext,
        AuthOptions options,
        AuthService auth,
        UserStore users,
        IAntiforgery antiforgery)
    {
        if (options.IsAnonymous)
        {
            return Results.Redirect("/");
        }

        if (!await ValidateAntiforgeryAsync(httpContext, antiforgery))
        {
            return RedirectWithError(ChangePasswordPath, "antiforgery");
        }

        var session = await auth.ValidatePrincipalAsync(httpContext.User, httpContext.RequestAborted);
        if (session is null)
        {
            return Results.Redirect(LoginPath);
        }

        var user = await users.GetByIdAsync(session.UserId, httpContext.RequestAborted);
        if (user is null)
        {
            return Results.Redirect(LoginPath);
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var currentPassword = form["currentPassword"].ToString();
        var newPassword = form["newPassword"].ToString();
        var confirmation = form["confirmPassword"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return RedirectWithError(ChangePasswordPath, "password_required");
        }

        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            return RedirectWithError(ChangePasswordPath, "password_mismatch");
        }

        var (outcome, newSessionId) = await auth.ChangeOwnPasswordAsync(
            user,
            session.SessionId,
            currentPassword,
            newPassword,
            httpContext.RequestAborted);
        if (outcome == ChangePasswordOutcome.InvalidCurrentPassword)
        {
            return RedirectWithError(ChangePasswordPath, "current_password");
        }

        if (outcome == ChangePasswordOutcome.SamePassword)
        {
            return RedirectWithError(ChangePasswordPath, "same_password");
        }

        if (outcome != ChangePasswordOutcome.Success)
        {
            return Results.Redirect(LoginPath);
        }

        // Прежние сессии уже отозваны; выдаём новую для текущего запроса.
        await auth.SignInChangedPasswordAsync(httpContext, user, newSessionId!.Value);
        return Results.Redirect(returnUrl);
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Возврат после входа/смены пароля — только путь внутри приложения.
    /// Схема, хост и protocol-relative адреса игнорируются.
    /// </summary>
    public static string SafeReturnUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        var candidate = value.Trim();
        if (!candidate.StartsWith('/') || !RedirectHttpResult.IsLocalUrl(candidate))
        {
            return "/";
        }

        if (candidate.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("/setup", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("/auth", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(ChangePasswordPath, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return candidate;
    }

    private static IResult RedirectWithError(string path, string error, string? returnUrl = null)
    {
        var query = new Dictionary<string, string?> { ["error"] = error };
        if (returnUrl is not null)
        {
            query["returnUrl"] = returnUrl;
        }

        return Results.Redirect(QueryHelpers.AddQueryString(path, query!));
    }
}
