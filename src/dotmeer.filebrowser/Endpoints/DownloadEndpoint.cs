using System.Diagnostics;
using dotmeer.filebrowser.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Единственный сохраняемый публичный эндпоинт: потоковая отдача файла
/// для кнопки «Скачать» в UI. Остальной HTTP API удалён (решение D-серии
/// «Удаление внешнего HTTP API»): изменяющие операции доступны только
/// через Blazor, который вызывает прикладные сервисы напрямую.
/// </summary>
/// <remarks>
/// Ошибки маппятся локально в delegate без ApiExceptionMiddleware: тот же
/// ProblemDetails-контракт (application/problem+json, стабильные поля code
/// и traceId, локализованные title/detail, без физических путей).
/// </remarks>
public static class DownloadEndpoint
{
    public static void MapDownload(this WebApplication app)
    {
        app.MapGet("/api/fs/download", (
                string? root,
                string? path,
                FileSystemReadService reader,
                HttpContext httpContext,
                IStringLocalizer<SharedResources> localizer,
                ILoggerFactory loggerFactory) =>
            {
                try
                {
                    var file = reader.OpenFile(new FileRef(root ?? string.Empty, path ?? string.Empty));
                    return Results.File(
                        file.Stream,
                        contentType: "application/octet-stream",
                        fileDownloadName: file.Name,
                        enableRangeProcessing: true);
                }
                catch (Exception exception) when (!httpContext.Response.HasStarted)
                {
                    var problem = CreateProblem(exception, httpContext, localizer, loggerFactory.CreateLogger("dotmeer.filebrowser.Download"));
                    return Results.Json(
                        problem,
                        contentType: "application/problem+json",
                        statusCode: problem.Status);
                }
            });
    }

    private static ProblemDetails CreateProblem(
        Exception exception,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILogger logger)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        var (status, code, titleKey, detailKey) = exception switch
        {
            RootUnavailableException => (StatusCodes.Status503ServiceUnavailable, "root_unavailable", "Error_RootUnavailable_Title", "Error_RootUnavailable_Detail"),
            FileSystemObjectNotFoundException => (StatusCodes.Status404NotFound, "not_found", "Error_NotFound_Title", "Error_NotFound_Detail"),
            FileSystemAccessDeniedException => (StatusCodes.Status403Forbidden, "access_denied", "Error_AccessDenied_Title", "Error_AccessDenied_Detail"),
            UnsupportedFileSystemObjectException => (StatusCodes.Status422UnprocessableEntity, "unsupported_object", "Error_UnsupportedObject_Title", "Error_UnsupportedObject_Detail"),
            InvalidFileSystemRequestException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
            FileSystemResolutionException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidRequest_Detail"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Error_Internal_Title", "Error_Internal_Detail")
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Ошибка скачивания файла {TraceId}", traceId);
        }
        else
        {
            logger.LogWarning("Отклонено скачивание файла {TraceId}: {Status} {Code}", traceId, status, code);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = localizer[titleKey],
            Detail = localizer[detailKey],
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = traceId;
        return problem;
    }
}
