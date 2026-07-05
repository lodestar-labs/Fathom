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

    [TestCase("Order Line", Description = "a space breaks XML element names")]
    [TestCase("2024Orders", Description = "a leading digit breaks XML element names")]
    [TestCase("Lines/All", Description = "a path separator breaks zip entry names")]
    [TestCase("na\"me", Description = "a quote breaks the Content-Disposition header")]
    public void Unsafe_entity_names_are_rejected_at_registration(string name)
    {
        var definition = TestData.Orders();
        definition.Root.Children[0].Name = name;
        Assert.That(definition.Validate(), Has.Some.Contains("valid XML name"));
    }

    [Test]
    public void Unsafe_field_and_filter_and_export_names_are_rejected_too()
    {
        var definition = TestData.Orders();
        definition.Name = "orders export";
        definition.Root.Fields[0].Name = "Order Number";
        definition.Filters[0].Name = "country code";

        var errors = definition.Validate();
        Assert.That(errors.Count(e => e.Contains("valid XML name")), Is.EqualTo(3));
    }

    [TestCase("RowNumber")]
    [TestCase("parentrownumber")]
    [TestCase("RealKey")]
    public void Engine_reserved_field_names_are_rejected(string name)
    {
        var definition = TestData.Orders();
        definition.Root.Children[0].Fields.Add(new FieldDefinition { Name = name });
        Assert.That(definition.Validate(), Has.Some.Contains("is reserved"));
    }

    [Test]
    public void Unicode_letters_in_names_are_welcome()
    {
        var definition = TestData.Orders();
        definition.Root.Fields[3].Name = "Præmie"; // Total — the one field no filter references
        Assert.That(definition.Validate(), Is.Empty);
    }

    [TestCase("Order:Line", Description = "a colon is not a valid XML NCName char")]
    [TestCase("Ⅷvalue", Description = "a Unicode number-letter .NET calls a letter but XML rejects as a start char")]
    public void Names_that_are_not_xml_safe_are_rejected(string name)
    {
        var definition = TestData.Orders();
        definition.Root.Children[0].Name = name;
        Assert.That(definition.Validate(), Has.Some.Contains("valid XML name"));
    }

    [Test]
    public void Anything_the_name_rule_accepts_can_be_written_as_an_xml_element()
    {
        // The safety invariant behind the rule: a validated name is always a legal XML NCName,
        // so the writer can never throw on an element name after the response has started.
        string[] samples = ["Order", "Order_Line", "line-2", "v1.2", "Præmie", "_hidden", "æøå"];
        foreach (var name in samples)
        {
            Assume.That(ExportDefinition.IsValidName(name), Is.True, $"'{name}' should be a valid name");
            Assert.DoesNotThrow(() => System.Xml.XmlConvert.VerifyNCName(name), $"'{name}' must be XML-writable");
        }
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
    public void QualifiedTable_neutralizes_a_bracket_in_the_table_name()
    {
        // The classic stacked-statement injection: a ']' that would otherwise close the
        // bracket-quote and let following text run as SQL. It must be doubled, exactly as
        // columns are, so the whole thing stays one inert identifier.
        var entity = new EntityDefinition
        {
            Name = "Evil",
            Table = "Orders]; DROP TABLE Orders; --",
            KeyColumn = "Id",
            Fields = [new FieldDefinition { Name = "X" }],
        };

        Assert.That(entity.QualifiedTable, Is.EqualTo("[dbo].[Orders]]; DROP TABLE Orders; --]"));
    }

    [Test]
    public void Control_characters_in_a_table_name_are_rejected_at_registration()
    {
        var definition = TestData.Orders();
        definition.Root.Table = "Orders\r\nGO";
        Assert.That(definition.Validate(), Has.Some.Contains("control characters"));
    }

    [Test]
    public void A_schema_or_column_over_128_chars_is_rejected()
    {
        var definition = TestData.Orders();
        definition.Root.Fields[3].Column = new string('x', 129);
        Assert.That(definition.Validate(), Has.Some.Contains("1–128 characters"));
    }

    [Test]
    public void More_than_the_entity_limit_is_rejected()
    {
        var definition = TestData.Orders();
        for (var i = 0; i < 200; i++)
        {
            definition.Root.Children.Add(new EntityDefinition
            {
                Name = $"Extra{i}",
                Table = "T",
                KeyColumn = "Id",
                ParentKeyColumn = "OrderId",
                Fields = [new FieldDefinition { Name = "X" }],
            });
        }

        Assert.That(definition.Validate(), Has.Some.Contains("the maximum is 100"));
    }

    [Test]
    public void Field_ColumnName_defaults_to_field_name_when_column_is_unset() =>
        Assert.That(new FieldDefinition { Name = "Total" }.ColumnName, Is.EqualTo("Total"));

    [Test]
    public void Field_ColumnName_uses_explicit_column_when_set() =>
        Assert.That(new FieldDefinition { Name = "Total", Column = "TotalAmount" }.ColumnName, Is.EqualTo("TotalAmount"));
}
