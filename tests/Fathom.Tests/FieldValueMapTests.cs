using Fathom.SqlServer;

namespace Fathom.Tests;

[TestFixture]
public class FieldValueMapTests
{
    private static FieldValueMap Map(out FieldSchema schema)
    {
        schema = FieldSchema.For(TestData.Orders().Root); // OrderNumber, OrderDate, Country, Total
        return new FieldValueMap(schema, ["ORD-1", new DateTime(2026, 6, 10), null, 12.5m]);
    }

    [Test]
    public void Lookup_is_case_insensitive_like_the_dictionary_it_replaces()
    {
        var map = Map(out _);
        Assert.Multiple(() =>
        {
            Assert.That(map["ordernumber"], Is.EqualTo("ORD-1"));
            Assert.That(map.ContainsKey("TOTAL"), Is.True);
        });
    }

    [Test]
    public void Null_valued_field_is_present_with_a_null_value()
    {
        var map = Map(out _);
        Assert.Multiple(() =>
        {
            Assert.That(map.TryGetValue("Country", out var value), Is.True, "the key exists");
            Assert.That(value, Is.Null);
            Assert.That(map.GetValueOrDefault("Country"), Is.Null);
        });
    }

    [Test]
    public void Unknown_field_misses_cleanly()
    {
        var map = Map(out _);
        Assert.Multiple(() =>
        {
            Assert.That(map.TryGetValue("NoSuchField", out _), Is.False);
            Assert.That(map.ContainsKey("NoSuchField"), Is.False);
            Assert.That(() => map["NoSuchField"], Throws.TypeOf<KeyNotFoundException>());
        });
    }

    [Test]
    public void Enumeration_yields_fields_in_declaration_order()
    {
        var map = Map(out _);
        Assert.That(map.Select(kv => kv.Key), Is.EqualTo(new[] { "OrderNumber", "OrderDate", "Country", "Total" }));
    }

    [Test]
    public void Copying_into_a_dictionary_preserves_every_pair()
    {
        // ApplyExportLookupsAsync copies the map through Dictionary's IEnumerable ctor when a
        // lookup rewrites a value — this is the exact path that must keep working.
        var map = Map(out _);
        var copy = new Dictionary<string, object?>(map, StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(copy, Has.Count.EqualTo(4));
            Assert.That(copy["OrderNumber"], Is.EqualTo("ORD-1"));
            Assert.That(copy["Country"], Is.Null);
        });
    }

    [Test]
    public void Rows_share_one_schema_but_carry_their_own_values()
    {
        var first = Map(out var schema);
        var second = new FieldValueMap(schema, ["ORD-2", null, "DK", 99m]);

        Assert.Multiple(() =>
        {
            Assert.That(first["OrderNumber"], Is.EqualTo("ORD-1"));
            Assert.That(second["OrderNumber"], Is.EqualTo("ORD-2"));
            Assert.That(second["Country"], Is.EqualTo("DK"));
        });
    }
}
