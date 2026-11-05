using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;

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
                FileSystemNameConflictException => (StatusCodes.Status409Conflict, "name_conflict", "Конфликт имени", "Объект с таким именем уже существует."),
                FileSystemAccessDeniedException => (StatusCodes.Status403Forbidden, "access_denied", "Нет доступа", "Недостаточно прав для выполнения операции."),
                AntiforgeryRequestRejectedException or AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "antiforgery_validation_failed", "Запрос отклонён", "Не удалось подтвердить защитный токен запроса."),
                FileTaskNotFoundException => (StatusCodes.Status404NotFound, "task_not_found", "Задача не найдена", "Запрошенная задача не найдена."),
                FileTaskQueueFullException => (StatusCodes.Status429TooManyRequests, "task_queue_full", "Очередь заполнена", "Очередь задач заполнена. Повторите попытку позже."),
                ArgumentOutOfRangeException => (StatusCodes.Status400BadRequest, "invalid_request", "Некорректный запрос", "Проверьте параметры запроса."),
                ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", "Некорректный запрос", "Проверьте параметры запроса."),
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
