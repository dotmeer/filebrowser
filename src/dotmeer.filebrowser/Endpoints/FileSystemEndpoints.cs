using System.Globalization;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.FileSystem.Search;
using Microsoft.AspNetCore.Antiforgery;

namespace dotmeer.filebrowser.Endpoints;

public static class FileSystemEndpoints
{
    public static void MapFileSystem(this WebApplication app)
    {
        app.MapGet("/api/fs/roots", (FileSystemReadService reader) => Results.Ok(reader.GetRoots()));
        app.MapGet("/api/fs/list", (string? root, string? path, FileSystemReadService reader) =>
            Results.Ok(reader.List(new FileRef(root ?? string.Empty, path ?? string.Empty))));
        app.MapGet("/api/fs/download", (string? root, string? path, FileSystemReadService reader) =>
        {
            var file = reader.OpenFile(new FileRef(root ?? string.Empty, path ?? string.Empty));
            return Results.File(
                file.Stream,
                contentType: "application/octet-stream",
                fileDownloadName: file.Name,
                enableRangeProcessing: true);
        });
        app.MapGet("/api/fs/search", (
                string? root,
                string? path,
                string? q,
                string? ext,
                string? limit,
                FileSystemSearchService searchService,
                HttpContext httpContext) =>
            {
                var query = q ?? string.Empty;
                if (string.IsNullOrWhiteSpace(query))
                {
                    throw new InvalidFileSystemRequestException("Поисковый запрос не может быть пустым.");
                }

                var resultLimit = ParseSearchLimit(limit);
                var extGlobs = ParseExtGlobs(ext);
                var result = searchService.Search(
                    new FileRef(root ?? string.Empty, path ?? string.Empty),
                    query,
                    ext,
                    extGlobs,
                    resultLimit,
                    httpContext.RequestAborted);
                return Results.Ok(result);
            });

        app.MapPost("/api/fs/mkdir", (MkdirRequest request, FileSystemWriteService writer) =>
                Results.Ok(writer.CreateDirectory(request.Parent, request.Name)))
            .RequireCsrfValidation();
        app.MapPost("/api/fs/rename", (RenameRequest request, FileSystemWriteService writer) =>
                Results.Ok(writer.Rename(request.Source, request.NewName)))
            .RequireCsrfValidation();
        app.MapPost("/api/fs/delete", (DeleteRequest request, FileSystemWriteService writer) =>
            {
                writer.Delete(request.Source);
                return Results.NoContent();
            })
            .RequireCsrfValidation();
    }

    private static int ParseSearchLimit(string? limit)
    {
        if (string.IsNullOrEmpty(limit))
        {
            return FileSystemSearchService.DefaultResultLimit;
        }

        if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1
            || value > FileSystemSearchService.MaxResultLimit)
        {
            throw new InvalidFileSystemRequestException("Параметр limit должен быть целым числом от 1 до 500.");
        }

        return value;
    }

    private static IReadOnlyList<GlobMatcher>? ParseExtGlobs(string? ext)
    {
        if (ext is null)
        {
            return null;
        }

        var globs = new List<GlobMatcher>();
        foreach (var segment in ext.Split(',', StringSplitOptions.None))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            globs.Add(GlobMatcher.Parse(segment));
        }

        if (globs.Count == 0)
        {
            throw new InvalidFileSystemRequestException("Параметр ext не содержит ни одного шаблона.");
        }

        return globs;
    }

    private static RouteHandlerBuilder RequireCsrfValidation(this RouteHandlerBuilder endpoint) =>
        endpoint
            .WithMetadata(new RequireAntiforgeryTokenAttribute(required: true))
            .AddEndpointFilter(async (context, next) =>
            {
                var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(context.HttpContext);
                }
                catch (Exception exception)
                {
                    throw new AntiforgeryRequestRejectedException(exception);
                }
                return await next(context);
            });
}

public sealed record MkdirRequest(FileRef Parent, string Name);

public sealed record RenameRequest(FileRef Source, string NewName);

public sealed record DeleteRequest(FileRef Source);

public sealed class AntiforgeryRequestRejectedException(Exception innerException)
    : Exception("Antiforgery validation failed.", innerException);
