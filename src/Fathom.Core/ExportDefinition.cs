namespace Fathom.Core;

/// <summary>
/// The single declarative definition of one export: the entity hierarchy (what to read and
/// from where), and the filters a client may supply to narrow it. Everything the engine and
/// the writers do is derived from this one document — author it by hand or generate it
/// alongside the equivalent Loadstone manifest for the same table, since the two describe
/// the same shape from opposite directions.
/// </summary>
public sealed class ExportDefinition
{
    public required string Name { get; set; }

    public string Version { get; set; } = "1";

    public string? Description { get; set; }

    /// <summary>The root entity. Children nest to any depth.</summary>
    public required EntityDefinition Root { get; set; }

    /// <summary>Filters a client may supply when running this export.</summary>
    public List<FilterDefinition> Filters { get; set; } = [];

    /// <summary>All entities in parent-before-child (breadth-first) order.</summary>
    public IEnumerable<EntityDefinition> EnumerateEntities()
    {
        var queue = new Queue<EntityDefinition>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var entity = queue.Dequeue();
            yield return entity;
            foreach (var child in entity.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    public EntityDefinition? FindEntity(string name) =>
        EnumerateEntities().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parent entity of <paramref name="entity"/>, or null for the root.</summary>
    public EntityDefinition? FindParent(EntityDefinition entity) =>
        EnumerateEntities().FirstOrDefault(e => e.Children.Contains(entity));

    /// <summary>
    /// Structural validation of the document itself. Returns every problem found so authors
    /// fix them all in one pass; an empty list means the definition is usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Export 'name' is required.");
        }

        var seenEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in EnumerateEntities())
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                errors.Add("Every entity requires a 'name'.");
                continue;
            }

            if (!seenEntities.Add(entity.Name))
            {
                errors.Add($"Entity '{entity.Name}': names must be unique within an export.");
            }

            if (string.IsNullOrWhiteSpace(entity.Table))
            {
                errors.Add($"Entity '{entity.Name}': 'table' is required.");
            }

            if (string.IsNullOrWhiteSpace(entity.KeyColumn))
            {
                errors.Add($"Entity '{entity.Name}': 'keyColumn' is required.");
            }

            var isRoot = ReferenceEquals(entity, Root);
            if (!isRoot && string.IsNullOrWhiteSpace(entity.ParentKeyColumn))
            {
                errors.Add($"Entity '{entity.Name}': 'parentKeyColumn' is required on every non-root entity.");
            }

            if (entity.Fields.Count == 0)
            {
                errors.Add($"Entity '{entity.Name}': at least one field is required.");
            }

            var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in entity.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name))
                {
                    errors.Add($"Entity '{entity.Name}': every field requires a 'name'.");
                }
                else if (!seenFields.Add(field.Name))
                {
                    errors.Add($"Entity '{entity.Name}': duplicate field '{field.Name}'.");
                }
            }
        }

        foreach (var filter in Filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Name))
            {
                errors.Add("Every filter requires a 'name'.");
                continue;
            }

            var entity = FindEntity(filter.Entity);
            if (entity is null)
            {
                errors.Add($"Filter '{filter.Name}': unknown entity '{filter.Entity}'.");
                continue;
            }

            if (entity.FindField(filter.Field) is null)
            {
                errors.Add($"Filter '{filter.Name}': field '{filter.Field}' does not exist on entity '{filter.Entity}'.");
            }

            if (filter.Operator == FilterOperator.Between && filter.ValueType is FieldType.String or FieldType.Boolean or FieldType.Guid)
            {
                errors.Add($"Filter '{filter.Name}': 'between' is not supported for value type '{filter.ValueType}'.");
            }
        }

        return errors;
    }
}

/// <summary>One level of the hierarchy: a table, its key, and the fields it exposes.</summary>
public sealed class EntityDefinition
{
    /// <summary>Logical name — the output field/section name (e.g. JSON property, CSV file name).</summary>
    public required string Name { get; set; }

    public required string Table { get; set; }

    public string Schema { get; set; } = "dbo";

    /// <summary>Primary key column on <see cref="Table"/>.</summary>
    public required string KeyColumn { get; set; }

    /// <summary>Foreign key column on <see cref="Table"/> referencing the parent's key. Required on every non-root entity.</summary>
    public string? ParentKeyColumn { get; set; }

    public List<FieldDefinition> Fields { get; set; } = [];

    public List<EntityDefinition> Children { get; set; } = [];

    public string QualifiedTable => $"[{Schema}].[{Table}]";

    public FieldDefinition? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public EntityDefinition? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A single output field: which column it reads, its type, and an optional output lookup.</summary>
public sealed class FieldDefinition
{
    /// <summary>Output name — the JSON property / XML element / CSV header this field is written as.</summary>
    public required string Name { get; set; }

    /// <summary>Source column name. Defaults to <see cref="Name"/> when omitted.</summary>
    public string? Column { get; set; }

    public string ColumnName => Column ?? Name;

    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>
    /// Name of a registered <see cref="Lookups.IExportLookupProvider"/> that transforms this
    /// field's raw database value into its output representation (e.g. a numeric species code
    /// into its FAO string) before it is written.
    /// </summary>
    public string? Lookup { get; set; }
}

public enum FieldType
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    DateTime,
    Date,
    Guid,
}
