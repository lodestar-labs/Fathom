using System.Xml.Linq;
using Fathom.Writers;
using static Fathom.Tests.ExportRowBuilder;

namespace Fathom.Tests;

[TestFixture]
public class XmlExportWriterTests
{
    private static readonly XmlExportWriter Writer = new();

    [Test]
    public void Content_type_is_application_xml() =>
        Assert.That(Writer.GetContentType(TestData.Orders()), Is.EqualTo("application/xml"));

    [Test]
    public async Task Writes_nested_elements_named_after_the_definition_and_each_entity()
    {
        var definition = TestData.Orders();
        using var output = new MemoryStream();
        await Writer.WriteAsync(output, definition, AsAsync(SampleOrders(definition)));

        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.That(doc.Root!.Name.LocalName, Is.EqualTo("orders"));

        var orders = doc.Root.Elements("Order").ToList();
        Assert.That(orders, Has.Count.EqualTo(2));

        var firstOrder = orders[0];
        Assert.Multiple(() =>
        {
            Assert.That(firstOrder.Element("OrderNumber")!.Value, Is.EqualTo("ORD-2001"));
            Assert.That(firstOrder.Elements("Line").Count(), Is.EqualTo(2));
            Assert.That(firstOrder.Elements("Line").First().Element("Sku")!.Value, Is.EqualTo("KB-201"));
        });
    }

    [Test]
    public async Task Null_field_values_are_written_as_a_nil_marked_empty_element()
    {
        var definition = TestData.Orders();
        using var output = new MemoryStream();
        await Writer.WriteAsync(output, definition, AsAsync(SampleOrders(definition)));

        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        var secondOrder = doc.Root!.Elements("Order").ElementAt(1);
        var country = secondOrder.Element("Country")!;

        Assert.Multiple(() =>
        {
            Assert.That(country.IsEmpty, Is.True);
            Assert.That(country.Attribute("nil")!.Value, Is.EqualTo("true"));
        });
    }
}
