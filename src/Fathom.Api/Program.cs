using Fathom.Api;
using Fathom.Api.Endpoints;
using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer;
using Fathom.SqlServer.Diagnostics;
using Fathom.Writers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<FathomOptions>(builder.Configuration.GetSection(FathomOptions.SectionName));
    builder.Services.PostConfigure<FathomOptions>(options =>
        options.ConnectionString ??= builder.Configuration.GetConnectionString("Fathom"));

    // Fail fast: a misconfigured host should refuse to start, not boot green and error-loop.
    builder.Services.AddOptions<FathomOptions>()
        .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
            "No database configured. Set Fathom:ConnectionString or ConnectionStrings:Fathom.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.DefinitionDirectory), "Fathom:DefinitionDirectory must be set.")
        .Validate(o => o.ExportTimeout > TimeSpan.Zero, "Fathom:ExportTimeout must be positive.")
        .ValidateOnStart();

    var fathom = builder.Services.AddFathom().UseSqlServer();

    // Configuration-defined code lists (Fathom:CodeListLookups): wire an existing
    // TblCode/TblCodeType-shaped reference table into both output rendering and request-filter
    // resolution without writing code. A custom IExportLookupProvider/IRequestLookupProvider
    // is always available too — AddExportLookup<T>()/AddRequestLookup<T>() on `fathom`.
    foreach (var codeList in builder.Configuration.GetSection("Fathom:CodeListLookups").Get<CodeListLookupOptions[]>() ?? [])
    {
        fathom.AddCodeListLookup(codeList);
    }

    builder.Services.AddSingleton<IExportWriter, JsonExportWriter>();
    builder.Services.AddSingleton<IExportWriter, XmlExportWriter>();
    builder.Services.AddSingleton<IExportWriter, CsvExportWriter>();

    builder.Services.AddSingleton(provider => new ExportDefinitionDirectoryStore(
        provider.GetRequiredService<IOptions<FathomOptions>>().Value.DefinitionDirectory,
        provider.GetRequiredService<ILogger<ExportDefinitionDirectoryStore>>()));
    builder.Services.AddHostedService<FathomInitializer>();

    // A wildcard tenant accepts tokens from ANY Microsoft tenant — for a single-tenant data
    // export API that is almost never intended and quietly widens the trust boundary, so warn
    // loudly at startup. Set AzureAd:TenantId to your specific tenant GUID.
    var configuredTenant = builder.Configuration["AzureAd:TenantId"];
    if (configuredTenant is "common" or "organizations" or "consumers")
    {
        Log.Warning(
            "AzureAd:TenantId is '{TenantId}', which accepts tokens from any Microsoft tenant. "
            + "Set it to your specific tenant GUID unless multi-tenant access is intended.",
            configuredTenant);
    }

    // Azure Entra ID today — swap AddMicrosoftIdentityWebApi for any other ASP.NET Core
    // authentication scheme to use a different identity provider instead. Nothing past this
    // call (the endpoints, the query engine, the writers) has any Entra-specific code in it;
    // they only ever see a standard authenticated ClaimsPrincipal via RequireAuthorization().
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
    builder.Services.AddAuthorization();

    // Cached DB readiness probe needs a clock; TimeProvider isn't registered by default.
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

    // Exports are usually network-bound, so opt-in (Accept-Encoding) compression is one of
    // the biggest real-world speedups available. Zip output is deliberately absent from the
    // list — it is already compressed. Enabled over HTTPS too: BREACH-style attacks need a
    // secret reflected next to attacker-controlled content, which a data export is not.
    builder.Services.AddResponseCompression(compression =>
    {
        compression.EnableForHttps = true;
        compression.MimeTypes = ["application/json", "application/xml", "text/csv"];
    });

    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    });

    var otel = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("fathom"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(FathomDiagnostics.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(FathomDiagnostics.MeterName));

    // Any OTLP-compatible backend (Azure Monitor, Grafana, Jaeger, ...) is one env var away:
    // set OTEL_EXPORTER_OTLP_ENDPOINT and both signals start flowing.
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        otel.WithTracing(tracing => tracing.AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddOtlpExporter());
    }

    var app = builder.Build();

    // Unhandled exceptions become RFC 7807 problem responses instead of bare 500s.
    app.UseExceptionHandler();
    app.UseResponseCompression();
    app.UseSerilogRequestLogging();

    // Every response serves data or docs, never markup to be sniffed into something else.
    app.Use(async (context, next) =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await next();
    });

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapOpenApi();
    app.MapScalarApiReference(options => options.Title = "Fathom API");

    app.MapExportDefinitionEndpoints();
    app.MapExportRunEndpoints();

    app.MapHealthChecks("/health");

    // Probe split: liveness answers "is the process up" and must not depend on the database;
    // readiness gates traffic on the export database actually being reachable.
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fathom failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
