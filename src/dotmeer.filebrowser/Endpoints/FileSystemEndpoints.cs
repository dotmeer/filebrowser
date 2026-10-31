using dotmeer.filebrowser.FileSystem;

namespace dotmeer.filebrowser.Endpoints;

public static class FileSystemEndpoints
{
    public static void MapFileSystemRead(this WebApplication app)
    {
        app.MapGet("/api/fs/roots", (FileSystemReadService reader) => Results.Ok(reader.GetRoots()));
        app.MapGet("/api/fs/list", (string? root, string? path, FileSystemReadService reader) =>
            Results.Ok(reader.List(new FileRef(root ?? string.Empty, path ?? string.Empty))));
    }
}
