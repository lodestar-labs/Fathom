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
    /// Output names (export, entity, field, filter) travel everywhere a value name can go:
    /// XML element names, zip entry file names, URL route segments, query keys, definition
    /// file names, and the Content-Disposition download file name. Rather than let an exotic
    /// name blow up one of those mid-stream (an XML element may not contain a space; a zip
    /// entry must not contain a path separator), names are constrained at registration to a
    /// valid XML NCName: a letter or '_' first, then letters, digits, '_', '-', or '.'. The
    /// XML rule is the strictest of the destinations, so satisfying it satisfies all of them —
    /// and it is checked with the same <see cref="System.Xml.XmlConvert"/> the XML writer uses,
    /// so a Unicode letter that .NET accepts but XML rejects can't slip through and corrupt a
    /// response after it has started streaming.
    /// </summary>
    internal static bool IsValidName(string name) =>
        name.Length > 0
        && System.Xml.XmlConvert.IsStartNCNameChar(name[0])
        && name.All(System.Xml.XmlConvert.IsNCNameChar);

    private static string NameRuleError(string what, string name) =>
        $"{what} name '{name}' is invalid: names must be a valid XML name — a letter or '_' first, then letters, digits, '_', '-', or '.'.";

    /// <summary>
    /// A physical database identifier (schema, table, column) is safe to bracket-quote into
    /// generated SQL. Bracket-quoting with <c>]</c>-doubling already neutralizes injection
    /// completely; this is the belt to that suspenders — it rejects control characters (which
    /// have no place in an identifier and could enable log-injection or odd driver behavior)
    /// and enforces SQL Server's 128-character identifier limit, catching a malformed or
    /// hostile definition at registration instead of at the first export run.
    /// </summary>
    private static bool IsSafeSqlIdentifier(string identifier) =>
        identifier.Length is > 0 and <= 128
        && !identifier.Any(char.IsControl);

    private static string IdentifierRuleError(string what, string entity, string identifier) =>
        $"Entity '{entity}': {what} '{identifier}' is invalid — database identifiers must be 1–128 characters with no control characters.";

    /// <summary>
    /// Column names the engine synthesizes when staging and reading levels. A field with one
    /// of these output names would collide with its synthetic namesake in the generated
    /// SELECT lists (duplicate column in SELECT INTO is a SQL error) — rejected up front.
    /// </summary>
    private static readonly string[] ReservedFieldNames = ["RowNumber", "ParentRowNumber", "RealKey"];

    /// <summary>
    /// Upper bound on entities in one export. Each entity is its own staging round trip and
    /// its own open reader held for the export's lifetime, so an export with thousands of
    /// levels is a resource-exhaustion hazard, not a real hierarchy — reject it at
    /// registration. Comfortably above any genuine relational hierarchy.
    /// </summary>
    private const int MaxEntities = 100;

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
        else if (!IsValidName(Name))
        {
            errors.Add(NameRuleError("Export", Name));
        }

        var entityCount = EnumerateEntities().Count();
        if (entityCount > MaxEntities)
        {
            errors.Add($"Export has {entityCount} entities; the maximum is {MaxEntities}.");
        }

        var seenEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in EnumerateEntities())
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                errors.Add("Every entity requires a 'name'.");
                continue;
            }

            if (!IsValidName(entity.Name))
            {
                errors.Add(NameRuleError("Entity", entity.Name));
            }

            if (!seenEntities.Add(entity.Name))
            {
                errors.Add($"Entity '{entity.Name}': names must be unique within an export.");
            }

            if (string.IsNullOrWhiteSpace(entity.Table))
            {
                errors.Add($"Entity '{entity.Name}': 'table' is required.");
            }
            else if (!IsSafeSqlIdentifier(entity.Table))
            {
                errors.Add(IdentifierRuleError("table", entity.Name, entity.Table));
            }

            if (!string.IsNullOrEmpty(entity.Schema) && !IsSafeSqlIdentifier(entity.Schema))
            {
                errors.Add(IdentifierRuleError("schema", entity.Name, entity.Schema));
            }

            if (string.IsNullOrWhiteSpace(entity.KeyColumn))
            {
                errors.Add($"Entity '{entity.Name}': 'keyColumn' is required.");
            }
            else if (!IsSafeSqlIdentifier(entity.KeyColumn))
            {
                errors.Add(IdentifierRuleError("keyColumn", entity.Name, entity.KeyColumn));
            }

            var isRoot = ReferenceEquals(entity, Root);
            if (!isRoot && string.IsNullOrWhiteSpace(entity.ParentKeyColumn))
            {
                errors.Add($"Entity '{entity.Name}': 'parentKeyColumn' is required on every non-root entity.");
            }
            else if (entity.ParentKeyColumn is { } parentKey && !IsSafeSqlIdentifier(parentKey))
            {
                errors.Add(IdentifierRuleError("parentKeyColumn", entity.Name, parentKey));
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
                else if (!IsValidName(field.Name))
                {
                    errors.Add(NameRuleError($"Entity '{entity.Name}' field", field.Name));
                }
                else if (ReservedFieldNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Entity '{entity.Name}': field name '{field.Name}' is reserved (RowNumber, ParentRowNumber, and RealKey are used internally by the engine).");
                }

                if (field.Column is { } column && !IsSafeSqlIdentifier(column))
                {
                    errors.Add(IdentifierRuleError($"field '{field.Name}' column", entity.Name, column));
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

            if (!IsValidName(filter.Name))
            {
                errors.Add(NameRuleError("Filter", filter.Name));
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

    /// <summary>
    /// The bracket-quoted <c>[schema].[table]</c> the query engine reads from. Both parts are
    /// quoted with <c>]</c>-doubling — identical to how every column is quoted — so that even
    /// though schema/table come from the (admin-authored, lower-trust) definition rather than
    /// from a request, a name containing a <c>]</c> can never break out of the brackets into
    /// injectable SQL. Registration additionally rejects such names outright
    /// (see <see cref="Validate"/>), making this defense-in-depth.
    /// </summary>
    public string QualifiedTable => $"[{Schema.Replace("]", "]]")}].[{Table.Replace("]", "]]")}]";

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
