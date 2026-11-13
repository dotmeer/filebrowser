using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.Uploads;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Приём файла из браузера (SPEC §11, §13.10, §16). Это единственный
/// изменяющий HTTP-эндпоинт приложения: байты нельзя передавать через
/// Blazor circuit, поэтому загрузчик в браузере пишет их напрямую.
/// Запрос защищён antiforgery (заголовок X-CSRF-TOKEN), принимает одно
/// тело application/octet-stream и адресует файл парой root + path + name.
/// </summary>
public static class UploadEndpoint
{
    public static void MapUpload(this WebApplication app)
    {
        app.MapGet("/api/fs/upload/check", HandleCheck);
        app.MapPost("/api/fs/upload", HandleUploadAsync);
    }

    private static IResult HandleCheck(
        string? root,
        string? path,
        string? name,
        long? size,
        UploadService uploads,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILoggerFactory loggerFactory,
        UploadMetrics metrics)
    {
        if (!TryValidateName(name, httpContext, localizer, metrics, out var invalidName))
        {
            return invalidName;
        }

        try
        {
            uploads.Prepare(new FileRef(root ?? string.Empty, path ?? string.Empty), name!, size);
            return Results.NoContent();
        }
        catch (Exception exception)
        {
            metrics.UploadRejected();
            return MapProblem(exception, httpContext, localizer, loggerFactory);
        }
    }

    private static async Task<IResult> HandleUploadAsync(
        string? root,
        string? path,
        string? name,
        UploadService uploads,
        TaskRepository repository,
        FileBrowserConfiguration configuration,
        TaskCancellationRegistry cancellationRegistry,
        TaskProgressService runtimeProgress,
        UploadMetrics metrics,
        IAntiforgery antiforgery,
        IHostApplicationLifetime lifetime,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILoggerFactory loggerFactory)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            metrics.UploadRejected();
            return Problem(
                StatusCodes.Status400BadRequest,
                "antiforgery_failed",
                "Error_Antiforgery_Title",
                "Error_Antiforgery_Detail",
                httpContext,
                localizer);
        }

        if (!TryValidateName(name, httpContext, localizer, metrics, out var invalidName))
        {
            return invalidName;
        }

        var contentLength = httpContext.Request.ContentLength;
        if (contentLength is null)
        {
            metrics.UploadRejected();
            return Problem(
                StatusCodes.Status411LengthRequired,
                "length_required",
                "Error_LengthRequired_Title",
                "Error_LengthRequired_Detail",
                httpContext,
                localizer);
        }

        Guid? uploadId = null;
        var uploadIdValue = httpContext.Request.Query["uploadId"].ToString();
        if (uploadIdValue.Length > 0)
        {
            if (!Guid.TryParse(uploadIdValue, out var parsedUploadId) || parsedUploadId == Guid.Empty)
            {
                metrics.UploadRejected();
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "invalid_upload_id",
                    "Error_InvalidUploadId_Title",
                    "Error_InvalidUploadId_Detail",
                    httpContext,
                    localizer);
            }

            if (await repository.GetAsync(parsedUploadId, httpContext.RequestAborted) is not null)
            {
                metrics.UploadRejected();
                return Problem(
                    StatusCodes.Status409Conflict,
                    "upload_id_conflict",
                    "Error_UploadIdConflict_Title",
                    "Error_UploadIdConflict_Detail",
                    httpContext,
                    localizer);
            }

            uploadId = parsedUploadId;
        }

        var bodyLimit = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = configuration.Uploads.MaxFileBytes;
        }

        UploadPreflight preflight;
        try
        {
            preflight = uploads.Prepare(
                new FileRef(root ?? string.Empty, path ?? string.Empty),
                name!,
                contentLength);
        }
        catch (Exception exception)
        {
            metrics.UploadRejected();
            return MapProblem(exception, httpContext, localizer, loggerFactory);
        }

        UploadSession? session = null;
        CancellationTokenSource? userCancellation = null;
        var taskId = Guid.Empty;
        var registeredCancellationId = Guid.Empty;
        try
        {
            if (uploadId is { } requestedId)
            {
                // Регистрация до создания задачи закрывает окно гонки:
                // отмена, пришедшая раньше, уже лежит как pending. Только
                // один запрос владеет источником отмены для uploadId.
                if (!cancellationRegistry.TryRegister(requestedId, out userCancellation))
                {
                    metrics.UploadRejected();
                    return Problem(
                        StatusCodes.Status409Conflict,
                        "upload_id_conflict",
                        "Error_UploadIdConflict_Title",
                        "Error_UploadIdConflict_Detail",
                        httpContext,
                        localizer);
                }

                registeredCancellationId = requestedId;
                if (userCancellation.IsCancellationRequested)
                {
                    return Problem(
                        StatusCodes.Status409Conflict,
                        "upload_cancelled",
                        "Error_UploadCancelled_Title",
                        "Error_UploadCancelled_Detail",
                        httpContext,
                        localizer);
                }
            }

            session = await uploads.StartAsync(preflight, httpContext.RequestAborted, uploadId);
            taskId = session.TaskId;

            if (userCancellation is null)
            {
                userCancellation = cancellationRegistry.Register(taskId);
                registeredCancellationId = taskId;
            }

            var created = await repository.GetAsync(taskId, httpContext.RequestAborted);
            if (created?.Status == FileTaskStatus.Cancelling)
            {
                userCancellation.Cancel();
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                lifetime.ApplicationStopping,
                userCancellation.Token);
            using var bodyReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                lifetime.ApplicationStopping);

            var bytes = await uploads.ReceiveAsync(
                session,
                httpContext.Request.BodyReader,
                linkedCancellation.Token,
                bodyReadCancellation.Token);
            var outcome = await uploads.PublishAsync(session, bytes, CancellationToken.None);
            if (outcome == UploadPublishOutcome.Cancelled)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "upload_cancelled",
                    "Error_UploadCancelled_Title",
                    "Error_UploadCancelled_Detail",
                    httpContext,
                    localizer);
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Json(
                new UploadResponse(preflight.FinalPath.Root, preflight.FinalPath.RelativePath, bytes),
                statusCode: StatusCodes.Status201Created);
        }
        catch (UploadPublishedException exception)
        {
            // Файл уже опубликован: AbortAsync уничтожил бы результат приёма.
            return MapProblem(exception.InnerException ?? exception, httpContext, localizer, loggerFactory);
        }
        catch (FileTaskIdConflictException)
        {
            // Конкурент успел создать задачу с тем же uploadId.
            metrics.UploadRejected();
            return Problem(
                StatusCodes.Status409Conflict,
                "upload_id_conflict",
                "Error_UploadIdConflict_Title",
                "Error_UploadIdConflict_Detail",
                httpContext,
                localizer);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Остановка приложения — не пользовательская отмена: задача
            // остаётся активной, временный артефакт удалит очистка при старте
            // (SPEC §13.2, §13.5, §13.8).
            return Results.Empty;
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            await AbortAsync(session, UploadAbortReason.ClientDisconnected, FileTaskErrorCodes.UploadAborted);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (userCancellation?.IsCancellationRequested == true)
        {
            await AbortAsync(session, UploadAbortReason.UserCancelled, FileTaskErrorCodes.CancelledByUser);
            return Problem(
                StatusCodes.Status409Conflict,
                "upload_cancelled",
                "Error_UploadCancelled_Title",
                "Error_UploadCancelled_Detail",
                httpContext,
                localizer);
        }
        catch (InvalidFileTaskTransitionException) when (userCancellation?.IsCancellationRequested == true)
        {
            // Отмена выиграла гонку с переходом scanning → running.
            await AbortAsync(session, UploadAbortReason.UserCancelled, FileTaskErrorCodes.CancelledByUser);
            return Problem(
                StatusCodes.Status409Conflict,
                "upload_cancelled",
                "Error_UploadCancelled_Title",
                "Error_UploadCancelled_Detail",
                httpContext,
                localizer);
        }
        catch (BadHttpRequestException)
        {
            // Клиент оборвал тело раньше объявленного Content-Length.
            await AbortAsync(session, UploadAbortReason.ClientDisconnected, FileTaskErrorCodes.UploadAborted);
            return Results.Empty;
        }
        catch (IOException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            await AbortAsync(session, UploadAbortReason.ClientDisconnected, FileTaskErrorCodes.UploadAborted);
            return Results.Empty;
        }
        catch (Exception exception)
        {
            await AbortAsync(session, UploadAbortReason.Error, ToErrorCode(exception));
            return MapProblem(exception, httpContext, localizer, loggerFactory);
        }
        finally
        {
            var cancellationId = taskId != Guid.Empty ? taskId : registeredCancellationId;
            if (cancellationId != Guid.Empty)
            {
                cancellationRegistry.Unregister(cancellationId);
            }

            if (taskId != Guid.Empty)
            {
                runtimeProgress.Clear(taskId);
            }
        }

        async Task AbortAsync(UploadSession? currentSession, UploadAbortReason reason, string errorCode)
        {
            if (currentSession is null)
            {
                return;
            }

            await uploads.AbortAsync(currentSession, reason, errorCode);
        }
    }

    private static bool TryValidateName(
        string? name,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        UploadMetrics metrics,
        out IResult result)
    {
        try
        {
            PathResolver.ValidateNewName(name ?? string.Empty);
            result = Results.Empty;
            return true;
        }
        catch (InvalidFileSystemRequestException)
        {
            metrics.UploadRejected();
            result = Problem(
                StatusCodes.Status400BadRequest,
                "invalid_name",
                "Error_InvalidName_Title",
                "Error_InvalidName_Detail",
                httpContext,
                localizer);
            return false;
        }
    }

    private static string ToErrorCode(Exception exception) => exception switch
    {
        FileSystemNameConflictException => FileTaskErrorCodes.NameConflict,
        FileTooLargeException => FileTaskErrorCodes.FileTooLarge,
        InsufficientSpaceException => FileTaskErrorCodes.InsufficientSpace,
        UploadIncompleteException => FileTaskErrorCodes.UploadIncomplete,
        FileSystemAccessDeniedException => FileTaskErrorCodes.OperationAccessDenied,
        _ => FileTaskErrorCodes.IoError,
    };

    private static IResult MapProblem(
        Exception exception,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILoggerFactory loggerFactory)
    {
        var problem = FileSystemProblemMapper.Create(
            exception,
            httpContext,
            localizer,
            loggerFactory.CreateLogger("dotmeer.filebrowser.Upload"),
            operation: "загрузка");
        return FileSystemProblemMapper.ToJsonResult(problem);
    }

    private static IResult Problem(
        int status,
        string code,
        string titleKey,
        string detailKey,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer) =>
        FileSystemProblemMapper.ToJsonResult(
            FileSystemProblemMapper.CreateProblem(status, code, titleKey, detailKey, httpContext, localizer));

    public sealed record UploadResponse(string Root, string Path, long Size);
}
