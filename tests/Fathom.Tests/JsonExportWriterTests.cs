using System.Text.Json;
using Fathom.Writers;
using static Fathom.Tests.ExportRowBuilder;

namespace Fathom.Tests;

[TestFixture]
public class JsonExportWriterTests
{
    private static readonly JsonExportWriter Writer = new();

    [Test]
    public void Content_type_is_always_json() =>
        Assert.That(Writer.GetContentType(TestData.Orders()), Is.EqualTo("application/json"));

    [Test]
    public async Task Writes_a_json_array_with_children_grouped_by_entity_name_under_the_child_entitys_name()
    {
        var definition = TestData.Orders();
        using var output = new MemoryStream();
        await Writer.WriteAsync(output, definition, AsAsync(SampleOrders(definition)));

        using var doc = JsonDocument.Parse(output.ToArray());
        var root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(root.GetArrayLength(), Is.EqualTo(2));

        var order1 = root[0];
        Assert.Multiple(() =>
        {
            Assert.That(order1.GetProperty("OrderNumber").GetString(), Is.EqualTo("ORD-2001"));
            Assert.That(order1.GetProperty("Total").GetDecimal(), Is.EqualTo(512.25m));
            Assert.That(order1.GetProperty("Line").ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(order1.GetProperty("Line").GetArrayLength(), Is.EqualTo(2));
            Assert.That(order1.GetProperty("Line")[0].GetProperty("Sku").GetString(), Is.EqualTo("KB-201"));
            Assert.That(order1.GetProperty("Line")[1].GetProperty("Quantity").GetInt32(), Is.EqualTo(4));
        });

        var order2 = root[1];
        Assert.That(order2.GetProperty("Country").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task An_entity_with_no_rows_writes_an_empty_array()
    {
        var definition = TestData.Orders();
        using var output = new MemoryStream();
        await Writer.WriteAsync(output, definition, AsAsync());

        using var doc = JsonDocument.Parse(output.ToArray());
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(0));
    }
}
