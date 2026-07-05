using System.Globalization;
using Fathom.Core;

namespace Fathom.Tests;

[TestFixture]
public class FieldValueConverterTests
{
    [Test]
    public void Parse_int32_uses_invariant_culture() =>
        Assert.That(FieldValueConverter.Parse(FieldType.Int32, "42"), Is.EqualTo(42));

    [Test]
    public void Parse_decimal_accepts_a_period_regardless_of_current_culture()
    {
        using var _ = new CultureScope("da-DK"); // uses ',' as the decimal separator
        Assert.That(FieldValueConverter.Parse(FieldType.Decimal, "512.25"), Is.EqualTo(512.25m));
    }

    [Test]
    public void Parse_boolean_accepts_true_and_false() =>
        Assert.Multiple(() =>
        {
            Assert.That(FieldValueConverter.Parse(FieldType.Boolean, "true"), Is.EqualTo(true));
            Assert.That(FieldValueConverter.Parse(FieldType.Boolean, "False"), Is.EqualTo(false));
        });

    [Test]
    public void Parse_date_ignores_any_time_component_in_the_clr_value()
    {
        var value = (DateTime)FieldValueConverter.Parse(FieldType.Date, "2026-06-10");
        Assert.That(value, Is.EqualTo(new DateTime(2026, 6, 10)));
    }

    [Test]
    public void Parse_guid_round_trips()
    {
        var guid = Guid.NewGuid();
        Assert.That(FieldValueConverter.Parse(FieldType.Guid, guid.ToString()), Is.EqualTo(guid));
    }

    [Test]
    public void TryParse_returns_false_instead_of_throwing_on_bad_input()
    {
        Assert.That(FieldValueConverter.TryParse(FieldType.Int32, "not-a-number", out var value), Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void TryParse_returns_true_with_the_parsed_value_on_good_input()
    {
        Assert.That(FieldValueConverter.TryParse(FieldType.Int32, "7", out var value), Is.True);
        Assert.That(value, Is.EqualTo(7));
    }

    [Test]
    public void ToOutputString_of_null_and_dbnull_is_null() =>
        Assert.Multiple(() =>
        {
            Assert.That(FieldValueConverter.ToOutputString(null), Is.Null);
            Assert.That(FieldValueConverter.ToOutputString(DBNull.Value), Is.Null);
        });

    [Test]
    public void ToOutputString_of_decimal_uses_invariant_culture()
    {
        using var _ = new CultureScope("da-DK");
        Assert.That(FieldValueConverter.ToOutputString(512.25m), Is.EqualTo("512.25"));
    }

    [Test]
    public void ToOutputString_of_bool_is_lowercase() =>
        Assert.Multiple(() =>
        {
            Assert.That(FieldValueConverter.ToOutputString(true), Is.EqualTo("true"));
            Assert.That(FieldValueConverter.ToOutputString(false), Is.EqualTo("false"));
        });

    [Test]
    public void ToOutputString_of_datetime_round_trips_through_parse()
    {
        var original = new DateTime(2026, 6, 10, 14, 30, 0, DateTimeKind.Utc);
        var rendered = FieldValueConverter.ToOutputString(original);
        Assert.That(DateTime.Parse(rendered!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Is.EqualTo(original));
    }

    [Test]
    public void ToOutputString_of_int_uses_plain_digits() =>
        Assert.That(FieldValueConverter.ToOutputString(42), Is.EqualTo("42"));

    [Test]
    public void A_date_typed_field_renders_as_a_plain_date_not_a_midnight_timestamp()
    {
        var midnight = new DateTime(2026, 6, 10);
        Assert.Multiple(() =>
        {
            Assert.That(FieldValueConverter.ToOutputString(FieldType.Date, midnight), Is.EqualTo("2026-06-10"));
            Assert.That(FieldValueConverter.ToOutputString(FieldType.DateTime, midnight), Does.StartWith("2026-06-10T00:00:00"));
            Assert.That(FieldValueConverter.ToOutputString(FieldType.Date, "not-a-date"), Is.EqualTo("not-a-date"),
                "a non-DateTime value passes through untouched");
        });
    }

    [Test]
    public void ToOutputString_of_binary_is_base64_not_the_type_name()
    {
        var rendered = FieldValueConverter.ToOutputString(new byte[] { 1, 2, 3 });
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Is.EqualTo("AQID"));
            Assert.That(rendered, Does.Not.Contain("Byte[]"));
        });
    }

    /// <summary>Temporarily swaps the current thread's culture, restoring it on dispose.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
