# UrbanFix

| Folder | Contents |
| --- | --- |
| `core/` | .NET 9 modular monolith: `Api` (endpoints, SignalR hub), `Infrastructure` (EF Core, RLS migrations, outbox, Hangfire, S3 storage, notifications), `Identity`, `SharedKernel`, `Complaints` (lifecycle, job workflow, routing, pricing rules), `Organization`; xUnit + Testcontainers tests |
| `ai/` | Python FastAPI AI service: versioned `/v1` API, service-token auth, provider abstraction, PII scrubber, and `eval/`, the evaluation harness (QA-04) |
| `web/` | pnpm workspace: `apps/portal` (React + Vite staff and resident portal), `packages/api-client` (generated from the OpenAPI contract), `packages/tokens` (design tokens), `packages/ui` (shared components, i18n and RTL; `@urbanfix/ui/core` is framework-free for mobile), `e2e/` (Playwright) |
| `contracts/` | `openapi.json`, the committed API contract (a test fails if the API drifts from it) |
| `mobile/` | React Native + Expo app for residents and technicians (see `docs/MA-00-mobile-framework.md`): resumable uploads, offline receipts, English/Urdu with RTL |
| `infra/` | Docker Compose dev stack: Postgres 16 + PostGIS + pgvector, Redis, MinIO-compatible storage, Jaeger |
| `scripts/` | `build.ps1` (build + test everything), `dev-up.ps1` (start the stack) |
| `docs/` | Decisions and status: `AI-01-model-selection.md`, `MA-00-mobile-framework.md`, `wave-3-status.md` (what is done, partial and not started) |

```powershell
./scripts/dev-up.ps1   # Postgres :5432, Redis :6379, S3 :9000/:9001, Jaeger UI :16686
./scripts/build.ps1    # build, lint, test (with coverage gates) core, ai (incl. the AI evaluation), web
```

## Run it locally

```powershell
dotnet run --project core/src/UrbanFix.Api --urls http://localhost:5181   # migrates the DB, creates admin@urbanfix.local (see appsettings.Development.json)
cd web; pnpm install; $env:VITE_API_URL="http://localhost:5181"; pnpm --filter @urbanfix/portal dev   # http://localhost:5173
```

The portal calls the API under `/api` on its own origin (Vite proxies it in dev and preview; put the same prefix on the reverse proxy in production), so there is no CORS to configure.

Sign in as the platform admin to create an organization and its manager, then add people with `POST /members`. Every account enrols an authenticator app at first sign-in.

## Tests

| What | Command | Needs |
| --- | --- | --- |
| Core | `dotnet test core` | Docker (throwaway Postgres and S3 containers) |
| Portal and UI | `cd web; pnpm test` | |
| Mobile logic and bundle | `cd mobile; pnpm test; pnpm bundle` | `pnpm install` in `web` first (shared packages are linked) |
| AI service | `cd ai; python -m pytest` | |
| AI evaluation | `cd ai; python -m eval` | replays recorded answers: free and deterministic |
| End to end | `cd web/e2e; pnpm e2e` | `./scripts/dev-up.ps1`, and Edge (set `PW_CHANNEL=chrome` or `chromium` to change) |

**Contract.** Changing the API: `$env:UPDATE_CONTRACT=1; dotnet test core --filter OperationsTests`, then `pnpm --filter @urbanfix/api-client generate`. Endpoints declare their response types with `.Produces<T>()`, which is what gives the portal typed responses.

**AI evaluation.** `python -m eval` fails (exit 2) when the model, a prompt or a test case changed, because the recorded answers no longer cover the run. Refresh them with `python -m eval --live` (needs `OPENROUTER_API_KEY`; about a cent), review `eval/reports/latest.md`, then commit `eval/cache/` and `eval/baseline.json`. Release thresholds are in `eval/thresholds.json`.

**End to end.** The Playwright suite starts the API and the built portal itself and runs the story complaint → review → estimate → assign → work with photos and costs → sign-off → resident confirms → audit, in English and Urdu. The development platform admin's authenticator secret is kept in `web/e2e/.state/` (git-ignored); if it is lost, reset the admin's second factor, see `web/e2e/tests/support.ts`.

Dev credentials in `infra/docker-compose.yml` and `core/src/UrbanFix.Api/appsettings.json` are local-only; CORE refuses to start outside Development with the default database role password.

AI service: copy `ai/.env.example` to `ai/.env` and set `OPENROUTER_API_KEY` plus `AI_SERVICE_TOKEN`; with a key present it uses Claude Haiku 5.5 (fallback DeepSeek v4.1 Flash), see `docs/AI-01-model-selection.md`. Run: `cd ai; python -m uvicorn app.main:app --env-file .env`.
