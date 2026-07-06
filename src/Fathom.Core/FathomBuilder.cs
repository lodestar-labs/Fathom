using Fathom.Core.Lookups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fathom.Core;

/// <summary>Fluent registration surface: <c>services.AddFathom().UseSqlServer().AddCodeListLookup(...)</c>.</summary>
public sealed class FathomBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
}

public static class FathomServiceCollectionExtensions
{
    public static FathomBuilder AddFathom(this IServiceCollection services)
    {
        services.AddSingleton<IExportDefinitionRegistry, ExportDefinitionRegistry>();
        return new FathomBuilder(services);
    }

    /// <summary>Registers a custom output-side lookup provider (numeric/coded value -&gt; output string).</summary>
    public static FathomBuilder AddExportLookup<T>(this FathomBuilder builder) where T : class, IExportLookupProvider
    {
        // Register the concrete type once and forward the interface to it, so a provider added
        // for both directions (AddExportLookup<T>() + AddRequestLookup<T>()) is a single shared
        // instance — one cache, one warmup — not two independent singletons.
        builder.Services.TryAddSingleton<T>();
        builder.Services.AddSingleton<IExportLookupProvider>(sp => sp.GetRequiredService<T>());
        return builder;
    }

    /// <summary>Registers a custom request-side lookup provider (client-facing value -&gt; database value).</summary>
    public static FathomBuilder AddRequestLookup<T>(this FathomBuilder builder) where T : class, IRequestLookupProvider
    {
        builder.Services.TryAddSingleton<T>();
        builder.Services.AddSingleton<IRequestLookupProvider>(sp => sp.GetRequiredService<T>());
        return builder;
    }
}
