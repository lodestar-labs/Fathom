using Fathom.Core;
using Fathom.Core.Lookups;
using Fathom.SqlServer;

namespace Fathom.Tests;

[TestFixture]
public class FilterResolverTests
{
    private static FilterResolver Resolver(params IRequestLookupProvider[] providers) => new(providers);

    [Test]
    public async Task Missing_optional_filter_is_silently_skipped()
    {
        var resolved = await Resolver().ResolveAsync(TestData.Orders(), []);
        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public void Missing_required_filter_throws()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "mustHave", Entity = "Order", Field = "Total", Required = true });

        Assert.ThrowsAsync<FilterValidationException>(() => Resolver().ResolveAsync(definition, []));
    }

    [Test]
    public async Task Equals_filter_parses_its_single_value()
    {
        var resolved = await Resolver().ResolveAsync(TestData.Orders(), [new FilterValue("orderNumber", ["ORD-2001"])]);
        Assert.That(resolved.Single().Values, Is.EqualTo(new object[] { "ORD-2001" }));
    }

    [Test]
    public void Equals_filter_with_more_than_one_value_throws()
    {
        Assert.ThrowsAsync<FilterValidationException>(() =>
            Resolver().ResolveAsync(TestData.Orders(), [new FilterValue("orderNumber", ["A", "B"])]));
    }

    [Test]
    public void Between_filter_requires_exactly_two_values()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition
        {
            Name = "orderDateRange", Entity = "Order", Field = "OrderDate", Operator = FilterOperator.Between, ValueType = FieldType.Date,
        });

        Assert.ThrowsAsync<FilterValidationException>(() =>
            Resolver().ResolveAsync(definition, [new FilterValue("orderDateRange", ["2026-01-01"])]));
    }

    [Test]
    public void In_filter_with_zero_values_throws()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "many", Entity = "Order", Field = "OrderNumber", Operator = FilterOperator.In });

        Assert.ThrowsAsync<FilterValidationException>(() =>
            Resolver().ResolveAsync(definition, [new FilterValue("many", [])]));
    }

    [Test]
    public async Task In_filter_accepts_any_positive_number_of_values()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "many", Entity = "Order", Field = "OrderNumber", Operator = FilterOperator.In });

        var resolved = await Resolver().ResolveAsync(definition, [new FilterValue("many", ["A", "B", "C"])]);
        Assert.That(resolved.Single().Values, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task IsNull_filter_carries_no_values_even_if_the_request_supplied_some()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "noCountry", Entity = "Order", Field = "Country", Operator = FilterOperator.IsNull });

        var resolved = await Resolver().ResolveAsync(definition, [new FilterValue("noCountry", ["ignored"])]);
        Assert.That(resolved.Single().Values, Is.Empty);
    }

    [Test]
    public async Task Request_lookup_resolves_the_raw_value_before_type_parsing()
    {
        var provider = new FakeRequestLookupProvider("countries", new Dictionary<string, string> { ["Denmark"] = "45" });
        var resolved = await Resolver(provider).ResolveAsync(TestData.Orders(), [new FilterValue("country", ["Denmark"])]);
        Assert.That(resolved.Single().Values, Is.EqualTo(new object[] { "45" }));
    }

    [Test]
    public void Request_lookup_miss_becomes_a_filter_validation_exception()
    {
        var provider = new FakeRequestLookupProvider("countries", new Dictionary<string, string>());
        var ex = Assert.ThrowsAsync<FilterValidationException>(() =>
            Resolver(provider).ResolveAsync(TestData.Orders(), [new FilterValue("country", ["Atlantis"])]));
        Assert.That(ex!.Message, Does.Contain("Atlantis"));
    }

    [Test]
    public void Unregistered_request_lookup_provider_is_a_configuration_error_not_a_client_error()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            Resolver().ResolveAsync(TestData.Orders(), [new FilterValue("country", ["Denmark"])]));
    }

    [Test]
    public void Value_that_does_not_parse_as_the_filters_type_throws_filter_validation_exception()
    {
        Assert.ThrowsAsync<FilterValidationException>(() =>
            Resolver().ResolveAsync(TestData.Orders(), [new FilterValue("orderDateFrom", ["not-a-date"])]));
    }
}
