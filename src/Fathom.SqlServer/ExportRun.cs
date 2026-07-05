using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Fathom.SqlServer;

/// <summary>
/// A live export: the open connection, the staged temp tables, and one reader per level
/// (bundled behind an <see cref="IAsyncDisposable"/>), plus the row stream that reads them
/// back through the N-way merge.
///
/// Resource ownership is deliberately separated from enumeration. Disposing an
/// <see cref="ExportRun"/> releases those resources — and records completion metrics — exactly
/// once, whether or not the caller ever enumerated <see cref="Rows"/>. Callers MUST dispose it
/// (an <c>await using</c>). This is what makes a writer that throws <em>before</em> its first
/// read (e.g. failing to open its temp files) safe: the pooled connection is still returned
/// instead of leaking until finalization.
/// </summary>
public sealed class ExportRun : IAsyncDisposable
{
    private readonly IAsyncDisposable _resources;
    private readonly IReadOnlyDictionary<string, ILevelCursor> _levels;
    private readonly ExportDefinition _definition;
    private readonly HierarchyMerger.ValueTransform _transform;
    private readonly Activity? _activity;
    private readonly Stopwatch _stopwatch;
    private readonly ILogger _logger;
    private readonly Dictionary<string, long> _rowCounts = new(StringComparer.OrdinalIgnoreCase);
    private string _outcome = "success";
    private int _enumerated;
    private int _disposed;

    internal ExportRun(
        IAsyncDisposable resources,
        IReadOnlyDictionary<string, ILevelCursor> levels,
        ExportDefinition definition,
        HierarchyMerger.ValueTransform transform,
        Activity? activity,
        Stopwatch stopwatch,
        ILogger logger)
    {
        _resources = resources;
        _levels = levels;
        _definition = definition;
        _transform = transform;
        _activity = activity;
        _stopwatch = stopwatch;
        _logger = logger;
    }

    /// <summary>The reconstructed hierarchy, streamed one root subtree at a time. Enumerate at most once.</summary>
    public IAsyncEnumerable<ExportRow> Rows => ReadAsync();

    private async IAsyncEnumerable<ExportRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException("An export's rows can only be enumerated once.");
        }

        var enumerator = HierarchyMerger
            .ReadLevelAsync(_definition.Root, parentRowNumber: 0, _levels, _transform, cancellationToken)
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
                    _outcome = ex is OperationCanceledException ? "cancelled" : "error";
                    throw;
                }

                CountRow(current, _rowCounts);
                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Release the connection + readers + temp tables regardless of whether Rows was read.
        await _resources.DisposeAsync();

        _activity?.SetTag("fathom.outcome", _outcome);
        long totalRows = 0;
        foreach (var (entityName, count) in _rowCounts)
        {
            totalRows += count;
            FathomDiagnostics.RowsExported.Add(
                count,
                new KeyValuePair<string, object?>("fathom.export", _definition.Name),
                new KeyValuePair<string, object?>("fathom.entity", entityName));
        }

        var tags = new KeyValuePair<string, object?>[]
        {
            new("fathom.export", _definition.Name),
            new("fathom.outcome", _outcome),
        };
        FathomDiagnostics.ExportsCompleted.Add(1, tags);
        FathomDiagnostics.ExportDuration.Record(_stopwatch.Elapsed.TotalSeconds, tags);
        _activity?.Dispose();

        _logger.LogInformation(
            "Export {Export} {Outcome}: {Rows} rows in {ElapsedMs} ms",
            _definition.Name, _outcome, totalRows, (long)_stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void CountRow(ExportRow row, Dictionary<string, long> counts)
    {
        counts[row.Entity.Name] = counts.GetValueOrDefault(row.Entity.Name) + 1;
        foreach (var child in row.Children)
        {
            CountRow(child, counts);
        }
    }
}
