using System.Xml.Linq;
using Fathom.Core;
using Fathom.Writers;
using static Fathom.Tests.ExportRowBuilder;

namespace Fathom.Tests;

[TestFixture]
public class XmlExportWriterTests
{
    // Built from code points so the source file stays pure ASCII (no embedded control or
    // replacement bytes, which are easy to corrupt in transit).
    private static readonly string Rc = ((char)0xFFFD).ToString(); // U+FFFD REPLACEMENT CHARACTER
    private static readonly string Nul = ((char)0x00).ToString();
    private static readonly string Soh = ((char)0x01).ToString();
    private static readonly string Vt = ((char)0x0B).ToString();
    private static readonly string Esc = ((char)0x1B).ToString();

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
    public async Task A_control_character_in_a_value_does_not_break_the_stream()
    {
        // NUL and SOH are legal in a SQL string but illegal in XML 1.0. They must be
        // substituted, not thrown on — otherwise the response truncates into invalid XML.
        var definition = new ExportDefinition
        {
            Name = "things",
            Root = new EntityDefinition
            {
                Name = "Thing", Table = "T", KeyColumn = "Id", Fields = [new FieldDefinition { Name = "Note" }],
            },
        };
        var rows = AsAsync(Row(definition.Root, 1, ("Note", "before" + Soh + Nul + "after")));

        using var output = new MemoryStream();
        Assert.DoesNotThrowAsync(() => Writer.WriteAsync(output, definition, rows));

        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.That(doc.Root!.Element("Thing")!.Element("Note")!.Value, Is.EqualTo("before" + Rc + Rc + "after"));
    }

    [Test]
    public void SanitizeXml_leaves_clean_text_including_tabs_newlines_and_astral_emoji_untouched()
    {
        Assert.Multiple(() =>
        {
            Assert.That(XmlExportWriter.SanitizeXml("plain text"), Is.EqualTo("plain text"));
            Assert.That(XmlExportWriter.SanitizeXml("tab\tlf\ncr\rok"), Is.EqualTo("tab\tlf\ncr\rok"));
            Assert.That(XmlExportWriter.SanitizeXml("wave \U0001F30A ok"), Is.EqualTo("wave \U0001F30A ok"));
        });
    }

    [Test]
    public void SanitizeXml_replaces_each_illegal_control_char()
    {
        Assert.That(XmlExportWriter.SanitizeXml("a" + Vt + "b" + Esc + "c"), Is.EqualTo("a" + Rc + "b" + Rc + "c"));
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
