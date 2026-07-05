using Fathom.Core;
using Fathom.Core.Pipeline;

namespace Fathom.Tests;

/// <summary>Test-only helpers for building an in-memory <see cref="ExportRow"/> tree and streaming it as an <see cref="IAsyncEnumerable{ExportRow}"/>, the same shape a writer consumes from the real query engine.</summary>
internal static class ExportRowBuilder
{
    public static ExportRow Row(EntityDefinition entity, long rowNumber, params (string Field, object? Value)[] values) =>
        new()
        {
            Entity = entity,
            RowNumber = rowNumber,
            Values = values.ToDictionary(v => v.Field, v => v.Value),
        };

    public static async IAsyncEnumerable<ExportRow> AsAsync(params ExportRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    /// <summary>Two orders shaped like <c>samples/orders/orders.json</c>: ORD-2001 (two lines) and ORD-2002 (one line, null country).</summary>
    public static ExportRow[] SampleOrders(ExportDefinition definition)
    {
        var order = definition.Root;
        var line = definition.Root.Children[0];

        var order1 = Row(order, 1, ("OrderNumber", "ORD-2001"), ("OrderDate", new DateTime(2026, 6, 10)), ("Country", "DK"), ("Total", 512.25m));
        order1.Children.Add(Row(line, 1, ("LineNumber", 1), ("Sku", "KB-201"), ("Quantity", 1)));
        order1.Children.Add(Row(line, 2, ("LineNumber", 2), ("Sku", "CH-770"), ("Quantity", 4)));

        var order2 = Row(order, 2, ("OrderNumber", "ORD-2002"), ("OrderDate", new DateTime(2026, 6, 11)), ("Country", null), ("Total", 42.00m));
        order2.Children.Add(Row(line, 1, ("LineNumber", 1), ("Sku", "MS-115"), ("Quantity", 1)));

        return [order1, order2];
    }
}
