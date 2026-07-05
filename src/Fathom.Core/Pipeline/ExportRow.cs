namespace Fathom.Core.Pipeline;

/// <summary>
/// One row of one entity, with its resolved (lookup-applied) output values and its direct
/// children already attached. The engine yields a fully realized tree per root — memory is
/// bounded to one root subtree at a time, however deep or wide the hierarchy is.
/// </summary>
public sealed class ExportRow
{
    /// <summary>Entity this row belongs to.</summary>
    public required EntityDefinition Entity { get; init; }

    /// <summary>
    /// Sequence number assigned by the engine, unique within this entity's rows for this
    /// export run — not a database identifier. Writers that need to correlate rows across
    /// separate per-entity outputs (e.g. one CSV file per entity) use this as the row's key
    /// and the parent row's RowNumber as its parent key.
    /// </summary>
    public required long RowNumber { get; init; }

    /// <summary>Output values keyed by <see cref="FieldDefinition.Name"/>, already lookup-resolved.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    public List<ExportRow> Children { get; } = [];
}
