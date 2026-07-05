using System.Text;
using System.Xml;
using Fathom.Core;
using Fathom.Core.Pipeline;

namespace Fathom.Writers;

/// <summary>Streams roots as nested XML elements — one element per entity, named after it, fields as child elements.</summary>
public sealed class XmlExportWriter : IExportWriter
{
    public string Format => "xml";

    public string GetContentType(ExportDefinition definition) => "application/xml";

    public async Task WriteAsync(
        Stream destination,
        ExportDefinition definition,
        IAsyncEnumerable<ExportRow> roots,
        CancellationToken cancellationToken = default)
    {
        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        var writer = XmlWriter.Create(destination, settings);
        try
        {
            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null, definition.Name, null);
            await foreach (var root in roots.WithCancellation(cancellationToken))
            {
                await WriteRowAsync(writer, root, cancellationToken);
            }

            await writer.WriteEndElementAsync();
            await writer.WriteEndDocumentAsync();
            await writer.FlushAsync();
        }
        finally
        {
            writer.Close();
        }
    }

    private static async Task WriteRowAsync(XmlWriter writer, ExportRow row, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, row.Entity.Name, null);
        foreach (var field in row.Entity.Fields)
        {
            var text = FieldValueConverter.ToOutputString(row.Values.GetValueOrDefault(field.Name));
            if (text is null)
            {
                await writer.WriteStartElementAsync(null, field.Name, null);
                await writer.WriteAttributeStringAsync(null, "nil", null, "true");
                await writer.WriteEndElementAsync();
            }
            else
            {
                await writer.WriteElementStringAsync(null, field.Name, null, text);
            }
        }

        foreach (var child in row.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(writer, child, cancellationToken);
        }

        await writer.WriteEndElementAsync();
    }
}
