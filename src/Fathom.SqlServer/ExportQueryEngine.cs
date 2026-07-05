using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fathom.Core;
using Fathom.Core.Lookups;
using Fathom.Core.Pipeline;
using Fathom.SqlServer.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fathom.SqlServer;

/// <summary>
/// Runs one export end to end: resolve request filters, stage every hierarchy level into
/// row-numbered temp tables (parents first), then stream all levels back out through an
/// N-way merge that reconstructs the tree — one root subtree in memory at a time, however
/// deep or wide the hierarchy, however many rows the whole export contains.
/// </summary>
public sealed class ExportQueryEngine(
    SqlConnectionFactory connectionFactory,
    FilterResolver filterResolver,
    IEnumerable<IExportLookupProvider> exportLookupProviders,
    IOptions<FathomOptions> options,
    ILogger<ExportQueryEngine> logger)
{
    private readonly Dictionary<string, IExportLookupProvider> _exportLookups =
        exportLookupProviders.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    // SqlCommand.CommandTimeout is whole seconds; a sub-second ExportTimeout would truncate to
    // 0, which SqlClient treats as "no timeout" — the opposite of what a small value asks for.
    private readonly int _commandTimeoutSeconds = Math.Max(1, (int)options.Value.ExportTimeout.TotalSeconds);

    /// <summary>
    /// Resolves filters and stages every level — the part that can fail on bad input (an
    /// unresolvable filter value, a SQL error) — eagerly, so callers see that failure as an
    /// ordinary thrown exception before committing to a response. The returned sequence is
    /// the lazy part: reading the already-staged levels back out through the merge.
    /// </summary>
    public async Task<IAsyncEnumerable<ExportRow>> RunAsync(
        ExportDefinition definition,
        IReadOnlyList<FilterValue> requestFilters,
        CancellationToken cancellationToken = default)
    {
        var exportTag = new KeyValuePair<string, object?>("fathom.export", definition.Name);
        var activity = FathomDiagnostics.ActivitySource.StartActivity("fathom.export");
        activity?.SetTag("fathom.export", definition.Name);
        var stopwatch = Stopwatch.StartNew();
        FathomDiagnostics.ExportsStarted.Add(1, exportTag);

        SqlConnection? connection = null;
        try
        {
            var resolvedFilters = await filterResolver.ResolveAsync(definition, requestFilters, cancellationToken);

            connection = await connectionFactory.OpenAsync(cancellationToken);

            var entities = definition.EnumerateEntities().ToArray();
            foreach (var entity in entities)
            {
                var parent = definition.FindParent(entity);
                var filtersForEntity = resolvedFilters
                    .Where(f => string.Equals(f.Definition.Entity, entity.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var (sql, parameters) = StagingSqlBuilder.BuildStagingSql(entity, parent, filtersForEntity);

                await using var command = new SqlCommand(sql, connection) { CommandTimeout = _commandTimeoutSeconds };
                command.Parameters.AddRange([.. parameters]);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            logger.LogDebug("Staged {EntityCount} entities for export {Export}", entities.Length, definition.Name);

            var cursors = new Dictionary<string, ReaderCursor>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                var sql = StagingSqlBuilder.BuildFinalSelectSql(entity);
                var command = new SqlCommand(sql, connection) { CommandTimeout = _commandTimeoutSeconds };
                var reader = await command.ExecuteReaderAsync(cancellationToken);
                var cursor = new ReaderCursor(command, reader, entity);
                await cursor.AdvanceAsync(cancellationToken);
                cursors[entity.Name] = cursor;
            }

            return StreamAsync(connection, cursors, definition, activity, stopwatch, cancellationToken);
        }
        catch (Exception ex)
        {
            var outcome = ex is OperationCanceledException ? "cancelled" : "error";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordCompletion(definition.Name, outcome, stopwatch.Elapsed);
            activity?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>
    /// The lazy half: streams roots from the already-staged cursors. Drives the merge's
    /// enumerator manually (rather than <c>await foreach</c>) so failures during enumeration
    /// can be caught and tagged before disposal — a <c>yield return</c> cannot appear inside a
    /// <c>try</c> block with a <c>catch</c>, so the catch lives around <c>MoveNextAsync</c> only.
    /// </summary>
    private async IAsyncEnumerable<ExportRow> StreamAsync(
        SqlConnection connection,
        Dictionary<string, ReaderCursor> cursors,
        ExportDefinition definition,
        Activity? activity,
        Stopwatch stopwatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var outcome = "success";
        var enumerator = ReadLevelAsync(definition.Root, parentRowNumber: 0, cursors, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ExportRow current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    outcome = ex is OperationCanceledException ? "cancelled" : "error";
                    throw;
                }

                CountRow(current, rowCounts);
                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            foreach (var cursor in cursors.Values)
            {
                await cursor.DisposeAsync();
            }

            await connection.DisposeAsync();

            activity?.SetTag("fathom.outcome", outcome);
            foreach (var (entityName, count) in rowCounts)
            {
                FathomDiagnostics.RowsExported.Add(
                    count,
                    new KeyValuePair<string, object?>("fathom.export", definition.Name),
                    new KeyValuePair<string, object?>("fathom.entity", entityName));
            }

            RecordCompletion(definition.Name, outcome, stopwatch.Elapsed);
            activity?.Dispose();
        }
    }

    private static void CountRow(ExportRow row, Dictionary<string, long> counts)
    {
        counts[row.Entity.Name] = counts.GetValueOrDefault(row.Entity.Name) + 1;
        foreach (var child in row.Children)
        {
            CountRow(child, counts);
        }
    }

    private static void RecordCompletion(string exportName, string outcome, TimeSpan elapsed)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("fathom.export", exportName),
            new("fathom.outcome", outcome),
        };
        FathomDiagnostics.ExportsCompleted.Add(1, tags);
        FathomDiagnostics.ExportDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>
    /// Yields every row of <paramref name="entity"/> whose staged ParentRowNumber matches
    /// <paramref name="parentRowNumber"/>, recursing into each row's own children before
    /// yielding it. Correct because every level was staged in
    /// <c>ORDER BY (ParentRowNumber, RealKey)</c> order — the same relative order the parent
    /// level is visited in — so a plain forward scan of each cursor is a valid merge join.
    /// </summary>
    private async IAsyncEnumerable<ExportRow> ReadLevelAsync(
        EntityDefinition entity,
        long parentRowNumber,
        Dictionary<string, ReaderCursor> cursors,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = cursors[entity.Name];
        while (cursor.HasCurrent && cursor.CurrentParentRowNumber == parentRowNumber)
        {
            var rowNumber = cursor.CurrentRowNumber;
            var rawValues = cursor.CurrentValues;
            await cursor.AdvanceAsync(cancellationToken);

            var values = await ApplyExportLookupsAsync(entity, rawValues, cancellationToken);
            var row = new ExportRow { Entity = entity, RowNumber = rowNumber, Values = values };

            foreach (var child in entity.Children)
            {
                await foreach (var childRow in ReadLevelAsync(child, rowNumber, cursors, cancellationToken))
                {
                    row.Children.Add(childRow);
                }
            }

            yield return row;
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>> ApplyExportLookupsAsync(
        EntityDefinition entity,
        IReadOnlyDictionary<string, object?> raw,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?>? resolved = null;
        foreach (var field in entity.Fields)
        {
            if (field.Lookup is not { } lookupName)
            {
                continue;
            }

            if (!_exportLookups.TryGetValue(lookupName, out var provider))
            {
                throw new InvalidOperationException(
                    $"Field '{field.Name}' on entity '{entity.Name}' references unregistered export lookup provider '{lookupName}'.");
            }

            var rawValue = FieldValueConverter.ToOutputString(raw[field.Name]);
            if (rawValue is null)
            {
                continue;
            }

            var mapped = await provider.ResolveAsync(rawValue, cancellationToken);
            if (mapped is null)
            {
                continue;
            }

            resolved ??= new Dictionary<string, object?>(raw, StringComparer.OrdinalIgnoreCase);
            resolved[field.Name] = mapped;
        }

        return resolved ?? raw;
    }
}
