# UrbanFix

| Folder | Contents |
| --- | --- |
| `core/` | .NET modular monolith (`UrbanFix.Api` + module libraries, xUnit tests) |
| `ai/` | Python FastAPI AI service |
| `web/` | pnpm workspace: `apps/portal` (React + Vite), `packages/ui` (shared UI, FD-08) |
| `mobile/` | Empty until MA-00 picks the framework |
| `infra/` | Docker Compose dev stack |
| `scripts/` | `build.ps1` (build + test everything), `dev-up.ps1` (start the stack) |

```powershell
./scripts/dev-up.ps1   # Postgres 16 + PostGIS + pgvector :5432, Redis :6379, MinIO :9000/:9001
./scripts/build.ps1    # build and test core, ai, web
```

Dev credentials in `infra/docker-compose.yml` are local-only.
