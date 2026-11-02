using dotmeer.filebrowser.FileSystem;
using Microsoft.AspNetCore.Antiforgery;

namespace dotmeer.filebrowser.Endpoints;

public static class FileSystemEndpoints
{
    public static void MapFileSystem(this WebApplication app)
    {
        app.MapGet("/api/fs/roots", (FileSystemReadService reader) => Results.Ok(reader.GetRoots()));
        app.MapGet("/api/fs/list", (string? root, string? path, FileSystemReadService reader) =>
            Results.Ok(reader.List(new FileRef(root ?? string.Empty, path ?? string.Empty))));

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
