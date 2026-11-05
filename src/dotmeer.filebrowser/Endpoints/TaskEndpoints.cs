using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.FileSystem;
using Microsoft.AspNetCore.Antiforgery;

namespace dotmeer.filebrowser.Endpoints;

public static class TaskEndpoints
{
    public static void MapTasks(this WebApplication app)
    {
        app.MapGet("/api/tasks", async (int? offset, int? limit, TaskRepository repository, CancellationToken cancellationToken) =>
        {
            var page = await repository.GetPageAsync(offset ?? 0, limit ?? 50, cancellationToken);
            return Results.Ok(page);
        });
        app.MapGet("/api/tasks/{id:guid}", async (Guid id, TaskRepository repository, CancellationToken cancellationToken) =>
        {
            var task = await repository.GetAsync(id, cancellationToken)
                ?? throw new FileTaskNotFoundException();
            return Results.Ok(task);
        });
        app.MapPost("/api/tasks/copy", async (FileTaskRequest request, TaskDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var task = await dispatcher.EnqueueAsync(FileTaskType.Copy, request.Sources, request.Destination, cancellationToken);
                return Results.Accepted($"/api/tasks/{task.Id:D}", new TaskAcceptedResponse(task.Id));
            })
            .RequireCsrfValidation();
        app.MapPost("/api/tasks/move", async (FileTaskRequest request, TaskDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var task = await dispatcher.EnqueueAsync(FileTaskType.Move, request.Sources, request.Destination, cancellationToken);
                return Results.Accepted($"/api/tasks/{task.Id:D}", new TaskAcceptedResponse(task.Id));
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

public sealed record FileTaskRequest(IReadOnlyList<FileRef> Sources, FileRef Destination);

public sealed record TaskAcceptedResponse(Guid Id);
