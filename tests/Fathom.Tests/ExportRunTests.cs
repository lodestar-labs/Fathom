using System.Diagnostics;
using System.Diagnostics.Metrics;
using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fathom.Tests;

/// <summary>
/// Locks the resource-safety contract of <see cref="ExportRun"/>: disposing it releases the
/// underlying connection/readers <em>whether or not</em> the rows were ever enumerated. This
/// is the exact property that prevents a pooled-connection leak when a writer throws during
/// its own setup, before it begins reading — the scenario an adversarial review flagged.
/// </summary>
[TestFixture]
public class ExportRunTests
{
    private sealed class SpyResources : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OneRowCursor(long rowNumber) : ILevelCursor
    {
        // Primed with one row (as the engine leaves a real cursor after OpenCursorAsync);
        // the first Advance exhausts it.
        public bool HasCurrent { get; private set; } = true;

        public long CurrentRowNumber => rowNumber;

        public long CurrentParentRowNumber => 0;

        public IReadOnlyDictionary<string, object?> CurrentValues { get; } =
            new Dictionary<string, object?> { ["OrderNumber"] = "ORD-1" };

        public Task AdvanceAsync(CancellationToken cancellationToken)
        {
            HasCurrent = false;
            return Task.CompletedTask;
        }
    }

    private static ExportRun Run(SpyResources resources, params (string Entity, ILevelCursor Cursor)[] levels)
    {
        var definition = TestData.Orders();
        // Flatten to a single level so one fake cursor is a complete export.
        definition.Root.Children.Clear();
        var map = levels.ToDictionary(l => l.Entity, l => l.Cursor, StringComparer.OrdinalIgnoreCase);
        return new ExportRun(
            resources,
            map,
            definition,
            static (_, raw, _) => Task.FromResult(raw),
            activity: null,
            Stopwatch.StartNew(),
            NullLogger.Instance);
    }

    [Test]
    public async Task Disposing_without_ever_enumerating_still_releases_resources()
    {
        var resources = new SpyResources();
        var run = Run(resources, ("Order", new OneRowCursor(1)));

        // Simulate a writer that throws during its own setup, before touching run.Rows.
        await using (run)
        {
            // no enumeration
        }

        Assert.That(resources.DisposeCount, Is.EqualTo(1), "the connection must be released even though Rows was never read");
    }

    [Test]
    public async Task Disposing_after_full_enumeration_releases_resources_exactly_once()
    {
        var resources = new SpyResources();
        var run = Run(resources, ("Order", new OneRowCursor(1)));

        var count = 0;
        await using (run)
        {
            await foreach (var _ in run.Rows)
            {
                count++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(resources.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_run_marked_failed_records_a_failed_completion_metric()
    {
        // Uniquely named so the listener isn't confused by any other run's completion metric.
        const string exportName = "outcomemetrictest";
        string? capturedOutcome = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Fathom" && instrument.Name == "fathom.exports.completed")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? export = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "fathom.export") { export = tag.Value as string; }
                else if (tag.Key == "fathom.outcome") { outcome = tag.Value as string; }
            }

            if (export == exportName) { capturedOutcome = outcome; }
        });
        listener.Start();

        var definition = TestData.Orders();
        definition.Root.Children.Clear();
        definition.Name = exportName;
        var run = new ExportRun(
            new SpyResources(),
            new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase) { ["Order"] = new OneRowCursor(1) },
            definition,
            static (_, raw, _) => Task.FromResult(raw),
            activity: null,
            Stopwatch.StartNew(),
            NullLogger.Instance);

        run.MarkFailed(cancelled: false); // a writer threw before its first read
        await run.DisposeAsync();

        Assert.That(capturedOutcome, Is.EqualTo("error"), "a faulted export must not be tagged success");
    }

    [Test]
    public async Task Double_dispose_releases_resources_once()
    {
        var resources = new SpyResources();
        var run = Run(resources, ("Order", new OneRowCursor(1)));

        await run.DisposeAsync();
        await run.DisposeAsync();

        Assert.That(resources.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Rows_cannot_be_enumerated_twice()
    {
        var resources = new SpyResources();
        await using var run = Run(resources, ("Order", new OneRowCursor(1)));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in run.Rows) { }
            await foreach (var _ in run.Rows) { }
        });
    }
}
