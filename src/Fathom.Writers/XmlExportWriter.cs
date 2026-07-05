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

        // Async disposal end to end: Kestrel's response body disallows synchronous writes by
        // default, and a synchronous Close()/Dispose() here could flush synchronously.
        await using var writer = XmlWriter.Create(destination, settings);
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

    private static async Task WriteRowAsync(XmlWriter writer, ExportRow row, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(null, row.Entity.Name, null);
        foreach (var field in row.Entity.Fields)
        {
            var text = FieldValueConverter.ToOutputString(field.Type, row.Values.GetValueOrDefault(field.Name));
            if (text is null)
            {
                await writer.WriteStartElementAsync(null, field.Name, null);
                await writer.WriteAttributeStringAsync(null, "nil", null, "true");
                await writer.WriteEndElementAsync();
            }
            else
            {
                await writer.WriteElementStringAsync(null, field.Name, null, SanitizeXml(text));
            }
        }

        foreach (var child in row.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(writer, child, cancellationToken);
        }

        await writer.WriteEndElementAsync();
    }

    /// <summary>
    /// Replaces characters that are legal in a SQL string but illegal in XML 1.0 (most C0
    /// control chars — NUL, and everything below space except tab/CR/LF) with U+FFFD, the
    /// replacement character. Without this a control byte in a <c>varchar</c> column would make
    /// the writer throw <em>after</em> the 200 and part of the body had already been sent,
    /// truncating the download into invalid XML. XML 1.0 has no way to represent these code
    /// points at all — not even as numeric references — so substitution is the only option that
    /// yields a well-formed document. Allocation-free on the common (clean) path.
    /// </summary>
    internal static string SanitizeXml(string text)
    {
        var clean = true;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsLegalXml(rune.Value))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(IsLegalXml(rune.Value) ? rune.ToString() : "�");
        }

        return builder.ToString();
    }

    // The XML 1.0 Char production: tab, LF, CR, then #x20–#xD7FF, #xE000–#xFFFD, #x10000–#x10FFFF.
    private static bool IsLegalXml(int codePoint) =>
        codePoint is 0x9 or 0xA or 0xD
        || codePoint is >= 0x20 and <= 0xD7FF
        || codePoint is >= 0xE000 and <= 0xFFFD
        || codePoint is >= 0x10000 and <= 0x10FFFF;
}
