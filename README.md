# UrbanFix

| Folder | Contents |
| --- | --- |
| `core/` | .NET 9 modular monolith: `Api`, `Infrastructure` (EF Core, RLS migrations, outbox, Hangfire, S3 storage), `Identity`, `SharedKernel`, `Complaints` (empty), xUnit + Testcontainers tests |
| `ai/` | Python FastAPI AI service: versioned `/v1` API, service-token auth, provider abstraction, PII scrubber |
| `web/` | pnpm workspace: `apps/portal` (React + Vite), `packages/api-client` (generated from the OpenAPI contract), `packages/tokens` (design tokens), `packages/ui` (FD-08, placeholder) |
| `contracts/` | `openapi.json`, the committed API contract (a test fails if the API drifts from it) |
| `mobile/` | Empty until MA-00 picks the framework |
| `infra/` | Docker Compose dev stack: Postgres 16 + PostGIS + pgvector, Redis, MinIO-compatible storage, Jaeger |
| `scripts/` | `build.ps1` (build + test everything), `dev-up.ps1` (start the stack) |

```powershell
./scripts/dev-up.ps1   # Postgres :5432, Redis :6379, S3 :9000/:9001, Jaeger UI :16686
./scripts/build.ps1    # build, lint, test (with coverage gates) core, ai, web
```

Run CORE locally (Development migrates the database and creates `admin@urbanfix.local`, see `appsettings.Development.json`):

```powershell
dotnet run --project core/src/UrbanFix.Api
```

Tests need Docker (they start throwaway Postgres and S3 containers).

Changing the API: `$env:UPDATE_CONTRACT=1; dotnet test core --filter OperationsTests`, then `pnpm --filter @urbanfix/api-client generate`.

Dev credentials in `infra/docker-compose.yml` and `core/src/UrbanFix.Api/appsettings.json` are local-only; CORE refuses to start outside Development with the default database role password.
