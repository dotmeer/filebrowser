using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Observability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Потоковая отдача ZIP-архива папки или набора объектов (SPEC §12.2).
/// Выбор адресуется тикетом, созданным Blazor-компонентом после
/// предпроверки; тикет не является разрешением — корень, пути и типы
/// объектов проверяются заново общим resolver/walker.
/// </summary>
/// <remarks>
/// Ответ не имеет Content-Length и не поддерживает Range: архив
/// генерируется на лету. Ошибка до отправки заголовков возвращается
/// в общем ProblemDetails-контракте (§17), ошибка после начала ответа
/// (объект исчез, нет прав, отмена) обрывает соединение — частичный
/// архив не выдаётся за успешный.
/// </remarks>
public static class ArchiveEndpoint
{
    public static void MapArchive(this WebApplication app)
    {
        app.MapGet("/api/fs/zip", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        string? ticket,
        ArchiveTicketStore tickets,
        FileSystemArchiveService archiveService,
        ArchiveMetrics metrics,
        HttpContext httpContext,
        IStringLocalizer<SharedResources> localizer,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("dotmeer.filebrowser.Archive");
        if (string.IsNullOrEmpty(ticket))
        {
            metrics.ArchiveRejected();
            return Problem(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Error_InvalidRequest_Title",
                "Error_InvalidRequest_Detail",
                httpContext,
                localizer);
        }

        if (!tickets.TryRedeem(ticket, out var selection))
        {
            metrics.ArchiveRejected();
            return Problem(
                StatusCodes.Status410Gone,
                "ticket_expired",
                "Error_TicketExpired_Title",
                "Error_TicketExpired_Detail",
                httpContext,
                localizer);
        }

        using var slot = archiveService.TryAcquireSlot();
        if (slot is null)
        {
            metrics.ArchiveRejected();
            return Problem(
                StatusCodes.Status429TooManyRequests,
                "archive_busy",
                "Error_ArchiveBusy_Title",
                "Error_ArchiveBusy_Detail",
                httpContext,
                localizer);
        }

        ArchivePlan plan;
        try
        {
            plan = await Task.Run(
                () => archiveService.Plan(selection.ToFileRef(), selection.Names, httpContext.RequestAborted),
                httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            metrics.ArchiveAborted();
            return Results.Empty;
        }
        catch (Exception exception) when (!httpContext.Response.HasStarted)
        {
            metrics.ArchiveRejected();
            var problem = FileSystemProblemMapper.Create(
                exception,
                httpContext,
                localizer,
                logger,
                operation: "скачивание архивом");
            return FileSystemProblemMapper.ToJsonResult(problem);
        }

        SetResponseHeaders(httpContext, plan.ArchiveName);

        // ZipArchive пишет в поток синхронно (включая data descriptors и
        // central directory), а Kestrel по умолчанию запрещает
        // синхронный ввод-вывод. Разрешаем его только для этого запроса:
        // число одновременно формируемых архивов ограничено слотом
        // (SPEC §12.2), поэтому блокировка потока ограничена.
        var bodyControl = httpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        try
        {
            await archiveService.WriteAsync(plan, httpContext.Response.Body, httpContext.RequestAborted);
            metrics.ArchiveCompleted(plan.TotalBytes);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // Клиент прервал скачивание: ответ больше не нужен.
            metrics.ArchiveAborted();
        }
        catch (Exception exception)
        {
            // Заголовки уже отправлены: статус изменить нельзя, поэтому
            // соединение обрывается, а недокачанный архив не выдаётся
            // за успешный (SPEC §12.2).
            metrics.ArchiveAborted();
            logger.LogWarning(
                "Скачивание архивом прервано после начала ответа: {ExceptionType}",
                exception.GetType().Name);
            httpContext.Abort();
        }

        return Results.Empty;
    }

    private static void SetResponseHeaders(HttpContext httpContext, string archiveName)
    {
        httpContext.Response.ContentType = "application/zip";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(archiveName);
        httpContext.Response.Headers.ContentDisposition = disposition.ToString();
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
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
}
