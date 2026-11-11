using dotmeer.filebrowser.FileSystem;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace dotmeer.filebrowser.Endpoints;

/// <summary>
/// Эндпоинт предпросмотра файла для модального просмотрщика UI
/// (SPEC §16 «Предпросмотр»). Близнец эндпоинта скачивания: тот же вход
/// root + относительный path, тот же resolver/validator через
/// <see cref="FileSystemReadService.OpenFile"/>, тот же ProblemDetails-контракт
/// через <see cref="FileSystemProblemMapper"/>. Отличия: MIME-тип и вид
/// ответа определяются серверным whitelist расширений
/// (<see cref="PreviewCatalog"/>), Content-Disposition — inline, текст
/// усечён потолком <see cref="PreviewCatalog.TextPreviewLimitBytes"/>.
/// </summary>
public static class PreviewEndpoint
{
    public static void MapPreview(this WebApplication app)
    {
        app.MapGet("/api/fs/preview", async (
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
                    var definition = PreviewCatalog.Find(file.Name);
                    if (definition is null)
                    {
                        file.Stream.Dispose();
                        return FileSystemProblemMapper.ToJsonResult(
                            FileSystemProblemMapper.CreateProblem(
                                StatusCodes.Status415UnsupportedMediaType,
                                "preview_not_supported",
                                "Error_PreviewNotSupported_Title",
                                "Error_PreviewNotSupported_Detail",
                                httpContext,
                                localizer));
                    }

                    SetInlineDisposition(httpContext, file.Name);

                    if (definition.Kind == PreviewKind.Text)
                    {
                        var (body, truncated) = await ReadCappedAsync(file.Stream, PreviewCatalog.TextPreviewLimitBytes);
                        file.Stream.Dispose();
                        if (truncated)
                        {
                            httpContext.Response.Headers["X-Preview-Truncated"] = "true";
                        }

                        return Results.Bytes(body, definition.ContentType);
                    }

                    // Изображения — поток целиком; PDF — с поддержкой Range
                    // (браузер запрашивает фрагменты для постраничного рендеринга).
                    return Results.File(
                        file.Stream,
                        contentType: definition.ContentType,
                        enableRangeProcessing: definition.Kind == PreviewKind.Pdf);
                }
                catch (Exception exception) when (!httpContext.Response.HasStarted)
                {
                    var problem = FileSystemProblemMapper.Create(
                        exception,
                        httpContext,
                        localizer,
                        loggerFactory.CreateLogger("dotmeer.filebrowser.Preview"),
                        operation: "предпросмотр");
                    return FileSystemProblemMapper.ToJsonResult(problem);
                }
            });
    }

    /// <summary>
    /// Content-Disposition: inline с корректным (в том числе не-ASCII)
    /// именем файла. Формируется вручную: <see cref="Results.File"/> с
    /// fileDownloadName отдаёт attachment, что для предпросмотра неверно.
    /// </summary>
    private static void SetInlineDisposition(HttpContext httpContext, string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);
        httpContext.Response.Headers.ContentDisposition = disposition.ToString();
    }

    /// <summary>
    /// Читает из потока не более <paramref name="limit"/> байт, не буферизуя
    /// файл целиком. Флаг усечения — true, если за пределами потолка
    /// остались данные.
    /// </summary>
    private static async Task<(byte[] Body, bool Truncated)> ReadCappedAsync(FileStream stream, long limit)
    {
        var buffer = new byte[limit];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var truncated = false;
        if (totalRead == buffer.Length)
        {
            truncated = stream.ReadByte() != -1;
        }

        if (totalRead < buffer.Length)
        {
            Array.Resize(ref buffer, totalRead);
        }

        return (buffer, truncated);
    }
}
