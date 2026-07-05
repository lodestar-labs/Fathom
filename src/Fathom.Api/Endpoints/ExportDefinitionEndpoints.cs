using Fathom.Core;

namespace Fathom.Api.Endpoints;

/// <summary>Registering, inspecting, and removing export definitions.</summary>
public static class ExportDefinitionEndpoints
{
    public static void MapExportDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exports").WithTags("Exports").RequireAuthorization();

        group.MapGet("/", (IExportDefinitionRegistry registry) =>
                Results.Ok(registry.All.Select(d => new
                {
                    d.Name,
                    d.Version,
                    d.Description,
                    Entities = d.EnumerateEntities().Select(e => e.Name).ToArray(),
                    Filters = d.Filters.Select(f => new { f.Name, f.Entity, f.Field, f.Operator, f.Required }).ToArray(),
                })))
            .WithName("ListExports");

        group.MapGet("/{name}", (string name, IExportDefinitionRegistry registry) =>
                registry.Find(name) is { } definition
                    ? Results.Text(ExportDefinitionSerializer.Serialize(definition), "application/json")
                    : Results.NotFound())
            .WithName("GetExportDefinition");

        group.MapPut("/{name}", async (
                string name,
                HttpRequest request,
                IExportDefinitionRegistry registry,
                ExportDefinitionDirectoryStore store,
                CancellationToken cancellationToken) =>
            {
                ExportDefinition definition;
                try
                {
                    definition = await ExportDefinitionSerializer.DeserializeAsync(request.Body, cancellationToken);
                }
                catch (ExportDefinitionException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                if (!string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = $"Export name '{definition.Name}' does not match the route ('{name}')." });
                }

                try
                {
                    // Validate before touching disk: Register() runs ExportDefinition.Validate()
                    // and throws on the same structural errors DeserializeAsync can't catch.
                    var errors = definition.Validate();
                    if (errors.Count > 0)
                    {
                        return Results.BadRequest(new { errors });
                    }

                    await store.SaveAsync(definition, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.Problem($"The export definition could not be persisted: {ex.Message}", statusCode: 500);
                }

                registry.Register(definition);
                return Results.Ok(new { definition.Name, entities = definition.EnumerateEntities().Count() });
            })
            .WithName("PutExportDefinition");

        group.MapDelete("/{name}", async (
                string name,
                IExportDefinitionRegistry registry,
                ExportDefinitionDirectoryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!registry.Remove(name))
                {
                    return Results.NotFound();
                }

                await store.DeleteAsync(name, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteExportDefinition");
    }
}
