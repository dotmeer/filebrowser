using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace dotmeer.filebrowser.Observability;

public static class FileBrowserTelemetry
{
    public const string ServiceName = "dotmeer.filebrowser";

    public static readonly string ServiceVersion = GetServiceVersion();

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    private static string GetServiceVersion()
    {
        var informationalVersion = typeof(FileBrowserTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var assemblyVersion = typeof(FileBrowserTelemetry).Assembly.GetName().Version?.ToString(3);
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "0.0.0" : assemblyVersion;
    }
}

public static class FileBrowserObservabilityExtensions
{
    public static IServiceCollection AddFileBrowserObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.Equals(configuration["OTEL_SDK_DISABLED"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                FileBrowserTelemetry.ServiceName,
                serviceVersion: FileBrowserTelemetry.ServiceVersion));

        openTelemetry.WithTracing(tracing => tracing
            .AddSource(FileBrowserTelemetry.ActivitySource.Name)
            .AddAspNetCoreInstrumentation(options => options.RecordException = true)
            .AddHttpClientInstrumentation());

        openTelemetry.WithMetrics(metrics => metrics
            .AddMeter(FileBrowserTelemetry.Meter.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation());

        if (!IsOtlpConfigured(configuration))
        {
            return services;
        }

        openTelemetry.WithTracing(tracing => tracing.AddOtlpExporter());
        openTelemetry.WithMetrics(metrics => metrics.AddOtlpExporter());
        return services;
    }

    private static bool IsOtlpConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
        || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"])
        || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
}
