using System.Diagnostics;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Uploads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Общий маппинг исключений файловых операций в ProblemDetails-контракт
/// (application/problem+json, стабильные поля code и traceId,
/// локализованные title/detail, без физических путей). Разделяется
/// эндпоинтами скачивания и предпросмотра.
/// </summary>
public static class FileSystemProblemMapper
{
    /// <summary>
    /// Маппит исключение файловой системы в ProblemDetails и пишет
    /// соответствующую запись в лог.
    /// </summary>
    /// <param name="operation">Название операции для лога: «скачивание» или «предпросмотр».</param>
    public static ProblemDetails Create(
        Exception exception,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILogger logger,
        string operation)
    {
        var (status, code, titleKey, detailKey) = exception switch
        {
            ArchiveLimitExceededException => (StatusCodes.Status400BadRequest, "archive_too_large", "Error_ArchiveTooLarge_Title", "Error_ArchiveTooLarge_Detail"),
            ArchiveBusyException => (StatusCodes.Status429TooManyRequests, "archive_busy", "Error_ArchiveBusy_Title", "Error_ArchiveBusy_Detail"),
            ArchiveScanTimeoutException => (StatusCodes.Status503ServiceUnavailable, "scan_timeout", "Error_ScanTimeout_Title", "Error_ScanTimeout_Detail"),
            FileTooLargeException => (StatusCodes.Status413PayloadTooLarge, "file_too_large", "Error_FileTooLarge_Title", "Error_FileTooLarge_Detail"),
            InsufficientSpaceException => (StatusCodes.Status507InsufficientStorage, "insufficient_space", "Error_InsufficientSpace_Title", "Error_InsufficientSpace_Detail"),
            UploadIncompleteException => (StatusCodes.Status400BadRequest, "upload_incomplete", "Error_UploadIncomplete_Title", "Error_UploadIncomplete_Detail"),
            FileSystemNameConflictException => (StatusCodes.Status409Conflict, "name_conflict", "Error_NameConflict_Title", "Error_NameConflict_Detail"),
            RootUnavailableException => (StatusCodes.Status503ServiceUnavailable, "root_unavailable", "Error_RootUnavailable_Title", "Error_RootUnavailable_Detail"),
            FileSystemObjectNotFoundException => (StatusCodes.Status404NotFound, "not_found", "Error_NotFound_Title", "Error_NotFound_Detail"),
            FileSystemAccessDeniedException => (StatusCodes.Status403Forbidden, "access_denied", "Error_AccessDenied_Title", "Error_AccessDenied_Detail"),
            UnsupportedFileSystemObjectException => (StatusCodes.Status422UnprocessableEntity, "unsupported_object", "Error_UnsupportedObject_Title", "Error_UnsupportedObject_Detail"),
            InvalidFileSystemRequestException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
            FileSystemResolutionException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidPath_Detail"),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", "Error_InvalidRequest_Title", "Error_InvalidRequest_Detail"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Error_Internal_Title", "Error_Internal_Detail")
        };

        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        if (status >= 500)
        {
            logger.LogError(exception, "Ошибка операции «{Operation}» файла {TraceId}", operation, traceId);
        }
        else
        {
            logger.LogWarning("Отклонена операция «{Operation}» файла {TraceId}: {Status} {Code}", operation, traceId, status, code);
        }

        return CreateProblem(status, code, titleKey, detailKey, httpContext, localizer);
    }

    /// <summary>
    /// Собирает ProblemDetails из явных полей (для ошибок без исключения,
    /// например 415 для расширения вне whitelist предпросмотра).
    /// </summary>
    public static ProblemDetails CreateProblem(
        int status,
        string code,
        string titleKey,
        string detailKey,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
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

    /// <summary>
    /// Сериализует ProblemDetails как JSON-ответ с media type
    /// application/problem+json.
    /// </summary>
    public static IResult ToJsonResult(ProblemDetails problem) =>
        Results.Json(
            problem,
            contentType: "application/problem+json",
            statusCode: problem.Status);
}
