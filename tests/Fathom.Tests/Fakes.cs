using Fathom.Core.Lookups;

namespace Fathom.Tests;

/// <summary>Resolves a fixed set of known raw values; throws <see cref="LookupResolutionException"/> for anything else.</summary>
internal sealed class FakeRequestLookupProvider(string name, IReadOnlyDictionary<string, string> values) : IRequestLookupProvider
{
    public string Name { get; } = name;

    public ValueTask<string> ResolveAsync(string rawValue, CancellationToken cancellationToken = default) =>
        values.TryGetValue(rawValue, out var resolved)
            ? ValueTask.FromResult(resolved)
            : throw new LookupResolutionException($"'{rawValue}' is not a known value for lookup '{Name}'.");
}

/// <summary>Resolves every raw value to a fixed output string, or passes it through unchanged when <paramref name="fixedOutput"/> is null.</summary>
internal sealed class FakeExportLookupProvider(string name, string? fixedOutput) : IExportLookupProvider
{
    public string Name { get; } = name;

    public ValueTask<string?> ResolveAsync(string rawValue, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(fixedOutput);
}
