using dotmeer.filebrowser.Components;
using dotmeer.filebrowser.Configuration;
using dotmeer.filebrowser.Endpoints;
using dotmeer.filebrowser.FileSystem;

var builder = WebApplication.CreateBuilder(args);

var configPath = builder.Configuration["ConfigPath"] ?? "/config/config.yaml";
var fileBrowserConfiguration = FileBrowserConfigurationLoader.Load(configPath);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddSingleton(fileBrowserConfiguration);
builder.Services.AddSingleton<PathResolver>();
builder.Services.AddSingleton<FileSystemReadService>();
builder.Services.AddSingleton<FileSystemWriteService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseMiddleware<ApiExceptionMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthz();
app.MapFileSystem();

app.Run();

public partial class Program { }
