using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer;

namespace Fathom.Api.Endpoints;

/// <summary>Running a registered export and streaming the result straight to the response.</summary>
public static class ExportRunEndpoints
{
    public static void MapExportRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exports").WithTags("Exports").RequireAuthorization();

        group.MapGet("/{name}/run", async (
                string name,
                string? format,
                HttpRequest request,
                HttpResponse response,
                IExportDefinitionRegistry registry,
                ExportQueryEngine engine,
                IEnumerable<IExportWriter> writers,
                CancellationToken cancellationToken) =>
            {
                if (registry.Find(name) is not { } definition)
                {
                    return Results.NotFound(new { error = $"Export '{name}' is not registered." });
                }

                var resolvedFormat = format ?? "json";
                var writer = writers.FirstOrDefault(w => string.Equals(w.Format, resolvedFormat, StringComparison.OrdinalIgnoreCase));
                if (writer is null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown format '{resolvedFormat}'. Supported: {string.Join(", ", writers.Select(w => w.Format))}.",
                    });
                }

                IAsyncEnumerable<ExportRow> rows;
                try
                {
                    // Filter resolution and staging happen eagerly here — before anything is
                    // written to the response — so a bad filter value surfaces as a clean 400
                    // instead of an aborted mid-stream response.
                    rows = await engine.RunAsync(definition, ExtractFilters(definition, request.Query), cancellationToken);
                }
                catch (FilterValidationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                response.ContentType = writer.GetContentType(definition);
                response.Headers["Content-Disposition"] = $"attachment; filename=\"{name}.{FileExtension(writer, definition)}\"";
                await writer.WriteAsync(response.Body, definition, rows, cancellationToken);
                return Results.Empty;
            })
            .WithName("RunExport")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Only filters actually present on the query string are passed on — absence vs. requiredness is FilterResolver's call, not the endpoint's.</summary>
    private static List<FilterValue> ExtractFilters(ExportDefinition definition, IQueryCollection query)
    {
        var filters = new List<FilterValue>();
        foreach (var filterDef in definition.Filters)
        {
            if (query.TryGetValue(filterDef.Name, out var values))
            {
                filters.Add(new FilterValue(filterDef.Name, [.. values.OfType<string>()]));
            }
        }

        return filters;
    }

    private static string FileExtension(IExportWriter writer, ExportDefinition definition) =>
        writer.Format switch
        {
            "csv" => definition.Root.Children.Count > 0 ? "zip" : "csv",
            _ => writer.Format,
        };
}
