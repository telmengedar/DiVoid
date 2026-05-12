# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & run

This is a .NET 9 solution (`DiVoid.sln`) with two projects: `Backend` (ASP.NET Core Web API) and `Backend.tests` (NUnit).

```bash
dotnet build DiVoid.sln
dotnet run --project Backend                              # via launchSettings.json: http://localhost:5007
dotnet test Backend.tests/Backend.tests.csproj            # run all tests
dotnet test Backend.tests/Backend.tests.csproj --filter "FullyQualifiedName~Tests.Test1"   # run a single test
```

`Program.cs` overrides Kestrel to listen on port 80 with HTTP/1+2+3, but the dev profile in `Properties/launchSettings.json` uses port 5007. The dev profile wins under `dotnet run`.

The Pooshit packages (`Pooshit.AspNetCore.Services`, `Pooshit.Ocelot`, `Pooshit.Json`, `Pooshit.Http`) are public **preview** packages on nuget.org — vanilla `dotnet restore` works without any custom feed configuration. Restore failures usually mean a network/proxy issue or that the preview-version qualifier got dropped from a `<PackageReference>` line.

## Architecture

### Big picture

A simple "nodes and links" content store. `Node` records carry typed content (`ContentType` + `byte[] Content` + optional `float[] Embedding`) and are connected by undirected `NodeLink` edges. `NodeType` is a separate table joined into list/get queries so the API exposes types as strings while the DB stores them by id. `User` and `ApiKey` entities back two authentication schemes: `ApiKey` (service callers, CLI agents) and `JwtBearer` (Keycloak OIDC, upcoming web frontend). Both are registered in `Startup.ConfigureServices`; see `docs/architecture/auth-and-bootstrap.md` and `docs/architecture/keycloak-user-auth.md`.

**Key config value when `Auth:Enabled=true`:** `Keycloak:Audience` must be set to the Keycloak `DiVoid` client_id, or the service refuses to start (fail-closed). `DIVOID_KEY_PEPPER` must also be set (≥32 bytes) for the API-key path.

### Data layer (Pooshit.Ocelot)

The whole persistence layer is Ocelot, not EF Core. Key idioms to recognize before editing:

- `IEntityManager` is the singleton DI handle. Build operations with `database.Load<T>()`, `database.Insert<T>()`, `database.Update<T>()`, `database.Delete<T>()`, then `.ExecuteAsync` / `.ExecuteEntityAsync` / `.ExecuteScalarAsync<T>()`.
- Schema is created at startup by `Init/DatabaseModelService` (an `IHostedService`) calling `SchemaService.CreateOrUpdateSchema<T>` for each entity. **To add a new persisted entity, register it there** — there is no migrations folder.
- DB type is configured in `appsettings.json` under `Database:Type`. `"Sqlite"` uses `Database:Source` (file path). Anything else falls through to PostgreSQL using `Host/Port/Instance/User/Password`. See `Extensions/Startup/DatabaseExtensions.cs`.
- Default dev DB is `DiVoid.db3` in the `Backend/` directory and is committed-tracked locally — deleting it forces a fresh schema rebuild on next run.
- Entity classes use Ocelot attributes: `[PrimaryKey]`, `[AutoIncrement]`, `[Index("name")]`, `[Size(n)]`. Multiple `[Index]` attributes with the same name compose a composite index (see `Node.TypeId` + `Node.Name` both tagged `"node"`).

### Field mappers and DTO/entity split

There is a deliberate two-class pattern: `Node` is the DB entity, `NodeDetails` is the API DTO, and `NodeMapper : FieldMapper<NodeDetails, Node>` translates between them. Mappers also define `DefaultListFields` and build the canonical join (e.g., `NodeMapper.CreateOperation` aliases `node` and joins `NodeType` as `type`). When adding a new listable field, add the `FieldMapping` *and* update `DefaultListFields` if it should appear by default.

### Filtering, paging, patching

- List endpoints take a `*Filter : ListFilter` (paging/sorting) plus per-domain fields. `FilterExtensions.ApplyFilter` clamps `Count` to ≤500. The mapper-based overload resolves `Sort` by strict dictionary lookup against the mapper's registered keys — for `NodeMapper` those are `id`, `type`, `name`, `status`; any other value throws `KeyNotFoundException`. Always go through `ApplyFilter` instead of setting `Limit`/`Offset` manually.
- Wildcard handling is in `NodeService.GenerateFilter`: if any name contains `%` or `_` (see `FilterExtensions.ContainsWildcards`), the predicate switches from `IN (...)` to OR-chained `LIKE`. Mirror that pattern when adding new string-array filters.
- `PATCH` endpoints accept JSON Patch-style `PatchOperation[]` and route through `DatabasePatchExtensions.Patch`. Properties **must** be marked `[AllowPatch]` to be patchable; the extension throws `NotSupportedException` otherwise. Supported ops: `replace`, `add`, `remove`, `flag`, `unflag`, plus a custom `embed` op that calls a DB-side `embedding('gemini-embedding-001', value)` function (PostgreSQL-only — relies on a server-side embedding extension).

### MVC pipeline specifics

`Startup.cs` configures three things that are easy to overlook:

1. **`JsonStreamOutputFormatter` is inserted at index 0** of `OutputFormatters`. It only handles types implementing `IResponseWriter` (notably `AsyncPageResponseWriter<T>` returned from list endpoints) and streams JSON directly to the response body — list endpoints return that writer, *not* the entity collection. Don't `await` and materialize the page in the controller.
2. **`ArrayParameterBinderProvider` is inserted at index 0** of `ModelBinderProviders`. It enables `?id=1,2,3`, `?id=[1,2,3]`, `?id={1,2,3}`, and repeated `?id=1&id=2` for any array query parameter — this is what makes `NodeFilter.Id`, `Type`, `Name`, `LinkedTo` work from the URL.
3. `JsonStringEnumConverter` is registered globally, so enums serialize as strings in both directions.

Errors flow through `Pooshit.AspNetCore.Services` middleware: throwing `NotFoundException<T>` / `PropertyNotFoundException` / `InvalidOperationException` from a service produces the right HTTP status — don't catch and rethrow as `ProblemDetails`.

### Routing

Controllers live under `Controllers/` (root, e.g. `HealthController` at `api/health`) and `Controllers/V1/` (e.g. `NodeController` at `api/nodes`). Add new versioned controllers under `Controllers/V1/` and keep the `[Route("api/...")]` prefix.

## Code style

`.editorconfig` and `omnisharp.json` together enforce K&R braces (open brace on same line) and 4-space indentation for `.cs` files (the `UseTabs: true` in `omnisharp.json` is overridden by `.editorconfig`'s `indent_style = space`). `IDE0055` formatting violations are surfaced as warnings. `<Nullable>disable</Nullable>` in `Backend.csproj` — don't add `?` to reference types in this project (the test project is the opposite: `<Nullable>enable</Nullable>`).
