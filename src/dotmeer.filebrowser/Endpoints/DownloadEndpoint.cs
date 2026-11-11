using dotmeer.filebrowser.FileSystem;
using Microsoft.Extensions.Localization;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Потоковая отдача файла для кнопки «Скачать» в UI. Один из двух
/// публичных файловых эндпоинтов (второй — предпросмотр, SPEC §16):
/// остальной HTTP API удалён (решение D-серии «Удаление внешнего
/// HTTP API»), изменяющие операции доступны только через Blazor, который
/// вызывает прикладные сервисы напрямую.
/// </summary>
/// <remarks>
/// Ошибки маппятся в общий ProblemDetails-контракт
/// (<see cref="FileSystemProblemMapper"/>): application/problem+json,
/// стабильные поля code и traceId, локализованные title/detail,
/// без физических путей.
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
                    var problem = FileSystemProblemMapper.Create(
                        exception,
                        httpContext,
                        localizer,
                        loggerFactory.CreateLogger("dotmeer.filebrowser.Download"),
                        operation: "скачивание");
                    return FileSystemProblemMapper.ToJsonResult(problem);
                }
            });
    }
}
