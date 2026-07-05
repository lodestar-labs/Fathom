namespace Fathom.Core;

/// <summary>
/// One client-supplied filter value, before request-lookup resolution or type conversion.
/// <see cref="Values"/> holds one entry for <see cref="FilterOperator.Equals"/> /
/// <see cref="FilterOperator.GreaterThanOrEqual"/> / <see cref="FilterOperator.LessThanOrEqual"/>,
/// two for <see cref="FilterOperator.Between"/>, one or more for <see cref="FilterOperator.In"/>,
/// and none for <see cref="FilterOperator.IsNull"/> / <see cref="FilterOperator.IsNotNull"/>.
/// </summary>
public sealed record FilterValue(string Name, IReadOnlyList<string> Values);
