using Fathom.Core;

namespace Fathom.Tests;

[TestFixture]
public class RequestFilterBinderTests
{
    private static RequestFilterBinder.BindResult Bind(params (string Key, string[] Values)[] query) =>
        RequestFilterBinder.Bind(
            TestData.Orders(),
            query.Select(q => KeyValuePair.Create(q.Key, (IReadOnlyList<string>)q.Values)),
            "format");

    [Test]
    public void Declared_filters_bind_with_their_canonical_name()
    {
        var result = Bind(("COUNTRY", ["DK"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.UnknownKeys, Is.Empty);
            Assert.That(result.Filters.Single().Name, Is.EqualTo("country"), "canonical declared casing, not the request's");
            Assert.That(result.Filters.Single().Values, Is.EqualTo(new[] { "DK" }));
        });
    }

    [Test]
    public void Reserved_keys_are_neither_filters_nor_unknown()
    {
        var result = Bind(("format", ["csv"]), ("country", ["DK"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Filters, Has.Count.EqualTo(1));
            Assert.That(result.UnknownKeys, Is.Empty);
        });
    }

    [Test]
    public void A_typoed_filter_name_is_reported_not_silently_dropped()
    {
        var result = Bind(("countryy", ["DK"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Filters, Is.Empty);
            Assert.That(result.UnknownKeys, Is.EqualTo(new[] { "countryy" }));
        });
    }

    [Test]
    public void Multiple_values_for_one_key_stay_together()
    {
        var result = Bind(("orderNumber", ["A", "B"]));
        Assert.That(result.Filters.Single().Values, Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public void Empty_query_binds_nothing_and_reports_nothing()
    {
        var result = Bind();
        Assert.Multiple(() =>
        {
            Assert.That(result.Filters, Is.Empty);
            Assert.That(result.UnknownKeys, Is.Empty);
        });
    }
}
