using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

internal static class TestHostFactory
{
    /// <summary>
    /// Поднимает хост приложения с изоляцией конфигурации на уровне хоста:
    /// ConfigPath/DatabasePath передаются через UseSetting WebApplicationFactory
    /// (host configuration), а не через процессные env-переменные — безопасно при
    /// любом расписании xUnit. databasePath=null оставляет вывод пути БД приложению
    /// (app.db рядом с config.yaml).
    /// </summary>
    public static TestHost Create(
        string configPath,
        string? databasePath = null,
        string? environment = null,
        bool useKestrel = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var keyDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "data-protection-keys");
        Directory.CreateDirectory(keyDirectory);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder.UseSetting("ConfigPath", configPath);
            if (databasePath is not null)
            {
                builder.UseSetting("DatabasePath", databasePath);
            }

            builder.UseSetting("Logging__EventLog__LogLevel__Default", "None");

            builder.ConfigureServices(services =>
            {
                services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
                configureServices?.Invoke(services);
            });
        });
        try
        {
            if (useKestrel)
            {
                factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            }

            return new TestHost(factory, factory.CreateClient());
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    internal sealed class TestHost(WebApplicationFactory<Program> factory, HttpClient client) : IDisposable
    {
        public IServiceProvider Services => factory.Services;

        public Uri Address => new(factory.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }
    }
}
