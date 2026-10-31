using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Endpoints;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (context.Request.Path.StartsWithSegments("/api"))
        {
            var (status, code, title, detail) = exception switch
            {
                RootUnavailableException => (StatusCodes.Status503ServiceUnavailable, "root_unavailable", "Корень недоступен", "Файловый корень временно недоступен."),
                FileSystemObjectNotFoundException => (StatusCodes.Status404NotFound, "not_found", "Объект не найден", "Запрошенный корень или объект не найден."),
                UnsupportedFileSystemObjectException => (StatusCodes.Status422UnprocessableEntity, "unsupported_object", "Объект не поддерживается", "Этот тип файловой системы не поддерживается."),
                InvalidFileSystemRequestException => (StatusCodes.Status400BadRequest, "invalid_request", "Некорректный запрос", "Проверьте root и относительный path."),
                FileSystemResolutionException => (StatusCodes.Status400BadRequest, "invalid_request", "Некорректный запрос", "Проверьте root и относительный path."),
                _ => (StatusCodes.Status500InternalServerError, "internal_error", "Внутренняя ошибка", "Не удалось выполнить запрос.")
            };

            if (status >= 500)
            {
                logger.LogError(exception, "Ошибка API запроса {TraceId}", context.TraceIdentifier);
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Instance = context.Request.Path
            };
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = context.TraceIdentifier;
            await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
        }
    }
}
