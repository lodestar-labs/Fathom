using System.IO.Compression;
using System.Text;
using Fathom.Core;
using Fathom.Writers;
using static Fathom.Tests.ExportRowBuilder;

namespace Fathom.Tests;

[TestFixture]
public class CsvExportWriterTests
{
    private static readonly CsvExportWriter Writer = new();

    [Test]
    public void Content_type_is_zip_for_a_hierarchical_export() =>
        Assert.That(Writer.GetContentType(TestData.Orders()), Is.EqualTo("application/zip"));

    [Test]
    public void Content_type_is_plain_csv_for_a_flat_export()
    {
        var flat = new ExportDefinition
        {
            Name = "flat",
            Root = new EntityDefinition { Name = "Order", Table = "Orders", KeyColumn = "OrderId", Fields = [new FieldDefinition { Name = "Total" }] },
        };

        Assert.That(Writer.GetContentType(flat), Is.EqualTo("text/csv"));
    }

    [Test]
    public async Task Flat_export_writes_a_plain_csv_with_no_key_columns()
    {
        var flat = new ExportDefinition
        {
            Name = "flat",
            Root = new EntityDefinition
            {
                Name = "Order",
                Table = "Orders",
                KeyColumn = "OrderId",
                Fields = [new FieldDefinition { Name = "OrderNumber" }, new FieldDefinition { Name = "Total", Type = FieldType.Decimal }],
            },
        };
        var rows = AsAsync(
            Row(flat.Root, 1, ("OrderNumber", "ORD-2001"), ("Total", 512.25m)),
            Row(flat.Root, 2, ("OrderNumber", "ORD-2002"), ("Total", 42.00m)));

        using var output = new MemoryStream();
        await Writer.WriteAsync(output, flat, rows);
        var text = Encoding.UTF8.GetString(output.ToArray());

        // decimal.ToString() preserves the literal's scale — 42.00m renders as "42.00", not "42".
        Assert.That(text, Is.EqualTo("OrderNumber,Total\r\nORD-2001,512.25\r\nORD-2002,42.00\r\n"));
    }

    [Test]
    public async Task Hierarchical_export_writes_one_zip_entry_per_entity_with_key_and_parent_key_columns()
    {
        var definition = TestData.Orders();
        using var output = new MemoryStream();
        await Writer.WriteAsync(output, definition, AsAsync(SampleOrders(definition)));

        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.That(archive.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { "Order.csv", "Line.csv" }));

        var orderCsv = ReadEntry(archive, "Order.csv");
        var lineCsv = ReadEntry(archive, "Line.csv");

        Assert.Multiple(() =>
        {
            Assert.That(orderCsv[0], Is.EqualTo("_key,OrderNumber,OrderDate,Country,Total"));
            Assert.That(orderCsv[1], Does.StartWith("1,ORD-2001,"));
            Assert.That(orderCsv[2], Does.StartWith("2,ORD-2002,"));

            Assert.That(lineCsv[0], Is.EqualTo("_key,_parentKey,LineNumber,Sku,Quantity"));
            Assert.That(lineCsv[1], Is.EqualTo("1,1,1,KB-201,1"));
            Assert.That(lineCsv[2], Is.EqualTo("2,1,2,CH-770,4"));
            Assert.That(lineCsv[3], Is.EqualTo("1,2,1,MS-115,1"));
        });
    }

    [Test]
    public void Fields_containing_commas_or_quotes_are_rfc4180_escaped()
    {
        using var writer = new StringWriter();
        CsvField.Write(writer, "Acme, Inc. \"the best\"");
        Assert.That(writer.ToString(), Is.EqualTo("\"Acme, Inc. \"\"the best\"\"\""));
    }

    [Test]
    public void Plain_fields_are_written_unquoted()
    {
        using var writer = new StringWriter();
        CsvField.Write(writer, "ORD-2001");
        Assert.That(writer.ToString(), Is.EqualTo("ORD-2001"));
    }

    private static string[] ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
