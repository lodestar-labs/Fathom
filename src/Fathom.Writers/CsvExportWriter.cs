using System.IO.Compression;
using System.Text;
using Fathom.Core;
using Fathom.Core.Pipeline;

namespace Fathom.Writers;

/// <summary>
/// Streams roots as CSV. A flat export (no children) writes a single CSV file — no key
/// columns needed. A hierarchical export writes a zip archive with one <c>{Entity}.csv</c>
/// per entity, each row carrying <c>_key</c> (and, on every non-root entity, <c>_parentKey</c>)
/// — exactly the convention Loadstone's own hierarchical CSV *import* expects, so a Fathom
/// export of this shape can be fed straight back into a Loadstone import with no transform.
/// </summary>
public sealed class CsvExportWriter : IExportWriter
{
    public string Format => "csv";

    public string GetContentType(ExportDefinition definition) =>
        definition.Root.Children.Count > 0 ? "application/zip" : "text/csv; charset=utf-8";

    public async Task WriteAsync(
        Stream destination,
        ExportDefinition definition,
        IAsyncEnumerable<ExportRow> roots,
        CancellationToken cancellationToken = default)
    {
        if (definition.Root.Children.Count == 0)
        {
            await WriteFlatAsync(destination, definition.Root, roots, cancellationToken);
        }
        else
        {
            await WriteHierarchicalAsync(destination, definition, roots, cancellationToken);
        }
    }

    private static async Task WriteFlatAsync(
        Stream destination, EntityDefinition entity, IAsyncEnumerable<ExportRow> roots, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        WriteHeader(writer, entity, includeKeys: false);
        await foreach (var row in roots.WithCancellation(cancellationToken))
        {
            WriteDataRow(writer, entity, row, parentKey: null, includeKeys: false);
        }

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// A <see cref="ZipArchive"/> in create mode allows only one entry's stream open at a
    /// time, but rows for every entity arrive interleaved in a single pass over
    /// <paramref name="roots"/>. So each entity streams to its own temp file first — keeping
    /// the same one-subtree-in-memory bound as every other writer — and only once the source
    /// is exhausted are the temp files copied into the archive's entries, one fully at a time.
    /// </summary>
    private static async Task WriteHierarchicalAsync(
        Stream destination, ExportDefinition definition, IAsyncEnumerable<ExportRow> roots, CancellationToken cancellationToken)
    {
        var entities = definition.EnumerateEntities().ToArray();
        var tempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var writers = new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entity in entities)
            {
                var path = Path.GetTempFileName();
                tempPaths[entity.Name] = path;
                var writer = new StreamWriter(
                    File.Open(path, FileMode.Truncate, FileAccess.Write), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                WriteHeader(writer, entity, includeKeys: true);
                writers[entity.Name] = writer;
            }

            await foreach (var root in roots.WithCancellation(cancellationToken))
            {
                WriteTree(root, parentKey: null, writers);
            }

            foreach (var writer in writers.Values)
            {
                await writer.FlushAsync(cancellationToken);
                await writer.DisposeAsync();
            }

            writers.Clear();

            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entity in entities)
            {
                var entry = archive.CreateEntry($"{entity.Name}.csv", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(tempPaths[entity.Name]);
                await fileStream.CopyToAsync(entryStream, cancellationToken);
            }
        }
        finally
        {
            foreach (var writer in writers.Values)
            {
                await writer.DisposeAsync();
            }

            foreach (var path in tempPaths.Values)
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteTree(ExportRow row, long? parentKey, Dictionary<string, StreamWriter> writers)
    {
        var writer = writers[row.Entity.Name];
        WriteDataRow(writer, row.Entity, row, parentKey, includeKeys: true);
        foreach (var child in row.Children)
        {
            WriteTree(child, row.RowNumber, writers);
        }
    }

    private static void WriteHeader(TextWriter writer, EntityDefinition entity, bool includeKeys)
    {
        var columns = new List<string>();
        if (includeKeys)
        {
            columns.Add("_key");
            if (entity.ParentKeyColumn is not null)
            {
                columns.Add("_parentKey");
            }
        }

        columns.AddRange(entity.Fields.Select(f => f.Name));
        writer.Write(string.Join(',', columns));
        writer.Write("\r\n");
    }

    private static void WriteDataRow(TextWriter writer, EntityDefinition entity, ExportRow row, long? parentKey, bool includeKeys)
    {
        var first = true;
        void WriteSeparator()
        {
            if (!first)
            {
                writer.Write(',');
            }

            first = false;
        }

        if (includeKeys)
        {
            WriteSeparator();
            writer.Write(row.RowNumber);
            if (entity.ParentKeyColumn is not null)
            {
                WriteSeparator();
                writer.Write(parentKey);
            }
        }

        foreach (var field in entity.Fields)
        {
            WriteSeparator();
            CsvField.Write(writer, FieldValueConverter.ToOutputString(field.Type, row.Values.GetValueOrDefault(field.Name)));
        }

        writer.Write("\r\n");
    }
}

/// <summary>RFC 4180 field escaping: quote a field only when it contains a comma, quote, or line break.</summary>
internal static class CsvField
{
    public static void Write(TextWriter writer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('"');
    }
}
