using Fathom.Core;

namespace Fathom.Tests;

[TestFixture]
public class ExportDefinitionTests
{
    [Test]
    public void Valid_definition_has_no_errors() =>
        Assert.That(TestData.Orders().Validate(), Is.Empty);

    [Test]
    public void Missing_name_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Name = "";
        Assert.That(definition.Validate(), Has.Some.Contains("'name' is required"));
    }

    [Test]
    public void Duplicate_entity_names_are_an_error()
    {
        var definition = TestData.Orders();
        definition.Root.Children.Add(new EntityDefinition
        {
            Name = "Order",
            Table = "Whatever",
            KeyColumn = "Id",
            ParentKeyColumn = "OrderId",
            Fields = [new FieldDefinition { Name = "X" }],
        });

        Assert.That(definition.Validate(), Has.Some.Contains("names must be unique"));
    }

    [Test]
    public void Non_root_entity_without_parent_key_column_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Root.Children[0].ParentKeyColumn = null;
        Assert.That(definition.Validate(), Has.Some.Contains("'parentKeyColumn' is required"));
    }

    [Test]
    public void Root_entity_does_not_require_a_parent_key_column() =>
        Assert.That(TestData.Orders().Root.ParentKeyColumn, Is.Null);

    [Test]
    public void Entity_with_no_fields_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Root.Children[0].Fields.Clear();
        Assert.That(definition.Validate(), Has.Some.Contains("at least one field is required"));
    }

    [Test]
    public void Duplicate_field_names_within_an_entity_are_an_error()
    {
        var definition = TestData.Orders();
        definition.Root.Fields.Add(new FieldDefinition { Name = "OrderNumber" });
        Assert.That(definition.Validate(), Has.Some.Contains("duplicate field 'OrderNumber'"));
    }

    [Test]
    public void Filter_referencing_unknown_entity_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "bogus", Entity = "NoSuchEntity", Field = "X" });
        Assert.That(definition.Validate(), Has.Some.Contains("unknown entity 'NoSuchEntity'"));
    }

    [Test]
    public void Filter_referencing_unknown_field_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition { Name = "bogus", Entity = "Order", Field = "NoSuchField" });
        Assert.That(definition.Validate(), Has.Some.Contains("field 'NoSuchField' does not exist"));
    }

    [Test]
    public void Between_operator_on_a_string_field_is_an_error()
    {
        var definition = TestData.Orders();
        definition.Filters.Add(new FilterDefinition
        {
            Name = "bogus",
            Entity = "Order",
            Field = "OrderNumber",
            Operator = FilterOperator.Between,
            ValueType = FieldType.String,
        });

        Assert.That(definition.Validate(), Has.Some.Contains("'between' is not supported"));
    }

    [Test]
    public void EnumerateEntities_visits_parent_before_children()
    {
        var names = TestData.Orders().EnumerateEntities().Select(e => e.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "Order", "Line" }));
    }

    [Test]
    public void FindEntity_is_case_insensitive() =>
        Assert.That(TestData.Orders().FindEntity("LINE"), Is.Not.Null);

    [Test]
    public void FindParent_of_root_is_null() =>
        Assert.That(TestData.Orders().FindParent(TestData.Orders().Root), Is.Null);

    [Test]
    public void FindParent_of_a_child_returns_its_parent()
    {
        var definition = TestData.Orders();
        var line = definition.Root.Children[0];
        Assert.That(definition.FindParent(line), Is.SameAs(definition.Root));
    }

    [Test]
    public void QualifiedTable_combines_schema_and_table() =>
        Assert.That(TestData.Orders().Root.QualifiedTable, Is.EqualTo("[dbo].[Orders]"));

    [Test]
    public void Field_ColumnName_defaults_to_field_name_when_column_is_unset() =>
        Assert.That(new FieldDefinition { Name = "Total" }.ColumnName, Is.EqualTo("Total"));

    [Test]
    public void Field_ColumnName_uses_explicit_column_when_set() =>
        Assert.That(new FieldDefinition { Name = "Total", Column = "TotalAmount" }.ColumnName, Is.EqualTo("TotalAmount"));
}
