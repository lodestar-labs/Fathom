using System.Data;
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
///
/// Flat exports (a root with no children) skip staging entirely: staging exists to give
/// child levels a stable parent row number to correlate with, so with no children the rows
/// stream directly from the source table and never touch tempdb.
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
    /// ordinary thrown exception before committing to a response. The returned
    /// <see cref="ExportRun"/> owns the connection and readers and MUST be disposed by the
    /// caller (an <c>await using</c>); its <see cref="ExportRun.Rows"/> is the lazy part.
    /// </summary>
    public async Task<ExportRun> RunAsync(
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
        // Hoisted so the catch can dispose any cursors already opened when a later one fails.
        var disposables = new List<ReaderCursor>();
        try
        {
            var resolvedFilters = await filterResolver.ResolveAsync(definition, requestFilters, cancellationToken);

            connection = await connectionFactory.OpenAsync(cancellationToken);

            var entities = definition.EnumerateEntities().ToArray();
            var cursors = new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase);

            if (entities.Length == 1)
            {
                // Flat fast path: no children to correlate, so no staging — one direct,
                // filtered SELECT against the source table.
                var (sql, parameters) = StagingSqlBuilder.BuildDirectSelectSql(definition.Root, resolvedFilters);
                var cursor = await OpenCursorAsync(connection, sql, parameters, definition.Root, cancellationToken);
                cursors[definition.Root.Name] = cursor;
                disposables.Add(cursor);
                logger.LogDebug("Streaming flat export {Export} directly (no staging)", definition.Name);
            }
            else
            {
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

                foreach (var entity in entities)
                {
                    var sql = StagingSqlBuilder.BuildFinalSelectSql(entity);
                    var cursor = await OpenCursorAsync(connection, sql, parameters: [], entity, cancellationToken);
                    cursors[entity.Name] = cursor;
                    disposables.Add(cursor);
                }
            }

            return new ExportRun(
                new ExportResources(connection, disposables), cursors, definition, ApplyExportLookupsAsync, activity, stopwatch, logger);
        }
        catch (Exception ex)
        {
            var outcome = ex is OperationCanceledException ? "cancelled" : "error";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordCompletion(definition.Name, outcome, stopwatch.Elapsed);
            activity?.Dispose();

            // Dispose any readers/commands opened before the failure, then the connection.
            foreach (var cursor in disposables)
            {
                await cursor.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    private async Task<ReaderCursor> OpenCursorAsync(
        SqlConnection connection,
        string sql,
        List<SqlParameter> parameters,
        EntityDefinition entity,
        CancellationToken cancellationToken)
    {
        var command = new SqlCommand(sql, connection) { CommandTimeout = _commandTimeoutSeconds };
        ReaderCursor? cursor = null;
        try
        {
            if (parameters.Count > 0)
            {
                command.Parameters.AddRange([.. parameters]);
            }

            // SequentialAccess: the cursor reads columns strictly in ascending ordinal order,
            // which keeps large text/binary columns from being buffered whole per row.
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            cursor = new ReaderCursor(command, reader, entity);
            await cursor.AdvanceAsync(cancellationToken);
            return cursor;
        }
        catch
        {
            // A throw in ExecuteReaderAsync/AdvanceAsync would otherwise orphan this command
            // (and its reader) — the caller only tracks cursors it has already received.
            if (cursor is not null)
            {
                await cursor.DisposeAsync();
            }
            else
            {
                await command.DisposeAsync();
            }

            throw;
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

            var rawValue = FieldValueConverter.ToOutputString(field.Type, raw[field.Name]);
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
