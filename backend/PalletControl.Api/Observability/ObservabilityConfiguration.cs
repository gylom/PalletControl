using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

public sealed record ObservabilityRuntimeOptions(bool OtlpEnabled, string? OtlpEndpoint, string ServiceName, string ServiceVersion);

public static class ObservabilityConfiguration
{
    public static ObservabilityRuntimeOptions AddPalletObservability(this WebApplicationBuilder builder, string version)
    {
        var endpointText = builder.Configuration["OpenTelemetry:OtlpEndpoint"]?.Trim();
        var hasEndpoint = Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint);
        var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "PalletControl.Api";
        var runtime = new ObservabilityRuntimeOptions(hasEndpoint, hasEndpoint ? endpoint!.ToString() : null, serviceName, version);
        builder.Services.AddSingleton(runtime);

        builder.Services.AddSingleton<SystemTelemetryService>();
        builder.Services.AddHostedService<SystemTelemetryService>(sp => sp.GetRequiredService<SystemTelemetryService>());

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: version))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/api/health");
                    })
                    .AddHttpClientInstrumentation();
                if (hasEndpoint)
                    tracing.AddOtlpExporter(options => options.Endpoint = endpoint!);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (hasEndpoint)
                    metrics.AddOtlpExporter(options => options.Endpoint = endpoint!);
            });

        if (hasEndpoint)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.ParseStateValues = true;
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: version));
                logging.AddOtlpExporter(options => options.Endpoint = endpoint!);
            });
        }

        return runtime;
    }
}
