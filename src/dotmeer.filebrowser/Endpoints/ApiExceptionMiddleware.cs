using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Localization;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;

namespace dotmeer.filebrowser.Endpoints;

public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionMiddleware> logger,
    IStringLocalizer<SharedResources> localizer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (context.Request.Path.StartsWithSegments("/api"))
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["TraceId"] = traceId,
                ["HttpMethod"] = context.Request.Method,
                ["RequestPath"] = context.Request.Path.Value
            });

            var (status, code, titleKey, detailKey) = exception switch
            {
                RootUnavailableException => (StatusCodes.Status503ServiceUnavailable, "root_unavailable", "Error_RootUnavailable_Title", "Error_RootUnavailable_Detail"),
                FileSystemObjectNotFoundException => (StatusCodes.Status404NotFound, "not_found", "Error_NotFound_Title", "Error_NotFound_Detail"),
                FileSystemNameConflictException => (StatusCodes.Status409Conflict, "name_conflict", "Error_NameConflict_Title", "Error_NameConflict_Detail"),
                FileSystemAccessDeniedException => (StatusCodes.Status403Forbidden, "access_denied", "Error_AccessDenied_Title", "Error_AccessDenied_Detail"),
                AntiforgeryRequestRejectedException or AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "antiforgery_validation_failed", "Error_Antiforgery_Title", "Error_Antiforgery_Detail"),
                FileTaskNotFoundException => (StatusCodes.Status404NotFound, "task_not_found", "Error_TaskNotFound_Title", "Error_TaskNotFound_Detail"),
                FileTaskQueueFullException => (StatusCodes.Status429TooManyRequests, "task_queue_full", "Error_TaskQueueFull_Title", "Error_TaskQueueFull_Detail"),
                ArgumentOutOfRangeException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidRequest_Detail"),
                ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidRequest_Detail"),
                UnsupportedFileSystemObjectException => (StatusCodes.Status422UnprocessableEntity, "unsupported_object", "Error_UnsupportedObject_Title", "Error_UnsupportedObject_Detail"),
                InvalidFileSystemRequestException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
                FileSystemResolutionException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
                _ => (StatusCodes.Status500InternalServerError, "internal_error", "Error_Internal_Title", "Error_Internal_Detail")
            };

            if (status >= 500)
            {
                logger.LogError(exception, "Ошибка API запроса {TraceId}", traceId);
            }
            else
            {
                logger.LogWarning("Отклонён API запрос {TraceId}: {Status} {Code}", traceId, status, code);
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
                Title = localizer[titleKey],
                Detail = localizer[detailKey],
                Instance = context.Request.Path
            };
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = traceId;
            await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
        }
    }
}
