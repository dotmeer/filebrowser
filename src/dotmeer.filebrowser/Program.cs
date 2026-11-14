using System.Diagnostics;
using System.Net;
using dotmeer.filebrowser.Access;
using dotmeer.filebrowser.Archive;
using dotmeer.filebrowser.Auth;
using dotmeer.filebrowser.Components;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Endpoints;
using dotmeer.filebrowser.FileSystem;
using dotmeer.filebrowser.Localization;
using dotmeer.filebrowser.Observability;
using dotmeer.filebrowser.Settings;
using dotmeer.filebrowser.Tasks;
using dotmeer.filebrowser.Uploads;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

// Локальная команда восстановления admin работает без запуска веб-сервера
// и завершается до создания хоста и чтения YAML.
if (AdminPasswordRecovery.IsCommand(args))
{
    return AdminPasswordRecovery.Run(args);
}

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

AuthOptions authOptions;
try
{
    authOptions = AuthModeReader.Read(builder.Configuration);
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "Не удалось определить режим AUTH_MODE.");
    throw;
}

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
        "Приложение инициализировано: сервис={ServiceName}, версия={ServiceVersion}, workers={Workers}, auth={AuthMode}, корни={RootNames}",
        FileBrowserTelemetry.ServiceName,
        FileBrowserTelemetry.ServiceVersion,
        fileBrowserConfiguration.Workers,
        authOptions.Mode,
        string.Join(',', fileBrowserConfiguration.Roots.Select(root => root.Name)));
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "Не удалось загрузить конфигурацию или инициализировать SQLite.");
    throw;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHealthChecks();
builder.Services.AddLocalization(options => options.ResourcesPath = "Localization");
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<UserAdministrationService>();
builder.Services.AddSingleton<LanguageSettingsService>();
builder.Services.AddSingleton<RootPermissionStore>();
builder.Services.AddSingleton<ServicePathProtectionProvider>();
builder.Services.AddSingleton<FileAccessService>();
builder.Services.AddSingleton<TaskAccessService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AccessGuard>();
builder.Services.AddScoped<IAuthorizationHandler, AccessPolicyHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Application, policy => policy.AddRequirements(new ApplicationAccessRequirement()))
    .AddPolicy(AuthPolicies.Authenticated, policy => policy.AddRequirements(new AuthenticatedUserRequirement()));
builder.Services.AddCascadingAuthenticationState();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "filebrowser.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = AuthService.SessionLifetime;
        options.SlidingExpiration = false;
        options.LoginPath = AuthEndpoints.LoginPath;
        options.AccessDeniedPath = "/access-denied";
        options.Events = new CookieAuthenticationEvents
        {
            // SQLite — источник истины: отозванная или удалённая сессия
            // перестаёт приниматься на следующем HTTP-запросе.
            OnValidatePrincipal = async context =>
            {
                var auth = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                var session = await auth.ValidatePrincipalAsync(
                    context.Principal!,
                    context.HttpContext.RequestAborted);
                if (session is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            },
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddSingleton(new AppSettingsService(taskDatabase));
builder.Services.AddFileBrowserObservability(builder.Configuration);
builder.Services.AddSingleton(fileBrowserConfiguration);
builder.Services.AddSingleton(new ApplicationPaths(
    Path.GetFullPath(configPath),
    taskDatabase.DatabasePath));
builder.Services.AddSingleton<PathResolver>();
builder.Services.AddSingleton<FileSystemReadService>();
builder.Services.AddSingleton<FileSystemSearchService>();
builder.Services.AddSingleton<SearchMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ArchiveTicketStore>();
builder.Services.AddSingleton<FileSystemArchiveService>();
builder.Services.AddSingleton<ArchiveMetrics>();
builder.Services.AddSingleton<FileSystemWriteService>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<UploadMetrics>();
builder.Services.AddSingleton(taskDatabase);
builder.Services.AddSingleton<TaskNotificationService>();
builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<TaskMetrics>();
builder.Services.AddSingleton<TaskQueue>();
builder.Services.AddSingleton<TaskDispatcher>();
builder.Services.AddSingleton<TemporaryArtifactCleanupService>();
builder.Services.AddSingleton<TaskProgressService>();
builder.Services.AddSingleton<TaskCancellationRegistry>();
builder.Services.AddSingleton<TaskOperationGate>();
builder.Services.AddSingleton<TaskCancellationService>();
builder.Services.AddSingleton<TaskPresentation>();
builder.Services.AddSingleton<FileTransferEngine>();
builder.Services.AddSingleton<FileDeleteEngine>();
builder.Services.AddSingleton<CopyTaskExecutor>();
builder.Services.AddSingleton<MoveTaskExecutor>();
builder.Services.AddSingleton<DeleteTaskExecutor>();
builder.Services.AddSingleton<IFileTaskExecutor, FileTaskExecutor>();
builder.Services.AddHostedService<TaskExecutorHostedService>();

var app = builder.Build();

var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (forwardedHeadersEnabled)
{
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy.Trim(), out var address))
        {
            forwardedHeaders.KnownProxies.Add(address);
        }
    }

    app.UseForwardedHeaders(forwardedHeaders);
}

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

app.UseAuthentication();
app.UseMiddleware<PageAccessMiddleware>();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// HTTP-эндпоинты UI: скачивание, предпросмотр, архив, загрузка и health-checks.
// Readiness совпадает с liveness: SQLite проверяется при запуске и при
// недоступности останавливает запуск; автовосстановления нет.
app.MapDownload();
app.MapPreview();
app.MapArchive();
app.MapUpload();
app.MapAuthentication();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();

return 0;

public partial class Program { }
