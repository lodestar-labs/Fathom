using Fathom.Core;
using Fathom.Core.Lookups;

namespace Fathom.SqlServer;

/// <summary>A filter with its request-lookup applied and its values parsed to the field's CLR type.</summary>
internal sealed record ResolvedFilter(FilterDefinition Definition, IReadOnlyList<object> Values);

/// <summary>The request supplied a filter value that could not be honored.</summary>
public sealed class FilterValidationException(string message) : Exception(message);

/// <summary>
/// Turns the raw filter values a client sent into <see cref="ResolvedFilter"/>s: applies the
/// filter's <see cref="IRequestLookupProvider"/> (if any) to translate client-facing values
/// into what the database stores, then parses the result to the filter's declared type.
/// </summary>
public sealed class FilterResolver(IEnumerable<IRequestLookupProvider> requestLookupProviders)
{
    private readonly Dictionary<string, IRequestLookupProvider> _providers =
        requestLookupProviders.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    internal async Task<IReadOnlyList<ResolvedFilter>> ResolveAsync(
        ExportDefinition definition,
        IReadOnlyList<FilterValue> requestFilters,
        CancellationToken cancellationToken = default)
    {
        var supplied = requestFilters.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedFilter>();

        foreach (var filterDef in definition.Filters)
        {
            if (!supplied.TryGetValue(filterDef.Name, out var value))
            {
                if (filterDef.Required)
                {
                    throw new FilterValidationException($"Filter '{filterDef.Name}' is required.");
                }

                continue;
            }

            if (filterDef.Operator is FilterOperator.IsNull or FilterOperator.IsNotNull)
            {
                result.Add(new ResolvedFilter(filterDef, []));
                continue;
            }

            var expectedCount = filterDef.Operator == FilterOperator.Between ? 2 : filterDef.Operator == FilterOperator.In ? -1 : 1;
            if (expectedCount >= 0 && value.Values.Count != expectedCount)
            {
                throw new FilterValidationException(
                    $"Filter '{filterDef.Name}' ({filterDef.Operator}) requires exactly {expectedCount} value(s); got {value.Values.Count}.");
            }

            if (value.Values.Count == 0)
            {
                throw new FilterValidationException($"Filter '{filterDef.Name}' requires at least one value.");
            }

            var typedValues = new List<object>(value.Values.Count);
            foreach (var rawValue in value.Values)
            {
                var resolvedRaw = rawValue;
                if (filterDef.RequestLookup is { } lookupName)
                {
                    if (!_providers.TryGetValue(lookupName, out var provider))
                    {
                        throw new InvalidOperationException(
                            $"Filter '{filterDef.Name}' references unregistered request lookup provider '{lookupName}'.");
                    }

                    try
                    {
                        resolvedRaw = await provider.ResolveAsync(rawValue, cancellationToken);
                    }
                    catch (LookupResolutionException ex)
                    {
                        throw new FilterValidationException($"Filter '{filterDef.Name}': {ex.Message}");
                    }
                }

                if (!FieldValueConverter.TryParse(filterDef.ValueType, resolvedRaw, out var typed))
                {
                    throw new FilterValidationException(
                        $"Filter '{filterDef.Name}': '{resolvedRaw}' is not a valid {filterDef.ValueType}.");
                }

                typedValues.Add(typed);
            }

            result.Add(new ResolvedFilter(filterDef, typedValues));
        }

        return result;
    }
}
