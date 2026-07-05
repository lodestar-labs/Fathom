namespace Fathom.Core;

/// <summary>
/// One filter a client may supply when running an export: which field it narrows, how
/// (<see cref="Operator"/>), and — for values that don't match what the column actually
/// stores — a named <see cref="Lookups.IRequestLookupProvider"/> that resolves the raw
/// request value (e.g. a country name) into the value the database expects (e.g. a numeric
/// country code) before it reaches SQL.
/// </summary>
public sealed class FilterDefinition
{
    /// <summary>The query-string/body key clients use to supply this filter, e.g. "country".</summary>
    public required string Name { get; set; }

    public required string Entity { get; set; }

    public required string Field { get; set; }

    public FilterOperator Operator { get; set; } = FilterOperator.Equals;

    public FieldType ValueType { get; set; } = FieldType.String;

    /// <summary>Name of a registered <see cref="Lookups.IRequestLookupProvider"/>, or null to use the raw request value as-is.</summary>
    public string? RequestLookup { get; set; }

    public bool Required { get; set; }
}

public enum FilterOperator
{
    Equals,
    In,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Between,
    IsNull,
    IsNotNull,
}
