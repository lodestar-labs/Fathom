namespace Fathom.Core.Pipeline;

/// <summary>
/// Streams a completed export to a destination stream in one specific format. Implementations
/// must write incrementally as roots arrive — never materialize the whole result set — so
/// memory for the write side stays bounded the same way the read side is.
/// </summary>
public interface IExportWriter
{
    /// <summary>Format key used in the <c>format</c> query parameter, e.g. "csv", "json", "xml".</summary>
    string Format { get; }

    /// <summary>
    /// The response content type for this specific export definition — not always fixed per
    /// writer: CSV renders a flat export as <c>text/csv</c> but a hierarchical one as a
    /// <c>application/zip</c> archive of one file per entity, since CSV cannot nest.
    /// </summary>
    string GetContentType(ExportDefinition definition);

    Task WriteAsync(
        Stream destination,
        ExportDefinition definition,
        IAsyncEnumerable<ExportRow> roots,
        CancellationToken cancellationToken = default);
}
