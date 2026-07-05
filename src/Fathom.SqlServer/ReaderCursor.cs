using Fathom.Core;
using Microsoft.Data.SqlClient;

namespace Fathom.SqlServer;

/// <summary>
/// A one-row lookahead over one entity's final, merge-ordered <see cref="SqlDataReader"/>.
/// <see cref="ExportQueryEngine"/> holds one cursor per entity and walks them in lockstep —
/// this is what turns N independently-staged, ROW_NUMBER-ordered result sets back into a
/// tree without ever materializing a whole level in memory.
/// </summary>
internal sealed class ReaderCursor(SqlCommand command, SqlDataReader reader, EntityDefinition entity) : IAsyncDisposable
{
    private int[]? _fieldOrdinals;

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

        _fieldOrdinals ??= [.. entity.Fields.Select(f => reader.GetOrdinal(f.Name))];

        CurrentRowNumber = reader.GetInt64(0);
        CurrentParentRowNumber = reader.GetInt64(1);

        var values = new Dictionary<string, object?>(entity.Fields.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entity.Fields.Count; i++)
        {
            var ordinal = _fieldOrdinals[i];
            values[entity.Fields[i].Name] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        CurrentValues = values;
        HasCurrent = true;
    }

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await command.DisposeAsync();
    }
}
