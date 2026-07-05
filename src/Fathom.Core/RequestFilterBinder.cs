namespace Fathom.Core;

/// <summary>
/// Binds raw request query parameters to an export's declared filters — strictly. Any query
/// key that is neither a declared filter nor a reserved key (e.g. <c>format</c>) is reported
/// back as unknown rather than ignored, because for a data-export API a silently dropped
/// filter is a data-governance incident: a client that typos <c>?countryy=DK</c> must get a
/// 400 naming the mistake, not the full unfiltered table.
/// </summary>
public static class RequestFilterBinder
{
    public sealed record BindResult(IReadOnlyList<FilterValue> Filters, IReadOnlyList<string> UnknownKeys);

    public static BindResult Bind(
        ExportDefinition definition,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> query,
        params string[] reservedKeys)
    {
        var declared = definition.Filters.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var filters = new List<FilterValue>();
        var unknown = new List<string>();

        foreach (var (key, values) in query)
        {
            if (reservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (declared.TryGetValue(key, out var filter))
            {
                // Canonical declared name, so downstream case-sensitive consumers agree.
                filters.Add(new FilterValue(filter.Name, values));
            }
            else
            {
                unknown.Add(key);
            }
        }

        return new BindResult(filters, unknown);
    }
}
