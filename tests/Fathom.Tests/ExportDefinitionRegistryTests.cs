using Fathom.Core;

namespace Fathom.Tests;

[TestFixture]
public class ExportDefinitionRegistryTests
{
    [Test]
    public void Registering_a_valid_definition_makes_it_findable()
    {
        var registry = new ExportDefinitionRegistry();
        registry.Register(TestData.Orders());

        Assert.Multiple(() =>
        {
            Assert.That(registry.Find("orders"), Is.Not.Null);
            Assert.That(registry.Find("ORDERS"), Is.Not.Null, "lookup is case-insensitive");
            Assert.That(registry.All.Select(d => d.Name), Is.EqualTo(new[] { "orders" }));
        });
    }

    [Test]
    public void Registering_an_invalid_definition_throws_and_does_not_register_it()
    {
        var registry = new ExportDefinitionRegistry();
        var invalid = TestData.Orders();
        invalid.Root.Fields.Clear();

        Assert.Throws<ExportDefinitionException>(() => registry.Register(invalid));
        Assert.That(registry.Find("orders"), Is.Null);
    }

    [Test]
    public void Re_registering_the_same_name_replaces_the_definition()
    {
        var registry = new ExportDefinitionRegistry();
        registry.Register(TestData.Orders());

        var updated = TestData.Orders();
        updated.Description = "Updated";
        registry.Register(updated);

        Assert.Multiple(() =>
        {
            Assert.That(registry.All, Has.Count.EqualTo(1));
            Assert.That(registry.Find("orders")!.Description, Is.EqualTo("Updated"));
        });
    }

    [Test]
    public void Remove_deletes_a_registered_definition_and_reports_whether_it_existed()
    {
        var registry = new ExportDefinitionRegistry();
        registry.Register(TestData.Orders());

        Assert.Multiple(() =>
        {
            Assert.That(registry.Remove("orders"), Is.True);
            Assert.That(registry.Remove("orders"), Is.False, "already removed");
            Assert.That(registry.Find("orders"), Is.Null);
        });
    }

    [Test]
    public void GetRequired_throws_for_an_unregistered_name()
    {
        // GetRequired is a default interface method — only reachable through the interface type.
        IExportDefinitionRegistry registry = new ExportDefinitionRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("no-such-export"));
    }

    [Test]
    public void Serializer_round_trips_a_definition_through_json()
    {
        var original = TestData.Orders();
        var json = ExportDefinitionSerializer.Serialize(original);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var roundTripped = ExportDefinitionSerializer.DeserializeAsync(stream).GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Name, Is.EqualTo(original.Name));
            Assert.That(roundTripped.Root.Children, Has.Count.EqualTo(1));
            Assert.That(roundTripped.Filters, Has.Count.EqualTo(original.Filters.Count));
        });
    }

    [Test]
    public void Serializer_reports_malformed_json_as_an_export_definition_exception()
    {
        using var stream = new MemoryStream("{ not valid json"u8.ToArray());
        Assert.ThrowsAsync<ExportDefinitionException>(() => ExportDefinitionSerializer.DeserializeAsync(stream));
    }
}
