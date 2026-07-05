using System.Globalization;

namespace Fathom.Core;

/// <summary>Converts between the raw string values a request carries and .NET/SQL typed values. Invariant culture throughout.</summary>
public static class FieldValueConverter
{
    /// <summary>Parses a raw request/lookup-resolved string into the CLR value the field type expects.</summary>
    public static object Parse(FieldType type, string raw) => type switch
    {
        FieldType.String => raw,
        FieldType.Int32 => int.Parse(raw, CultureInfo.InvariantCulture),
        FieldType.Int64 => long.Parse(raw, CultureInfo.InvariantCulture),
        FieldType.Decimal => decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
        FieldType.Boolean => bool.Parse(raw),
        FieldType.DateTime => DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        FieldType.Date => DateOnly.Parse(raw, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),
        FieldType.Guid => Guid.Parse(raw),
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };

    public static bool TryParse(FieldType type, string raw, out object value)
    {
        try
        {
            value = Parse(type, raw);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            value = null!;
            return false;
        }
    }

    /// <summary>Renders a value read back from the database as its output string (invariant, round-trippable).</summary>
    public static string? ToOutputString(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// Renders a value honoring the field's declared type: a <see cref="FieldType.Date"/>
    /// field renders as a plain <c>yyyy-MM-dd</c> date rather than a midnight timestamp —
    /// SQL Server's <c>date</c> columns surface as <see cref="DateTime"/>, and a date field
    /// exported as <c>2026-06-10T00:00:00.0000000</c> would be both ugly and harder to
    /// round-trip into date-typed import fields.
    /// </summary>
    public static string? ToOutputString(FieldType type, object? value) =>
        type == FieldType.Date && value is DateTime dt
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : ToOutputString(value);
}
