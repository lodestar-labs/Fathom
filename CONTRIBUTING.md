# Contributing to Fathom

Thanks for taking the time — contributions of every size are welcome, from typo fixes to
new database providers.

## Getting set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. `dotnet build` and `dotnet test` from the repository root should both be green before
   and after your change. The build treats warnings as errors in CI.
3. For end-to-end testing against a real database, `docker compose up` gives you SQL
   Server plus a running instance with the sample **orders** export registered.

## Understanding the codebase

Start with [docs/export-definition-reference.md](docs/export-definition-reference.md) for
the export definition spec, and the project layout table in [README.md](README.md) for
how the pieces fit together.

## Making changes

- Open an issue first for anything beyond a small fix, so we can agree on the approach
  before you invest time in it.
- Keep the layering intact: `Core` has no dependencies and defines the contracts
  (`ExportDefinition`, the lookup provider interfaces, `IExportWriter`); `SqlServer`
  implements the query engine against one database; `Writers` implements output formats
  against the writer contract; `Api` wires it all into an HTTP host. A new database
  provider or output format is a new implementation of an existing interface, in its own
  project if it brings its own dependencies.
- Add or update tests for the behavior you change. The suite is plain NUnit and runs
  without a database — SQL-touching logic (staging SQL, filter resolution) is tested by
  asserting on the generated statements and parameters, not by executing them.
- Match the existing code style; `.editorconfig` does most of the enforcing.

## Licensing of contributions

Fathom uses the Business Source License 1.1 with a commercial tier (see
[COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md)). By submitting a contribution you agree
it may be distributed under the project's current license and under commercial
licenses, and that the project may be relicensed in the future. You keep the copyright
to your work.

## Pull requests

- One logical change per PR.
- Describe what the change does and why; link the issue if there is one.
- CI (build with warnings-as-errors + tests) must pass.

## Reporting bugs

Include the export definition (or a trimmed version), the request that reproduces the
problem (route, query string, format), and — for a suspected data issue — a minimal
schema/data snippet. That's usually everything needed to reproduce an issue quickly.
