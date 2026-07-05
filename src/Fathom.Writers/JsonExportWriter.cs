using System.Text.Json;
using System.Text.Json.Serialization;
using Fathom.Core;
using Fathom.Core.Pipeline;

namespace Fathom.Writers;

/// <summary>
/// Streams roots as a JSON array, nesting each entity's children under a property named
/// after the child entity — the same shape Loadstone's JSON import reads, so a Fathom JSON
/// export is Loadstone-import-ready without transformation.
/// </summary>
public sealed class JsonExportWriter : IExportWriter
{
    private readonly JsonSerializerOptions _options = new() { Converters = { new ExportRowJsonConverter() } };

    public string Format => "json";

    public string GetContentType(ExportDefinition definition) => "application/json";

    public Task WriteAsync(
        Stream destination,
        ExportDefinition definition,
        IAsyncEnumerable<ExportRow> roots,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(destination, roots, _options, cancellationToken);
}

/// <summary>
/// Recursively writes one <see cref="ExportRow"/> as a JSON object: its fields as scalar
/// properties, then one array property per distinct child entity. Serialization of
/// <see cref="IAsyncEnumerable{ExportRow}"/> (handled by <see cref="JsonSerializer"/> itself)
/// is what keeps the whole export streaming — this converter only ever sees one root's
/// already-realized subtree at a time.
/// </summary>
internal sealed class ExportRowJsonConverter : JsonConverter<ExportRow>
{
    public override ExportRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Fathom's JSON export is write-only.");

    public override void Write(Utf8JsonWriter writer, ExportRow value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var field in value.Entity.Fields)
        {
            writer.WritePropertyName(field.Name);
            var fieldValue = value.Values.GetValueOrDefault(field.Name);
            if (field.Type == FieldType.Date && fieldValue is DateTime date)
            {
                // A declared date field renders as a date, not a midnight timestamp.
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                WriteValue(writer, fieldValue);
            }
        }

        foreach (var group in value.Children.GroupBy(c => c.Entity.Name))
        {
            writer.WritePropertyName(group.Key);
            writer.WriteStartArray();
            foreach (var child in group)
            {
                Write(writer, child, options);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case short sh:
                writer.WriteNumberValue(sh);
                break;
            case decimal d:
                writer.WriteNumberValue(d);
                break;
            case double db:
                writer.WriteNumberValue(db);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(FieldValueConverter.ToOutputString(value));
                break;
        }
    }
}
