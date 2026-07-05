using Fathom.Core;
using Microsoft.Data.SqlClient;

namespace Fathom.SqlServer;

/// <summary>
/// A one-row lookahead over one entity's final, merge-ordered <see cref="SqlDataReader"/> —
/// the SQL-backed <see cref="ILevelCursor"/> the engine hands to <see cref="HierarchyMerger"/>.
/// Reads columns strictly in ascending ordinal order (RowNumber, ParentRowNumber, then fields
/// in declaration order — exactly the order the final SELECT emits them), which keeps it
/// compatible with <see cref="System.Data.CommandBehavior.SequentialAccess"/> so wide
/// text/binary columns stream instead of being buffered whole per row.
/// </summary>
internal sealed class ReaderCursor(SqlCommand command, SqlDataReader reader, EntityDefinition entity)
    : ILevelCursor, IAsyncDisposable
{
    private readonly FieldSchema _schema = FieldSchema.For(entity);
    private readonly int _fieldCount = entity.Fields.Count;

    public bool HasCurrent { get; private set; }

    public long CurrentRowNumber { get; private set; }

    public long CurrentParentRowNumber { get; private set; }

    public IReadOnlyDictionary<string, object?> CurrentValues { get; private set; } = new Dictionary<string, object?>();

    /// <summary>Advances to the next row, or clears <see cref="HasCurrent"/> when the reader is exhausted.</summary>
    public async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            HasCurrent = false;
            return;
        }

        CurrentRowNumber = reader.GetInt64(0);
        CurrentParentRowNumber = reader.GetInt64(1);

        var values = new object?[_fieldCount];
        for (var i = 0; i < _fieldCount; i++)
        {
            // Field i sits at ordinal i + 2 by construction of the SELECT list.
            var ordinal = i + 2;
            values[i] = await reader.IsDBNullAsync(ordinal, cancellationToken)
                ? null
                : reader.GetValue(ordinal);
        }

        CurrentValues = new FieldValueMap(_schema, values);
        HasCurrent = true;
    }

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await command.DisposeAsync();
    }
}
