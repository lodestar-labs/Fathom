using Fathom.Core;
using Fathom.Core.Pipeline;
using Fathom.SqlServer;

namespace Fathom.Tests;

/// <summary>
/// Exercises the N-way merge against in-memory cursors — the same contract the SQL cursors
/// honor: every level sorted by (ParentRowNumber, RowNumber), row numbers assigned in
/// (parent's row number, key) order. These shapes — deep chains, sibling entities, parents
/// with no children — are exactly where hand-rolled hierarchy assembly goes quietly wrong.
/// </summary>
[TestFixture]
public class HierarchyMergerTests
{
    private sealed class FakeCursor(params (long RowNumber, long ParentRowNumber, string Tag)[] rows) : ILevelCursor
    {
        private int _next;

        public bool HasCurrent { get; private set; }

        public long CurrentRowNumber { get; private set; }

        public long CurrentParentRowNumber { get; private set; }

        public IReadOnlyDictionary<string, object?> CurrentValues { get; private set; } = new Dictionary<string, object?>();

        public Task AdvanceAsync(CancellationToken cancellationToken)
        {
            if (_next >= rows.Length)
            {
                HasCurrent = false;
                return Task.CompletedTask;
            }

            var (rowNumber, parentRowNumber, tag) = rows[_next++];
            CurrentRowNumber = rowNumber;
            CurrentParentRowNumber = parentRowNumber;
            CurrentValues = new Dictionary<string, object?> { ["Tag"] = tag };
            HasCurrent = true;
            return Task.CompletedTask;
        }
    }

    private static Task<IReadOnlyDictionary<string, object?>> Identity(
        EntityDefinition entity, IReadOnlyDictionary<string, object?> raw, CancellationToken cancellationToken) =>
        Task.FromResult(raw);

    private static async Task<List<ExportRow>> MergeAsync(
        EntityDefinition root,
        Dictionary<string, ILevelCursor> cursors,
        HierarchyMerger.ValueTransform? transform = null)
    {
        foreach (var cursor in cursors.Values)
        {
            await cursor.AdvanceAsync(CancellationToken.None);
        }

        var results = new List<ExportRow>();
        await foreach (var row in HierarchyMerger.ReadLevelAsync(root, 0, cursors, transform ?? Identity, CancellationToken.None))
        {
            results.Add(row);
        }

        return results;
    }

    private static string Tag(ExportRow row) => (string)row.Values["Tag"]!;

    private static EntityDefinition Entity(string name, params EntityDefinition[] children) => new()
    {
        Name = name,
        Table = name,
        KeyColumn = "Id",
        ParentKeyColumn = "ParentId",
        Fields = [new FieldDefinition { Name = "Tag" }],
        Children = [.. children],
    };

    [Test]
    public async Task Depth_four_chain_reassembles_every_level_under_the_right_ancestor()
    {
        // A → B → C → D. Two As; A1 has two Bs, A2 has one. B row numbers are global and
        // dense in (parent, key) order — exactly what the staging SQL produces.
        var d = Entity("D");
        var c = Entity("C", d);
        var b = Entity("B", c);
        var a = Entity("A", b);
        a.ParentKeyColumn = null;

        var cursors = new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new FakeCursor((1, 0, "a1"), (2, 0, "a2")),
            ["B"] = new FakeCursor((1, 1, "b1"), (2, 1, "b2"), (3, 2, "b3")),
            ["C"] = new FakeCursor((1, 1, "c1"), (2, 3, "c2"), (3, 3, "c3")),
            ["D"] = new FakeCursor((1, 2, "d1")),
        };

        var roots = await MergeAsync(a, cursors);

        Assert.That(roots.Select(Tag), Is.EqualTo(new[] { "a1", "a2" }));

        var a1 = roots[0];
        var a2 = roots[1];
        Assert.Multiple(() =>
        {
            Assert.That(a1.Children.Select(Tag), Is.EqualTo(new[] { "b1", "b2" }));
            Assert.That(a2.Children.Select(Tag), Is.EqualTo(new[] { "b3" }));

            // c1 hangs under b1; c2 and c3 under b3 — crossing the a1/a2 boundary.
            Assert.That(a1.Children[0].Children.Select(Tag), Is.EqualTo(new[] { "c1" }));
            Assert.That(a1.Children[1].Children, Is.Empty);
            Assert.That(a2.Children[0].Children.Select(Tag), Is.EqualTo(new[] { "c2", "c3" }));

            // d1 hangs under c2 (the level-3 row numbered 2), four levels down.
            Assert.That(a2.Children[0].Children[0].Children.Select(Tag), Is.EqualTo(new[] { "d1" }));
            Assert.That(a2.Children[0].Children[1].Children, Is.Empty);
        });
    }

    [Test]
    public async Task Sibling_child_entities_each_consume_their_own_cursor()
    {
        var lines = Entity("Line");
        var shipments = Entity("Shipment");
        var order = Entity("Order", lines, shipments);
        order.ParentKeyColumn = null;

        var cursors = new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Order"] = new FakeCursor((1, 0, "o1"), (2, 0, "o2")),
            ["Line"] = new FakeCursor((1, 1, "l1"), (2, 2, "l2"), (3, 2, "l3")),
            ["Shipment"] = new FakeCursor((1, 2, "s1")),
        };

        var roots = await MergeAsync(order, cursors);

        Assert.Multiple(() =>
        {
            Assert.That(roots[0].Children.Select(Tag), Is.EqualTo(new[] { "l1" }), "o1: one line, no shipments");
            Assert.That(roots[1].Children.Select(Tag), Is.EqualTo(new[] { "l2", "l3", "s1" }), "o2: two lines then one shipment");
            Assert.That(roots[1].Children[2].Entity.Name, Is.EqualTo("Shipment"));
        });
    }

    [Test]
    public async Task Parents_without_children_are_skipped_over_without_stealing_later_siblings_children()
    {
        var line = Entity("Line");
        var order = Entity("Order", line);
        order.ParentKeyColumn = null;

        // Orders 1 and 3 have lines; order 2 has none. A naive merge that doesn't compare
        // parent row numbers would attach order 3's lines to order 2.
        var cursors = new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Order"] = new FakeCursor((1, 0, "o1"), (2, 0, "o2"), (3, 0, "o3")),
            ["Line"] = new FakeCursor((1, 1, "l1"), (2, 3, "l2")),
        };

        var roots = await MergeAsync(order, cursors);

        Assert.Multiple(() =>
        {
            Assert.That(roots[0].Children.Select(Tag), Is.EqualTo(new[] { "l1" }));
            Assert.That(roots[1].Children, Is.Empty);
            Assert.That(roots[2].Children.Select(Tag), Is.EqualTo(new[] { "l2" }));
        });
    }

    [Test]
    public async Task Empty_root_cursor_yields_nothing()
    {
        var order = Entity("Order");
        order.ParentKeyColumn = null;

        var roots = await MergeAsync(order, new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Order"] = new FakeCursor(),
        });

        Assert.That(roots, Is.Empty);
    }

    [Test]
    public async Task Transform_is_applied_to_every_row_at_every_level()
    {
        var line = Entity("Line");
        var order = Entity("Order", line);
        order.ParentKeyColumn = null;

        var cursors = new Dictionary<string, ILevelCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Order"] = new FakeCursor((1, 0, "o1")),
            ["Line"] = new FakeCursor((1, 1, "l1")),
        };

        var roots = await MergeAsync(order, cursors, (entity, raw, _) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["Tag"] = ((string)raw["Tag"]!).ToUpperInvariant() }));

        Assert.Multiple(() =>
        {
            Assert.That(Tag(roots[0]), Is.EqualTo("O1"));
            Assert.That(Tag(roots[0].Children[0]), Is.EqualTo("L1"));
        });
    }
}
