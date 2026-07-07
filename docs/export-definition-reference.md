---
title: Export definition reference
---

# Export definition reference

An export definition is a JSON document that fully describes one export: the entity
hierarchy, field types and lookups, and the filters a client may supply. Definitions are
validated on registration; an invalid one is rejected with every problem listed.

Definitions can be registered three ways, all equivalent:

- `PUT /api/exports/{name}` with the definition as the request body;
- a `.json` file in the definition directory (`Fathom:DefinitionDirectory`), loaded at startup;
- in code, by constructing an `ExportDefinition` and calling `IExportDefinitionRegistry.Register`.

Property names are camelCase and case-insensitive. Enum values are PascalCase strings
(`"Date"`, `"GreaterThanOrEqual"`, ...).

## Names

Output names — the export name, entity names, field names, and filter names — travel
everywhere a value name can go: XML element names, zip entry file names, URL route
segments, query-string keys, and the download file name. So names are constrained at
registration rather than allowed to break one of those formats mid-stream: **a letter or
underscore first, then letters, digits, `_`, `-`, or `.`**. Unicode letters are welcome
(`Præmie` is a fine field name); spaces, separators, and quotes are not. Physical
database identifiers (`table`, `schema`, `keyColumn`, `column`, ...) are *not* restricted —
they are always bracket-quoted in generated SQL and never appear in output.

Three field names are reserved for the engine's own staging columns: `RowNumber`,
`ParentRowNumber`, and `RealKey` (case-insensitive). A source *column* with one of those
names is fine — map it to a different output name with `column`.

## Top level

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Unique export name; the `{name}` in every `/api/exports/{name}` route. |
| `version` | string | `"1"` | Free-form version label. |
| `description` | string | – | Shown in the API. |
| `root` | entity | required | The root entity of the hierarchy. |
| `filters` | filter[] | `[]` | Filters a client may supply when running this export. |

## Entities

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Output name — the JSON property, XML element, or CSV file name. |
| `table` | string | required | Source table. |
| `schema` | string | `dbo` | Source schema. |
| `keyColumn` | string | required | Primary key column on `table`. Must be unique per row — a non-unique column fails at run time when the engine builds its unique staging index. Never exposed in output unless also mapped as an ordinary field. |
| `parentKeyColumn` | string | required on children | Foreign key column on `table` referencing the parent's `keyColumn`. |
| `fields` | field[] | required | At least one. |
| `children` | entity[] | `[]` | Nested entities, any depth. |

How hierarchy maps to output: each entity becomes its own JSON array / XML element type /
CSV file, nested under its parent. Rows are read parent-first and reassembled into the
tree by a streaming merge — memory stays bounded to one root subtree regardless of depth,
fan-out, or total row count.

## Fields

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Output name — the JSON property, XML element, or CSV header. |
| `column` | string | `name` | Source column, when it differs from the output name. |
| `type` | enum | `String` | `String`, `Int32`, `Int64`, `Decimal`, `Boolean`, `DateTime`, `Date`, `Guid`. Values render from their actual database types; declaring `Date` additionally renders the value as a plain `yyyy-MM-dd` date instead of a midnight timestamp (SQL Server surfaces `date` columns as timestamps). |
| `lookup` | string | – | Name of a registered export lookup provider that maps this field's raw value to its output form (e.g. a numeric code to a string). |

Binary (`varbinary`) values render as base64 in every format.

## Filters

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | The query-string key a client uses to supply this filter, e.g. `?country=DK`. |
| `entity` | string | required | Which entity's field this filter narrows. |
| `field` | string | required | Which field on that entity. |
| `operator` | enum | `Equals` | `Equals`, `In`, `GreaterThanOrEqual`, `LessThanOrEqual`, `Between`, `IsNull`, `IsNotNull`. |
| `valueType` | enum | `String` | The type client values are parsed as. `Between` is not supported for `String`, `Boolean`, or `Guid`. |
| `requestLookup` | string | – | Name of a registered request lookup provider that resolves the client's raw value (e.g. a country name) into the value the database stores (e.g. a numeric code) before it is bound as a SQL parameter. |
| `required` | bool | `false` | Requests missing this filter are rejected. |

Value counts by operator: `Equals` / `GreaterThanOrEqual` / `LessThanOrEqual` take exactly
one value; `Between` takes exactly two; `In` takes one or more; `IsNull` / `IsNotNull`
take none (any values supplied are ignored). Repeat the query key for multiple values —
`?orderNumber=A&orderNumber=B` is two values for one filter.

All filter values are always bound as SQL parameters, never concatenated into the
generated statement — this holds regardless of `operator`, `requestLookup`, or how the
raw string looks.

Filter binding is **strict**: a query key that is neither a declared filter nor `format`
is rejected with a 400 naming the unknown key and listing what the export supports. For a
data-export API, silently ignoring a typo'd filter name (`?countryy=DK`) would mean
serving the entire unfiltered table — a data-governance incident, not a convenience.

A filter on a child entity narrows which child rows are exported; it does not remove
parents whose children were all filtered away (they export with an empty child set).
Filters on the root narrow everything, since children are correlated to the exported
parents.

**Date bounds against `datetime` columns.** A `Date`-typed filter value is parsed to
midnight, so a `lessThanOrEqual` "to" bound of `2026-06-10` compares as
`<= 2026-06-10T00:00:00` and excludes rows on that day that carry a time component. If the
underlying column is `datetime`/`datetime2` rather than `date`, express an inclusive upper
bound as the next day with a strict operator, or add a day to the bound — Fathom parses the
value as declared and can't see the column's storage type.

## Lookups

Fathom resolves lookups in two independent directions, deliberately kept as two small
interfaces rather than one:

- **`IExportLookupProvider`** — output side. Given a raw value read from the database,
  returns its output representation, or `null` to pass the raw value through unchanged.
  Referenced from a field's `lookup`.
- **`IRequestLookupProvider`** — request side. Given a client-supplied raw value, returns
  the value the database actually stores, or throws `LookupResolutionException` if it
  can't. Referenced from a filter's `requestLookup`.

The same code list is commonly needed in both directions (render a country code as a
name on the way out, accept a country name as a filter on the way in), so the built-in
`CodeListLookupProvider` implements both interfaces from one cache — registering it once
under a name wires up both a field's `lookup` and a filter's `requestLookup` that share
that name.

### The built-in code-list provider

Register a code list against a `TblCode` / `TblCodeType`-shaped reference table (the same
convention Loadstone's code lists use, so one reference table serves both products):

```csharp
builder.Services.AddFathom()
    .UseSqlServer()
    .AddCodeListLookup("countries", codeType: "Country");
```

Table and column names are configurable for schemas that don't match the default
convention — either as named parameters, or from configuration:

```json
"Fathom": {
  "CodeListLookups": [
    { "name": "countries", "codeType": "Country" },
    { "name": "species", "codeType": "Species", "codeTable": "RefCode", "codeTypeTable": "RefCodeType" }
  ]
}
```

### Custom lookups

For anything structurally different — a REST API, another database, a static dictionary —
implement `IExportLookupProvider`, `IRequestLookupProvider`, or both, and register with
`AddExportLookup<T>()` / `AddRequestLookup<T>()`. Each is a single async method.

## Output formats

Requested with `?format=` on the run endpoint (`GET /api/exports/{name}/run`); default is
`json`.

**JSON** — a top-level array, streamed element by element. Each entity's children are
nested under a property named after the child entity.

**XML** — nested elements named after the export, then each entity in turn. A field with
a null value is written as an empty element with `nil="true"`.

**CSV** — a flat export (no children) writes a single CSV file. A hierarchical export
writes a **zip archive** with one `EntityName.csv` per entity, each row carrying a `_key`
column and, on every non-root entity, a `_parentKey` column referencing its parent's
`_key` — exactly the convention [Loadstone's](https://github.com/lodestar-labs/Loadstone)
own hierarchical CSV *import* expects, so a Fathom export of this shape can be fed
straight back into a Loadstone import with no transformation.

## Security

Endpoints require an authenticated request — Azure Entra ID via `Microsoft.Identity.Web`
by default (`Program.cs`, the `AzureAd` configuration section). This is a property of the
host, not the library: Fathom's endpoints only require a standard authenticated
`ClaimsPrincipal` through ASP.NET Core's ordinary `RequireAuthorization()`, with no
Entra-specific code anywhere past the `AddAuthentication()` call. Swap
`AddMicrosoftIdentityWebApi(...)` for any other ASP.NET Core authentication scheme to use
a different identity provider.

Authorization is currently coarse: any authenticated caller can both author and run
exports. Because authoring a definition lets a caller point at any table the SQL login can
read, **the SQL login must be least-privilege** — `db_datareader` on exactly the schema you
intend to expose, never `db_owner`. Fathom only ever issues parameterized reads.

Injection is closed on two layers. Filter *values* are always bound as `SqlParameter`s.
Identifiers (schema, table, column) come only from the definition, are bracket-quoted with
`]`-doubling, and are validated at registration (no control characters, 1–128 characters) —
so neither a request value nor a hostile identifier in a definition can reach executable
SQL. The full trust model, the anonymous-endpoint rationale, and the operational notes on
query-string logging and CSV-in-spreadsheets are in the README's **Security model** section.
