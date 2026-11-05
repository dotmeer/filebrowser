using dotmeer.filebrowser.Components;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Endpoints;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Tasks;

var builder = WebApplication.CreateBuilder(args);

var configPath = builder.Configuration["ConfigPath"] ?? "/config/config.yaml";
var fileBrowserConfiguration = FileBrowserConfigurationLoader.Load(configPath);
var databasePath = builder.Configuration["DatabasePath"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "app.db");
var taskDatabase = new TaskDatabase(databasePath);
taskDatabase.Migrate();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddSingleton(fileBrowserConfiguration);
builder.Services.AddSingleton<PathResolver>();
builder.Services.AddSingleton<FileSystemReadService>();
builder.Services.AddSingleton<FileSystemWriteService>();
builder.Services.AddSingleton(taskDatabase);
builder.Services.AddSingleton<TaskNotificationService>();
builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<TaskQueue>();
builder.Services.AddSingleton<TaskDispatcher>();
builder.Services.AddSingleton<TemporaryArtifactCleanupService>();
builder.Services.AddSingleton<TaskProgressService>();
builder.Services.AddSingleton<FileTransferEngine>();
builder.Services.AddSingleton<CopyTaskExecutor>();
builder.Services.AddSingleton<MoveTaskExecutor>();
builder.Services.AddSingleton<IFileTaskExecutor, FileTaskExecutor>();
builder.Services.AddHostedService<TaskExecutorHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// Must be inside the generic exception handler so API errors keep the ProblemDetails contract.
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthz();
app.MapFileSystem();
app.MapTasks();

app.Run();

public partial class Program { }
