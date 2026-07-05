using Fathom.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fathom.Tests;

[TestFixture]
public class ExportDefinitionStoreTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fathom-store-" + TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private ExportDefinitionDirectoryStore Store() => new(_dir, NullLogger<ExportDefinitionDirectoryStore>.Instance);

    [Test]
    public async Task Names_differing_only_in_a_dot_versus_underscore_do_not_share_a_file()
    {
        var store = Store();
        var dotted = TestData.Orders();
        dotted.Name = "orders.v2";
        var underscored = TestData.Orders();
        underscored.Name = "orders_v2";

        await store.SaveAsync(dotted);
        await store.SaveAsync(underscored);

        var loaded = await store.LoadAllAsync();
        Assert.That(loaded.Select(d => d.Name), Is.EquivalentTo(new[] { "orders.v2", "orders_v2" }),
            "previously both sanitized to orders_v2.json and overwrote each other");
    }

    [Test]
    public async Task Delete_removes_only_the_named_definition()
    {
        var store = Store();
        var a = TestData.Orders();
        a.Name = "orders.v2";
        var b = TestData.Orders();
        b.Name = "orders_v2";
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        await store.DeleteAsync("orders.v2");

        var loaded = await store.LoadAllAsync();
        Assert.That(loaded.Select(d => d.Name), Is.EqualTo(new[] { "orders_v2" }));
    }

    [Test]
    public void A_traversal_style_name_is_refused_rather_than_escaping_the_directory()
    {
        var store = Store();
        var evil = TestData.Orders();
        // Not a valid NCName (contains separators), so the store must refuse to map it to a path.
        evil.Name = "../../etc/passwd";

        Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(evil));
    }
}
