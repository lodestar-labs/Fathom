using Fathom.Core;
using Microsoft.Data.SqlClient;

namespace Fathom.SqlServer;

/// <summary>
/// Generates the two SQL shapes the export engine needs per entity: the staging statement
/// (stream the filtered, row-numbered slice of one table into a session-scoped temp table,
/// parent rows first) and the final read (stream that temp table back out in merge order).
///
/// The staging technique — assign every level's rows a dense <c>ROW_NUMBER()</c> ordered by
/// <c>(ParentRowNumber, RealKey)</c>, correlate child to parent by that synthetic number
/// rather than the real foreign key — means the final per-level reads can be recombined by a
/// simple forward-only merge (see <see cref="ExportQueryEngine"/>) instead of one large join,
/// and the real primary/foreign key values are never exposed in the output unless an author
/// explicitly maps them as ordinary fields.
/// </summary>
internal static class StagingSqlBuilder
{
    // A readable prefix for debugging, plus a deterministic hash of the full name so that two
    // distinct entity names can never collapse to the same temp table. Sanitize alone maps
    // both '-' and '.' to '_', so entities "A-B" and "A.B" — both valid and distinct — would
    // otherwise share #fathom_A_B and the second SELECT INTO would fail.
    public static string TempTableName(EntityDefinition entity) =>
        $"#fathom_{Sanitize(entity.Name)}_{StableHash(entity.Name):x16}";

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    /// <summary>
    /// FNV-1a 64-bit. Deterministic across processes (unlike <see cref="string.GetHashCode()"/>,
    /// which is randomized per run) so the name a parent's temp table is derived from matches
    /// between the staging phase and the read phase.
    /// </summary>
    private static ulong StableHash(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public static (string Sql, List<SqlParameter> Parameters) BuildStagingSql(
        EntityDefinition entity,
        EntityDefinition? parent,
        IReadOnlyList<ResolvedFilter> filtersForEntity)
    {
        var stageTable = TempTableName(entity);
        var outputColumns = string.Join(",\n  ", entity.Fields.Select(f =>
            $"{SqlIdentifier.Quote(f.ColumnName)} AS {SqlIdentifier.Quote(f.Name)}"));
        var indexName = SqlIdentifier.Quote($"IX_{Sanitize(entity.Name)}_RealKey");

        if (parent is null)
        {
            var (whereSql, parameters) = BuildWhereClause(entity, filtersForEntity, tableAlias: null);
            var sql = $"""
                SELECT
                  ROW_NUMBER() OVER (ORDER BY {SqlIdentifier.Quote(entity.KeyColumn)}) AS RowNumber,
                  CAST(0 AS bigint) AS ParentRowNumber,
                  {SqlIdentifier.Quote(entity.KeyColumn)} AS RealKey,
                  {outputColumns}
                INTO {stageTable}
                FROM {entity.QualifiedTable}
                {whereSql};
                CREATE UNIQUE CLUSTERED INDEX {indexName} ON {stageTable}(RealKey);
                """;
            return (sql, parameters);
        }
        else
        {
            var (whereSql, parameters) = BuildWhereClause(entity, filtersForEntity, tableAlias: "c");
            var parentStage = TempTableName(parent);
            var fkColumn = SqlIdentifier.Quote(entity.ParentKeyColumn!);
            var keyColumn = SqlIdentifier.Quote(entity.KeyColumn);
            var innerOutputColumns = string.Join(",\n      ", entity.Fields.Select(f =>
                $"c.{SqlIdentifier.Quote(f.ColumnName)} AS {SqlIdentifier.Quote(f.Name)}"));
            // The inner subquery has already renamed every source column to its field name, so
            // the outer list must select by field name only — reusing outputColumns here would
            // reference source column names that no longer exist inside `staged`.
            var stagedColumns = string.Join(",\n  ", entity.Fields.Select(f => SqlIdentifier.Quote(f.Name)));

            var sql = $"""
                SELECT
                  ROW_NUMBER() OVER (ORDER BY ParentRowNumber, RealKey) AS RowNumber,
                  ParentRowNumber,
                  RealKey,
                  {stagedColumns}
                INTO {stageTable}
                FROM
                (
                  SELECT
                    p.RowNumber AS ParentRowNumber,
                    c.{keyColumn} AS RealKey,
                    {innerOutputColumns}
                  FROM {entity.QualifiedTable} AS c
                  INNER JOIN {parentStage} AS p ON c.{fkColumn} = p.RealKey
                  {whereSql}
                ) AS staged;
                CREATE UNIQUE CLUSTERED INDEX {indexName} ON {stageTable}(RealKey);
                """;
            return (sql, parameters);
        }
    }

    /// <summary>
    /// The zero-staging read for a flat export (root with no children): one direct SELECT
    /// against the source table, in the exact column shape the cursor expects (RowNumber,
    /// ParentRowNumber, fields). Staging exists to give children a parent to correlate with —
    /// with no children, a round trip through tempdb would be pure overhead, roughly doubling
    /// the I/O of the export.
    /// </summary>
    public static (string Sql, List<SqlParameter> Parameters) BuildDirectSelectSql(
        EntityDefinition entity,
        IReadOnlyList<ResolvedFilter> filters)
    {
        var (whereSql, parameters) = BuildWhereClause(entity, filters, tableAlias: null);
        var outputColumns = string.Join(",\n  ", entity.Fields.Select(f =>
            $"{SqlIdentifier.Quote(f.ColumnName)} AS {SqlIdentifier.Quote(f.Name)}"));
        var sql = $"""
            SELECT
              ROW_NUMBER() OVER (ORDER BY {SqlIdentifier.Quote(entity.KeyColumn)}) AS RowNumber,
              CAST(0 AS bigint) AS ParentRowNumber,
              {outputColumns}
            FROM {entity.QualifiedTable}
            {whereSql}
            ORDER BY RowNumber;
            """;
        return (sql, parameters);
    }

    /// <summary>The final, merge-ordered read of one entity's staged rows — RowNumber and ParentRowNumber first, then fields in declaration order.</summary>
    public static string BuildFinalSelectSql(EntityDefinition entity)
    {
        var stage = TempTableName(entity);
        var columns = string.Join(", ", entity.Fields.Select(f => SqlIdentifier.Quote(f.Name)));
        var isRoot = entity.ParentKeyColumn is null;
        var orderBy = isRoot ? "RowNumber" : "ParentRowNumber, RowNumber";
        var selectList = entity.Fields.Count == 0 ? "RowNumber, ParentRowNumber" : $"RowNumber, ParentRowNumber, {columns}";
        return $"SELECT {selectList} FROM {stage} ORDER BY {orderBy};";
    }

    internal static (string WhereSql, List<SqlParameter> Parameters) BuildWhereClause(
        EntityDefinition entity,
        IReadOnlyList<ResolvedFilter> filters,
        string? tableAlias)
    {
        var prefix = tableAlias is null ? string.Empty : $"{tableAlias}.";
        var conditions = new List<string>();
        var parameters = new List<SqlParameter>();
        var paramIndex = 0;

        string NextParamName() => $"@p_{Sanitize(entity.Name)}_{paramIndex++}";

        foreach (var filter in filters)
        {
            var field = entity.FindField(filter.Definition.Field)
                ?? throw new InvalidOperationException(
                    $"Filter '{filter.Definition.Name}' targets unknown field '{filter.Definition.Field}' on entity '{entity.Name}'.");
            var column = $"{prefix}{SqlIdentifier.Quote(field.ColumnName)}";

            switch (filter.Definition.Operator)
            {
                case FilterOperator.Equals:
                {
                    var p = NextParamName();
                    parameters.Add(new SqlParameter(p, filter.Values[0]));
                    conditions.Add($"{column} = {p}");
                    break;
                }
                case FilterOperator.In:
                {
                    var names = new List<string>();
                    foreach (var value in filter.Values)
                    {
                        var p = NextParamName();
                        parameters.Add(new SqlParameter(p, value));
                        names.Add(p);
                    }

                    conditions.Add($"{column} IN ({string.Join(", ", names)})");
                    break;
                }
                case FilterOperator.GreaterThanOrEqual:
                {
                    var p = NextParamName();
                    parameters.Add(new SqlParameter(p, filter.Values[0]));
                    conditions.Add($"{column} >= {p}");
                    break;
                }
                case FilterOperator.LessThanOrEqual:
                {
                    var p = NextParamName();
                    parameters.Add(new SqlParameter(p, filter.Values[0]));
                    conditions.Add($"{column} <= {p}");
                    break;
                }
                case FilterOperator.Between:
                {
                    var p1 = NextParamName();
                    parameters.Add(new SqlParameter(p1, filter.Values[0]));
                    var p2 = NextParamName();
                    parameters.Add(new SqlParameter(p2, filter.Values[1]));
                    conditions.Add($"{column} BETWEEN {p1} AND {p2}");
                    break;
                }
                case FilterOperator.IsNull:
                    conditions.Add($"{column} IS NULL");
                    break;
                case FilterOperator.IsNotNull:
                    conditions.Add($"{column} IS NOT NULL");
                    break;
            }
        }

        var sql = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
        return (sql, parameters);
    }
}
