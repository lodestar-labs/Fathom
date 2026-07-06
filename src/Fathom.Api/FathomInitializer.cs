using Fathom.Core;

namespace Fathom.Api;

/// <summary>Loads persisted export definitions into the registry at startup.</summary>
public sealed class FathomInitializer(
    IExportDefinitionRegistry registry,
    ExportDefinitionDirectoryStore store,
    ILogger<FathomInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in await store.LoadAllAsync(cancellationToken))
        {
            try
            {
                registry.Register(definition);
            }
            catch (ExportDefinitionException ex)
            {
                // One structurally invalid definition file must not take the host down with it —
                // skip it, exactly as the store already skips files that fail to deserialize.
                logger.LogError(ex, "Skipping invalid export definition '{Name}': {Message}", definition.Name, ex.Message);
            }
        }

        logger.LogInformation(
            "Fathom ready: {Count} export definitions registered ({Names})",
            registry.All.Count,
            string.Join(", ", registry.All.Select(d => d.Name)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
