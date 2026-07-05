using Fathom.Core;

namespace Fathom.Tests;

/// <summary>A small orders export (Order → Line) shared across the test suite — the same shape as the shipped sample.</summary>
internal static class TestData
{
    public static ExportDefinition Orders() => new()
    {
        Name = "orders",
        Description = "Test export",
        Root = new EntityDefinition
        {
            Name = "Order",
            Table = "Orders",
            KeyColumn = "OrderId",
            Fields =
            [
                new FieldDefinition { Name = "OrderNumber" },
                new FieldDefinition { Name = "OrderDate", Type = FieldType.Date },
                new FieldDefinition { Name = "Country", Lookup = "countries" },
                new FieldDefinition { Name = "Total", Type = FieldType.Decimal },
            ],
            Children =
            [
                new EntityDefinition
                {
                    Name = "Line",
                    Table = "OrderLines",
                    KeyColumn = "LineId",
                    ParentKeyColumn = "OrderId",
                    Fields =
                    [
                        new FieldDefinition { Name = "LineNumber", Type = FieldType.Int32 },
                        new FieldDefinition { Name = "Sku" },
                        new FieldDefinition { Name = "Quantity", Type = FieldType.Int32 },
                    ],
                },
            ],
        },
        Filters =
        [
            new FilterDefinition { Name = "country", Entity = "Order", Field = "Country", RequestLookup = "countries" },
            new FilterDefinition { Name = "orderDateFrom", Entity = "Order", Field = "OrderDate", Operator = FilterOperator.GreaterThanOrEqual, ValueType = FieldType.Date },
            new FilterDefinition { Name = "orderDateTo", Entity = "Order", Field = "OrderDate", Operator = FilterOperator.LessThanOrEqual, ValueType = FieldType.Date },
            new FilterDefinition { Name = "orderNumber", Entity = "Order", Field = "OrderNumber" },
        ],
    };
}
