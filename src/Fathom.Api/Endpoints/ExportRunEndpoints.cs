using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer;

namespace Fathom.Api.Endpoints;

/// <summary>Running a registered export and streaming the result straight to the response.</summary>
public static class ExportRunEndpoints
{
    /// <summary>Query keys with meaning of their own, excluded from filter binding.</summary>
    private const string FormatKey = "format";

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

                // Strict binding: an unrecognized query key is a 400, never silently ignored.
                // For a data-export API, a typo'd filter name that silently exports the whole
                // unfiltered table is a data-governance incident, not a convenience.
                var bound = RequestFilterBinder.Bind(
                    definition,
                    request.Query.Select(kv => KeyValuePair.Create(kv.Key, (IReadOnlyList<string>)[.. kv.Value.OfType<string>()])),
                    FormatKey);
                if (bound.UnknownKeys.Count > 0)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown query parameter(s): {string.Join(", ", bound.UnknownKeys)}. "
                            + $"This export supports: {string.Join(", ", definition.Filters.Select(f => f.Name))}; plus '{FormatKey}'.",
                    });
                }

                IAsyncEnumerable<ExportRow> rows;
                try
                {
                    // Filter resolution and staging happen eagerly here — before anything is
                    // written to the response — so a bad filter value surfaces as a clean 400
                    // instead of an aborted mid-stream response.
                    rows = await engine.RunAsync(definition, bound.Filters, cancellationToken);
                }
                catch (FilterValidationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                response.ContentType = writer.GetContentType(definition);
                response.Headers.ContentDisposition = $"attachment; filename=\"{name}.{FileExtension(writer, definition)}\"";
                await writer.WriteAsync(response.Body, definition, rows, cancellationToken);
                return Results.Empty;
            })
            .WithName("RunExport")
            .Produces(StatusCodes.Status200OK, responseType: null, "application/json", "application/xml", "text/csv", "application/zip")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static string FileExtension(IExportWriter writer, ExportDefinition definition) =>
        writer.Format switch
        {
            "csv" => definition.Root.Children.Count > 0 ? "zip" : "csv",
            _ => writer.Format,
        };
}
