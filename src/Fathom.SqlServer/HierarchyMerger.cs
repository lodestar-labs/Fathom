using System.Runtime.CompilerServices;
using Fathom.Core;
using Fathom.Core.Pipeline;

namespace Fathom.SqlServer;

/// <summary>
/// The N-way streaming merge at the heart of Fathom: reconstructs the full hierarchy tree
/// from one ordered cursor per entity level, holding at most one root subtree in memory.
/// </summary>
internal static class HierarchyMerger
{
    /// <summary>
    /// Transforms one row's raw values before they enter the tree — the engine passes its
    /// export-lookup application here; tests pass identity or a fake.
    /// </summary>
    public delegate Task<IReadOnlyDictionary<string, object?>> ValueTransform(
        EntityDefinition entity, IReadOnlyDictionary<string, object?> raw, CancellationToken cancellationToken);

    /// <summary>
    /// Yields every row of <paramref name="entity"/> whose ParentRowNumber matches
    /// <paramref name="parentRowNumber"/>, recursing into each row's own children before
    /// yielding it. Correct because every level was staged in
    /// <c>ORDER BY (ParentRowNumber, RealKey)</c> order — which is also the order the parent
    /// level is visited in — so a plain forward scan of each cursor is a valid merge join.
    /// </summary>
    public static async IAsyncEnumerable<ExportRow> ReadLevelAsync(
        EntityDefinition entity,
        long parentRowNumber,
        IReadOnlyDictionary<string, ILevelCursor> cursors,
        ValueTransform transformValues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = cursors[entity.Name];
        while (cursor.HasCurrent && cursor.CurrentParentRowNumber == parentRowNumber)
        {
            var rowNumber = cursor.CurrentRowNumber;
            var rawValues = cursor.CurrentValues;
            await cursor.AdvanceAsync(cancellationToken);

            var values = await transformValues(entity, rawValues, cancellationToken);
            var row = new ExportRow { Entity = entity, RowNumber = rowNumber, Values = values };

            foreach (var child in entity.Children)
            {
                await foreach (var childRow in ReadLevelAsync(child, rowNumber, cursors, transformValues, cancellationToken))
                {
                    row.Children.Add(childRow);
                }
            }

            yield return row;
        }
    }
}
