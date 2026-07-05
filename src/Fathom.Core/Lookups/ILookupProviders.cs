namespace Fathom.Core.Lookups;

/// <summary>
/// Transforms a raw exported field value into its output representation — e.g. a numeric
/// species code into its FAO string, or a numeric country code into an ISO name. Registered
/// by <see cref="Name"/> and referenced from a <see cref="FieldDefinition"/>'s
/// <c>Lookup</c> property. Called once per (field, row); implementations that need I/O to
/// resolve a value should cache internally — this runs on the hot streaming path.
/// </summary>
public interface IExportLookupProvider
{
    /// <summary>The name other configuration refers to this provider by.</summary>
    string Name { get; }

    /// <summary>
    /// Resolves <paramref name="rawValue"/> to its output form. Returns null to pass the
    /// raw value through unchanged (a provider may choose to only handle some values).
    /// </summary>
    ValueTask<string?> ResolveAsync(string rawValue, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a raw request-supplied filter value (e.g. a country name a client typed) into
/// the value actually stored in the database (e.g. a numeric country code) before it is
/// bound as a SQL parameter. Registered by <see cref="Name"/> and referenced from a
/// <see cref="FilterDefinition"/>'s <c>RequestLookup</c> property.
/// </summary>
public interface IRequestLookupProvider
{
    string Name { get; }

    /// <summary>
    /// Resolves <paramref name="rawValue"/> to the value the database expects. Throw
    /// <see cref="LookupResolutionException"/> when the value cannot be resolved — the API
    /// host turns that into a 400 naming the offending filter.
    /// </summary>
    ValueTask<string> ResolveAsync(string rawValue, CancellationToken cancellationToken = default);
}

/// <summary>Thrown by an <see cref="IRequestLookupProvider"/> when a filter value cannot be resolved.</summary>
public sealed class LookupResolutionException(string message) : Exception(message);
