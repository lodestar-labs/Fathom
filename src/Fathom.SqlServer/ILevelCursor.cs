namespace Fathom.SqlServer;

/// <summary>
/// A forward-only, one-row-lookahead cursor over one staged hierarchy level, in merge order
/// (<c>ORDER BY ParentRowNumber, RowNumber</c>). This is the whole contract the N-way merge
/// needs from a data source — abstracting it lets <see cref="HierarchyMerger"/> be tested
/// exhaustively with in-memory cursors, no database required.
/// </summary>
internal interface ILevelCursor
{
    bool HasCurrent { get; }

    long CurrentRowNumber { get; }

    long CurrentParentRowNumber { get; }

    IReadOnlyDictionary<string, object?> CurrentValues { get; }

    Task AdvanceAsync(CancellationToken cancellationToken);
}
