using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fathom.SqlServer.Diagnostics;

/// <summary>
/// Single home for Fathom's traces and metrics. Hook these names into any OpenTelemetry
/// pipeline (<c>AddSource("Fathom")</c> / <c>AddMeter("Fathom")</c>) and every export emits
/// spans and counters without further wiring.
/// </summary>
public static class FathomDiagnostics
{
    public const string ActivitySourceName = "Fathom";

    public const string MeterName = "Fathom";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ExportsStarted = Meter.CreateCounter<long>(
        "fathom.exports.started", description: "Export runs started, tagged by definition.");

    public static readonly Counter<long> ExportsCompleted = Meter.CreateCounter<long>(
        "fathom.exports.completed", description: "Export runs finished, tagged by definition and outcome.");

    public static readonly Counter<long> RowsExported = Meter.CreateCounter<long>(
        "fathom.rows.exported", description: "Rows streamed to the client, tagged by definition and entity.");

    public static readonly Histogram<double> ExportDuration = Meter.CreateHistogram<double>(
        "fathom.export.duration", unit: "s", description: "End-to-end export run duration, tagged by definition and outcome.");
}
