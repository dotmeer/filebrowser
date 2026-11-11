using System.Diagnostics;
using dotmeer.filebrowser.Components;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Endpoints;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Localization;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Settings;
using dotmeer.filebrowser.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions =
        ActivityTrackingOptions.TraceId
        | ActivityTrackingOptions.SpanId
        | ActivityTrackingOptions.ParentId;
});

using var startupLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    logging.Configure(options =>
    {
        options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId
            | ActivityTrackingOptions.SpanId
            | ActivityTrackingOptions.ParentId;
    });
    logging.AddConsole();
});
var startupLogger = startupLoggerFactory.CreateLogger("dotmeer.filebrowser.Startup");

var configPath = builder.Configuration["ConfigPath"] ?? "/config/config.yaml";
FileBrowserConfiguration fileBrowserConfiguration;
TaskDatabase taskDatabase;
try
{
    fileBrowserConfiguration = FileBrowserConfigurationLoader.Load(configPath);
    var databasePath = builder.Configuration["DatabasePath"]
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "app.db");
    taskDatabase = new TaskDatabase(databasePath);
    taskDatabase.Migrate();
    startupLogger.LogInformation(
        "Приложение инициализировано: сервис={ServiceName}, версия={ServiceVersion}, workers={Workers}, корни={RootNames}",
        FileBrowserTelemetry.ServiceName,
        FileBrowserTelemetry.ServiceVersion,
        fileBrowserConfiguration.Workers,
        string.Join(',', fileBrowserConfiguration.Roots.Select(root => root.Name)));
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "Не удалось загрузить конфигурацию или инициализировать SQLite.");
    throw;
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHealthChecks();
builder.Services.AddLocalization(options => options.ResourcesPath = "Localization");
builder.Services.AddSingleton(new AppSettingsService(taskDatabase));
builder.Services.AddFileBrowserObservability(builder.Configuration);
builder.Services.AddSingleton(fileBrowserConfiguration);
builder.Services.AddSingleton<PathResolver>();
builder.Services.AddSingleton<FileSystemReadService>();
builder.Services.AddSingleton<FileSystemSearchService>();
builder.Services.AddSingleton<SearchMetrics>();
builder.Services.AddSingleton<FileSystemWriteService>();
builder.Services.AddSingleton(taskDatabase);
builder.Services.AddSingleton<TaskNotificationService>();
builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<TaskMetrics>();
builder.Services.AddSingleton<TaskQueue>();
builder.Services.AddSingleton<TaskDispatcher>();
builder.Services.AddSingleton<TemporaryArtifactCleanupService>();
builder.Services.AddSingleton<TaskProgressService>();
builder.Services.AddSingleton<TaskCancellationRegistry>();
builder.Services.AddSingleton<TaskCancellationService>();
builder.Services.AddSingleton<TaskPresentation>();
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
// UI language is a server-side setting (SQLite); the culture applies to every
// request, including re-executed status-code pages.
app.UseRequestLocalization(StoredLanguageProvider.CreateOptions());
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Единственные HTTP-эндпоинты после удаления внешнего API: скачивание
// файла и предпросмотр для UI (SPEC §16) и health-checks для эксплуатации
// (k8s liveness/readiness). Liveness: приложение отвечает. Readiness: пока
// совпадает с liveness; при появлении критичной зависимости (например
// SQLite) сюда добавляется соответствующая проверка.
app.MapDownload();
app.MapPreview();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program { }
