# Fathom

**Export data of any shape out of a relational database — declaratively.**

Fathom is an API-first export platform: the conceptual inverse of
[Loadstone](https://github.com/lodestar-labs/Loadstone). Where Loadstone brings data of any
shape *into* a database, Fathom brings it back *out* — as CSV, JSON, or XML, flat or
hierarchical, with reference-data codes rendered as readable strings, however deep the
schema or however many rows the result set holds. You describe an export once, in a small
JSON definition — its entity hierarchy, fields, and the filters clients may supply — and
Fathom turns any registered database into a governed, observable HTTP export endpoint.

[![CI](https://github.com/lodestar-labs/Fathom/actions/workflows/ci.yml/badge.svg)](https://github.com/lodestar-labs/Fathom/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/license-BSL%201.1-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

---

## Why Fathom

Every team eventually writes the same program: a one-off script that queries a table,
follows its foreign keys down to a few child tables, maps a handful of numeric codes back
to readable strings, and dumps the result as CSV for whoever asked for it. Then the next
report needs the same thing against a different table, with different codes, in JSON
instead. Fathom is that program, written once, generalized:

- **One definition instead of N export scripts.** Entities, fields, types, hierarchy, and
  filters — a single declarative JSON document drives the query, the merge, and the
  output for every format.
- **Hierarchies are first-class.** One table or ten, nested children to any depth. Every
  level streams into its own row-numbered staging table, then a single N-way merge reads
  them all back out in lockstep — reconstructing the full tree while never holding more
  than one root subtree in memory, however many rows the whole export contains.
- **Reference data renders both ways.** A numeric code in the database can render as a
  string on the way out (`Country: 45` → `"Denmark"`) and a client can filter by that same
  string on the way in (`?country=Denmark` resolves to `45` before it ever reaches SQL).
  The built-in code-list provider covers the common case; anything else — a REST API,
  another database, a static dictionary — is one small interface away.
- **Every value is parameterized, always.** Client-supplied filter values are bound as SQL
  parameters unconditionally, regardless of operator or lookup — table and column names
  come only from the trusted, admin-authored export definition, never from a request.
- **Security is pluggable.** Azure Entra ID today, via `Microsoft.Identity.Web` — but the
  endpoints themselves have no Entra-specific code. They require a standard authenticated
  principal through ASP.NET Core's ordinary authorization pipeline, so swapping in a
  different identity provider is replacing one `AddAuthentication()` call, not a rewrite.
- **Built for throughput.** MARS-backed streaming reads, `IAsyncEnumerable` end to end,
  writers that emit incrementally straight to the response — memory stays bounded and
  nothing waits for the full result set before the first byte goes out.
- **Observability is not an afterthought.** Structured logs (Serilog), traces and metrics
  (OpenTelemetry) — a span per export tagged with outcome and duration, counters for rows
  exported per entity — and split liveness/readiness health checks.
- **OpenAPI that's actually usable.** Native `Microsoft.AspNetCore.OpenApi` generation with
  a Bearer security scheme wired in, served through [Scalar](https://scalar.com/) — every
  endpoint is discoverable and callable from the docs UI, not just described by it.

## Quick start

```bash
git clone https://github.com/lodestar-labs/Fathom.git
cd Fathom
docker compose up --build
```

Open http://localhost:8080/scalar for the interactive API docs (the raw OpenAPI document
is at `/openapi/v1.json`), and http://localhost:8080/health for the combined health check.
Both work immediately, with no configuration — Fathom's own liveness/readiness probes and
generated documentation never require authentication.

The sample **orders** export is registered automatically (from
`samples/orders/orders.export.json`, against the same `Orders` / `OrderLines` schema
Loadstone's own sample dataset imports into — run Loadstone's quick start first and Fathom
has real data to export). Calling `/api/exports/orders/run` itself needs a bearer token
from a real Entra ID app registration — set `AzureAd__TenantId` and `AzureAd__ClientId` in
`docker-compose.yml` (or the environment) to yours, then:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     "http://localhost:8080/api/exports/orders/run?format=json&country=Denmark"
```

Running without Docker: install the [.NET 10 SDK](https://dotnet.microsoft.com/download),
point `Fathom:ConnectionString` (or `ConnectionStrings:Fathom`) at any SQL Server, set
`AzureAd:TenantId` / `AzureAd:ClientId`, and `dotnet run --project src/Fathom.Api`.

## Describing an export

A definition is the entire specification of one export. This is the sample shipped in
`samples/orders`:

```json
{
  "name": "orders",
  "description": "Customer orders with their lines",
  "root": {
    "name": "Order",
    "table": "Orders",
    "keyColumn": "OrderId",
    "fields": [
      { "name": "OrderNumber" },
      { "name": "OrderDate", "type": "Date" },
      { "name": "Country", "lookup": "countries" },
      { "name": "Total", "type": "Decimal" }
    ],
    "children": [
      {
        "name": "Line",
        "table": "OrderLines",
        "keyColumn": "LineId",
        "parentKeyColumn": "OrderId",
        "fields": [
          { "name": "LineNumber", "type": "Int32" },
          { "name": "Sku" },
          { "name": "Quantity", "type": "Int32" }
        ]
      }
    ]
  },
  "filters": [
    { "name": "country", "entity": "Order", "field": "Country", "requestLookup": "countries" },
    { "name": "orderDateFrom", "entity": "Order", "field": "OrderDate", "operator": "GreaterThanOrEqual", "valueType": "Date" },
    { "name": "orderDateTo", "entity": "Order", "field": "OrderDate", "operator": "LessThanOrEqual", "valueType": "Date" }
  ]
}
```

From this one document Fathom derives:

- the staging SQL for every entity (parents first), each streamed into its own
  row-numbered temp table;
- the N-way merge that reconstructs the `Order` → `Line` tree from those staged levels;
- how `Country` renders as a string on the way out, and how `?country=Denmark` resolves
  back to the stored code on the way in;
- validation of `orderDateFrom` / `orderDateTo` as dates, bound as SQL parameters;
- the shape of all three output formats — a zip of `Order.csv` + `Line.csv` for CSV, one
  JSON array with nested `Line` arrays, or nested XML elements.

Register or update a definition at runtime with `PUT /api/exports/{name}`, or drop JSON
files into `Fathom:DefinitionDirectory` and version them with your app. The full
specification lives in
[docs/export-definition-reference.md](docs/export-definition-reference.md).

## Extending Fathom

Both extension points are ordinary DI registrations:

```csharp
builder.Services.AddFathom()
    .UseSqlServer()
    .AddCodeListLookup("countries", codeType: "Country")   // built-in TblCode/TblCodeType provider
    .AddExportLookup<SpeciesApiLookup>()                   // custom IExportLookupProvider
    .AddRequestLookup<SpeciesApiLookup>();                 // the same type, both directions
```

An export lookup answers "what does this raw value mean, for output?"; a request lookup
answers "what does this client-supplied value mean, for a filter?" — most reference data
needs both directions, so implementing one type as both interfaces (as the built-in
code-list provider does) is the common case, but they're independent: a lookup that only
makes sense one way only needs to implement that one interface.

Authentication is a host concern, not a library one. `Program.cs` wires Entra ID:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
```

Replace that with any other ASP.NET Core authentication scheme to use a different
identity provider — nothing downstream (the endpoints, the query engine, the writers) has
any Entra-specific code; they only ever see a standard authenticated `ClaimsPrincipal`.

## Observability

- **Logs** — structured Serilog output; ship them anywhere Serilog can (console by
  default).
- **Traces & metrics** — an OpenTelemetry `ActivitySource` and `Meter` (both named
  `Fathom`): one span per export run tagged with the definition name and outcome
  (`success` / `cancelled` / `error`), counters for exports started/completed and rows
  exported per entity, and an export-duration histogram. Set `OTEL_EXPORTER_OTLP_ENDPOINT`
  and everything flows to your collector — Azure Monitor, Grafana, Jaeger, take your pick.
- **Health** — `/health/live` (process up; never depends on the database, so a DB blip
  can't restart your replicas) and `/health/ready` (database reachable; wire readiness
  here to gate traffic). Plain `/health` remains as the combined view.
- **The API itself** — native OpenAPI generation at `/openapi/v1.json`, with a Bearer
  security scheme attached to every operation, browsable and callable through
  [Scalar](https://scalar.com/) at `/scalar`.

## Running on Azure

Fathom is a single ASP.NET Core app, which is exactly the shape Azure App Service wants:

1. Register an app in Entra ID (**App registrations** → **New registration**), expose an
   API scope, and note the tenant and client IDs.
2. Create an App Service (or container app) from the provided Dockerfile, and an Azure SQL
   database.
3. Set `ConnectionStrings__Fathom` (Key Vault reference recommended) and
   `AzureAd__TenantId` / `AzureAd__ClientId` from step 1.
4. Point `Fathom__DefinitionDirectory` at the persistent `%HOME%\data` share if you author
   definitions as files rather than through `PUT /api/exports/{name}`.
5. Optional: set `OTEL_EXPORTER_OTLP_ENDPOINT` to light up Application Insights via the
   Azure Monitor OTLP ingestion.

No code changes — configuration is standard `IConfiguration` (environment variables, Key
Vault, App Configuration all work as-is).

**Memory, honestly.** The read side streams end to end for every format — one root
subtree in memory at a time, regardless of hierarchy depth or total row count. The
hierarchical CSV writer is the one exception worth knowing about: because a zip archive
can only have one entry open for writing at a time, but rows for every entity arrive
interleaved in a single pass, each entity streams to a temporary file first and the temp
files are copied into the archive sequentially once the source is exhausted. Memory stays
bounded either way; hierarchical CSV exports use bounded temp disk space in addition.

## Fathom and Loadstone together

Fathom's CSV export and Loadstone's CSV import use the same convention on purpose: a
hierarchical export writes a zip of `EntityName.csv` files, each row carrying a `_key`
column and child files referencing their parent through `_parentKey` — exactly what
Loadstone's hierarchical CSV import expects. Export a hierarchy from one database with
Fathom, import it into another with [Loadstone](https://github.com/lodestar-labs/Loadstone),
no transformation in between. JSON round-trips the same way: Fathom nests each entity's
children under a property named after the child entity, which is the shape Loadstone's
JSON import reads.

## Project layout

| Project | What it is |
| --- | --- |
| `Fathom.Core` | Contracts: the export-definition model, the dual lookup-provider interfaces, the writer interface, the definition registry. No dependencies. |
| `Fathom.SqlServer` | The query engine: row-numbered temp-table staging, the N-way streaming merge, filter resolution, the built-in code-list lookup. |
| `Fathom.Writers` | Streaming CSV, JSON, and XML writers. |
| `Fathom.Api` | The HTTP host: REST endpoints, Entra ID auth, native OpenAPI + Scalar, Serilog + OpenTelemetry, health checks. |
| `Fathom.Tests` | Unit tests for the definition model, SQL generation, filter resolution, and the writers. |

## Roadmap

- A streaming-phase idle-read timeout (`Fathom:ExportTimeout` currently bounds each
  individual SQL command's start, not a stalled client mid-stream)
- PostgreSQL provider behind the existing query-engine contract
- Per-export authorization policies (gate a specific export behind an Entra ID app role,
  not just "authenticated")
- Parquet writer
- Published benchmarks

Contributions toward any of these (or a good case for something else) are very welcome —
see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Fathom is source-available under the [Business Source License 1.1](LICENSE):
**free for self-hosted production use at any scale, commercial or not.** Each release
converts to Apache 2.0 four years after it ships. The two things that require a
[commercial license](COMMERCIAL-LICENSE.md) are offering Fathom as a hosted service
to third parties and embedding it in a product you sell. Enterprise support plans and
OEM licenses are available — see [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md).
