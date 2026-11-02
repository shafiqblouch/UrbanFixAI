# API and AI on Koyeb (database and storage on Supabase)

Two Koyeb web services built from the repo's Dockerfiles. The database and file storage are the Supabase project in `eu-central-1`, so pick Koyeb's **Frankfurt** region for the lowest latency.

## 1. AI service
Koyeb dashboard > **Create Service** > **GitHub** > `shafiqblouch/UrbanFixAI`.
- **Branch:** `wave-3-to-5-complete`. **Builder:** Dockerfile.
- **Work directory:** `ai`. **Dockerfile location:** `Dockerfile` (relative to the work directory).
- **Instance:** the free/smallest one. **Region:** Frankfurt.
- **Exposed port:** `8000`, HTTP. **Health check:** HTTP `/health` on port 8000.
- **Environment variables** (mark both as secrets): `AI_SERVICE_TOKEN` (long random string, save it) and `OPENROUTER_API_KEY` (a new key).
- Name it `urbanfix-ai`. When it is healthy, `https://<name>.koyeb.app/health` returns `{"status":"ok","service":"ai"}`.

## 2. Core API
Create Service > GitHub, same repo and branch.
- **Work directory:** `core`. **Dockerfile location:** `Dockerfile`. **Region:** Frankfurt.
- **Exposed port:** `8000`, HTTP. **Health check:** HTTP `/health/live` on port 8000.
- **Environment variables:**

| Name | Value |
|---|---|
| `PORT` | `8000` |
| `ASPNETCORE_ENVIRONMENT` | `Development` |
| `DATABASE_URL` | Supabase Session-pooler URL, password URL-encoded (`@` is `%40`, `#` is `%23`) (secret) |
| `Database__AppRolePassword` | new random letters and digits (secret) |
| `Database__MigrateOnStartup` | `true` |
| `Bootstrap__PlatformAdminEmail` | `admin@urbanfix.local` |
| `Bootstrap__PlatformAdminPassword` | new strong password (secret) |
| `Ai__BaseUrl` | `https://<ai-service>.koyeb.app` |
| `Ai__ServiceToken` | same value as `AI_SERVICE_TOKEN` (secret) |
| `Storage__ServiceUrl` | `https://roywrfgbsnjqsxkushic.storage.supabase.co/storage/v1/s3` |
| `Storage__Region` | `eu-central-1` |
| `Storage__AccessKey` / `Storage__SecretKey` | the Supabase S3 key pair (secrets) |
| `Storage__Bucket` | `urbanfix-media` |

The first build takes 5 to 10 minutes. Then `https://<core>.koyeb.app/health/ready` should return `{"status":"ready"}`; the first start creates the database schema.

## Mobile app
Set `EXPO_PUBLIC_API_URL` in `mobile/eas.json` to `https://<core>.koyeb.app`, then build the APK (see `docs/deploy-free.md`, "Mobile-only testing").

## Things to know
- **Memory:** the free instance is small (512 MB at the time of writing; check Koyeb's current plans). The .NET API may restart with out-of-memory errors; if so, move core to a larger instance.
- **Sleeping:** free services may scale to zero when idle, so the first request after a pause can take a while.
- **Card:** Koyeb's sign-up may still ask for payment details. I could not check its current policy.
- **Demo sign-in is public:** Development mode, code `123456`; fake data only.
- **Updates:** Koyeb redeploys on every push to the branch.
