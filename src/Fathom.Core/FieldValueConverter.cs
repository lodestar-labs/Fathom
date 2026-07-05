using System.Globalization;

namespace Fathom.Core;

/// <summary>Converts between the raw string values a request carries and .NET/SQL typed values. Invariant culture throughout.</summary>
public static class FieldValueConverter
{
    public static Type ClrTypeFor(FieldType type) => type switch
    {
        FieldType.String => typeof(string),
        FieldType.Int32 => typeof(int),
        FieldType.Int64 => typeof(long),
        FieldType.Decimal => typeof(decimal),
        FieldType.Boolean => typeof(bool),
        FieldType.DateTime => typeof(DateTime),
        FieldType.Date => typeof(DateTime),
        FieldType.Guid => typeof(Guid),
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };

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
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
