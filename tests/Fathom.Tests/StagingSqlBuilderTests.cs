using Fathom.Core;
using Fathom.SqlServer;

namespace Fathom.Tests;

[TestFixture]
public class StagingSqlBuilderTests
{
    [Test]
    public void Root_staging_numbers_rows_by_key_and_stages_a_zero_parent()
    {
        var root = TestData.Orders().Root;
        var temp = StagingSqlBuilder.TempTableName(root);
        var (sql, parameters) = StagingSqlBuilder.BuildStagingSql(root, parent: null, []);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("ROW_NUMBER() OVER (ORDER BY [OrderId]) AS RowNumber"));
            Assert.That(sql, Does.Contain("CAST(0 AS bigint) AS ParentRowNumber"));
            Assert.That(sql, Does.Contain("[OrderId] AS RealKey"));
            Assert.That(sql, Does.Contain($"INTO {temp}"));
            Assert.That(sql, Does.Contain("FROM [dbo].[Orders]"));
            Assert.That(sql, Does.Contain($"ON {temp}(RealKey)"));
            Assert.That(parameters, Is.Empty);
        });
    }

    [Test]
    public void Distinct_entity_names_that_sanitize_alike_get_distinct_temp_tables()
    {
        // "A-B" and "A.B" both sanitize to A_B; the hash suffix must keep them apart, or the
        // second SELECT INTO collides with the first at run time.
        var dash = new EntityDefinition { Name = "A-B", Table = "T", KeyColumn = "Id", Fields = [new FieldDefinition { Name = "X" }] };
        var dot = new EntityDefinition { Name = "A.B", Table = "T", KeyColumn = "Id", Fields = [new FieldDefinition { Name = "X" }] };

        Assert.That(StagingSqlBuilder.TempTableName(dash), Is.Not.EqualTo(StagingSqlBuilder.TempTableName(dot)));
    }

    [Test]
    public void Root_staging_aliases_columns_that_differ_from_their_output_name()
    {
        var entity = new EntityDefinition
        {
            Name = "Order",
            Table = "Orders",
            KeyColumn = "OrderId",
            Fields = [new FieldDefinition { Name = "Total", Column = "TotalAmount" }],
        };

        var (sql, _) = StagingSqlBuilder.BuildStagingSql(entity, parent: null, []);
        Assert.That(sql, Does.Contain("[TotalAmount] AS [Total]"));
    }

    [Test]
    public void Child_staging_joins_to_the_parents_staged_table_by_real_key()
    {
        var definition = TestData.Orders();
        var childTemp = StagingSqlBuilder.TempTableName(definition.Root.Children[0]);
        var parentTemp = StagingSqlBuilder.TempTableName(definition.Root);
        var (sql, _) = StagingSqlBuilder.BuildStagingSql(definition.Root.Children[0], definition.Root, []);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain($"INTO {childTemp}"));
            Assert.That(sql, Does.Contain("FROM [dbo].[OrderLines] AS c"));
            Assert.That(sql, Does.Contain($"INNER JOIN {parentTemp} AS p ON c.[OrderId] = p.RealKey"));
            Assert.That(sql, Does.Contain("ROW_NUMBER() OVER (ORDER BY ParentRowNumber, RealKey) AS RowNumber"));
            Assert.That(sql, Does.Contain("c.[LineId] AS RealKey"));
        });
    }

    [Test]
    public void Direct_select_for_a_flat_export_never_touches_a_temp_table()
    {
        var flatRoot = TestData.Orders().Root;
        flatRoot.Children.Clear();

        var (sql, parameters) = StagingSqlBuilder.BuildDirectSelectSql(flatRoot, []);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Not.Contain("INTO #"), "no staging");
            Assert.That(sql, Does.Contain("ROW_NUMBER() OVER (ORDER BY [OrderId]) AS RowNumber"));
            Assert.That(sql, Does.Contain("CAST(0 AS bigint) AS ParentRowNumber"));
            Assert.That(sql, Does.Contain("FROM [dbo].[Orders]"));
            // Ordered by the synthetic alias, not the key column: a field alias could shadow
            // a source column name, and the alias is the one name guaranteed unambiguous.
            Assert.That(sql, Does.Contain("ORDER BY RowNumber;"));
            Assert.That(parameters, Is.Empty);
        });
    }

    [Test]
    public void Direct_select_parameterizes_filters_exactly_like_staging_does()
    {
        var flatRoot = TestData.Orders().Root;
        flatRoot.Children.Clear();
        var filter = new FilterDefinition { Name = "orderNumber", Entity = "Order", Field = "OrderNumber" };
        var resolved = new ResolvedFilter(filter, ["ORD-1"]);

        var (sql, parameters) = StagingSqlBuilder.BuildDirectSelectSql(flatRoot, [resolved]);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("WHERE [OrderNumber] = @p_Order_0"));
            Assert.That(parameters.Single().Value, Is.EqualTo("ORD-1"));
        });
    }

    [Test]
    public void Final_select_of_root_orders_by_row_number_alone()
    {
        var sql = StagingSqlBuilder.BuildFinalSelectSql(TestData.Orders().Root);
        Assert.That(sql, Does.Contain("ORDER BY RowNumber;"));
    }

    [Test]
    public void Final_select_of_a_child_orders_by_parent_then_row_number()
    {
        var sql = StagingSqlBuilder.BuildFinalSelectSql(TestData.Orders().Root.Children[0]);
        Assert.That(sql, Does.Contain("ORDER BY ParentRowNumber, RowNumber;"));
    }

    [Test]
    public void Final_select_lists_fields_in_declaration_order_after_the_key_columns()
    {
        var sql = StagingSqlBuilder.BuildFinalSelectSql(TestData.Orders().Root);
        Assert.That(sql, Does.Contain("SELECT RowNumber, ParentRowNumber, [OrderNumber], [OrderDate], [Country], [Total]"));
    }

    [Test]
    public void Equals_filter_binds_the_value_as_a_parameter_never_inlined_into_the_sql()
    {
        var filter = new FilterDefinition { Name = "orderNumber", Entity = "Order", Field = "OrderNumber" };
        var resolved = new ResolvedFilter(filter, ["'; DROP TABLE Orders; --"]);

        var (whereSql, parameters) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [resolved], tableAlias: null);

        Assert.Multiple(() =>
        {
            Assert.That(whereSql, Does.Not.Contain("DROP TABLE"), "the raw value must never be concatenated into the SQL text");
            Assert.That(whereSql, Does.Match(@"WHERE \[OrderNumber\] = @p_Order_0"));
            Assert.That(parameters, Has.Count.EqualTo(1));
            Assert.That(parameters[0].ParameterName, Is.EqualTo("@p_Order_0"));
            Assert.That(parameters[0].Value, Is.EqualTo("'; DROP TABLE Orders; --"));
        });
    }

    [Test]
    public void In_filter_binds_one_parameter_per_value()
    {
        var filter = new FilterDefinition { Name = "orderNumber", Entity = "Order", Field = "OrderNumber", Operator = FilterOperator.In };
        var resolved = new ResolvedFilter(filter, ["A", "B", "C"]);

        var (whereSql, parameters) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [resolved], tableAlias: null);

        Assert.Multiple(() =>
        {
            Assert.That(whereSql, Does.Contain("[OrderNumber] IN (@p_Order_0, @p_Order_1, @p_Order_2)"));
            Assert.That(parameters, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void Between_filter_binds_two_parameters()
    {
        var filter = new FilterDefinition
        {
            Name = "orderDate", Entity = "Order", Field = "OrderDate", Operator = FilterOperator.Between, ValueType = FieldType.Date,
        };
        var resolved = new ResolvedFilter(filter, [new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)]);

        var (whereSql, parameters) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [resolved], tableAlias: null);

        Assert.Multiple(() =>
        {
            Assert.That(whereSql, Does.Contain("[OrderDate] BETWEEN @p_Order_0 AND @p_Order_1"));
            Assert.That(parameters, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void IsNull_and_IsNotNull_filters_bind_no_parameters()
    {
        var isNull = new ResolvedFilter(
            new FilterDefinition { Name = "a", Entity = "Order", Field = "Total", Operator = FilterOperator.IsNull }, []);
        var isNotNull = new ResolvedFilter(
            new FilterDefinition { Name = "b", Entity = "Order", Field = "Country", Operator = FilterOperator.IsNotNull }, []);

        var (whereSql, parameters) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [isNull, isNotNull], tableAlias: null);

        Assert.Multiple(() =>
        {
            Assert.That(whereSql, Does.Contain("[Total] IS NULL"));
            Assert.That(whereSql, Does.Contain("[Country] IS NOT NULL"));
            Assert.That(whereSql, Does.Contain(" AND "));
            Assert.That(parameters, Is.Empty);
        });
    }

    [Test]
    public void No_filters_produces_no_where_clause()
    {
        var (whereSql, parameters) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [], tableAlias: null);
        Assert.Multiple(() =>
        {
            Assert.That(whereSql, Is.Empty);
            Assert.That(parameters, Is.Empty);
        });
    }

    [Test]
    public void Child_where_clause_prefixes_columns_with_the_supplied_alias()
    {
        var filter = new FilterDefinition { Name = "sku", Entity = "Line", Field = "Sku" };
        var resolved = new ResolvedFilter(filter, ["KB-201"]);

        var (whereSql, _) = StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root.Children[0], [resolved], tableAlias: "c");
        Assert.That(whereSql, Does.Contain("c.[Sku] = @p_Line_0"));
    }

    [Test]
    public void Filter_targeting_an_unknown_field_throws()
    {
        var filter = new FilterDefinition { Name = "bogus", Entity = "Order", Field = "NoSuchField" };
        var resolved = new ResolvedFilter(filter, ["x"]);

        Assert.Throws<InvalidOperationException>(() =>
            StagingSqlBuilder.BuildWhereClause(TestData.Orders().Root, [resolved], tableAlias: null));
    }
}
