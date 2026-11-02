# API and AI on Render (database and storage on Supabase)

Two Render web services: `urbanfix-ai` (Python) and `urbanfix-core` (.NET). The database and file storage are the Supabase project.

## Create the services

Render's Blueprint flow asked for a card in testing. Creating each service by hand with a **Free** instance may not; if it still asks, a card (even with $0 charged) is Render's requirement, not the project's.

### 1. AI service
Dashboard > **New > Web Service** > connect `shafiqblouch/UrbanFixAI`.
- Branch `wave-3-to-5-complete`, **Language: Docker**, **Dockerfile Path** `./ai/Dockerfile`, **Docker Build Context Directory** `./ai`.
- Instance type **Free**. Health Check Path `/health`.
- Environment: `AI_SERVICE_TOKEN` (long random string, save it) and `OPENROUTER_API_KEY` (a new key).

When live, `https://<name>.onrender.com/health` returns `{"status":"ok","service":"ai"}`.

### 2. Core API
New > Web Service, same repo and branch.
- **Dockerfile Path** `./core/Dockerfile`, **Docker Build Context Directory** `./core`. Instance **Free**. Health Check Path `/health/live`.
- Environment:

| Name | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` |
| `DATABASE_URL` | Supabase Session-pooler URL, password URL-encoded (`@` is `%40`, `#` is `%23`) |
| `Database__AppRolePassword` | new random letters and digits |
| `Database__MigrateOnStartup` | `true` |
| `Bootstrap__PlatformAdminEmail` | `admin@urbanfix.local` |
| `Bootstrap__PlatformAdminPassword` | new strong password |
| `Ai__BaseUrl` | the AI service URL from step 1 |
| `Ai__ServiceToken` | the same value as `AI_SERVICE_TOKEN` |
| `Storage__ServiceUrl` | `https://<project-ref>.storage.supabase.co/storage/v1/s3` |
| `Storage__Region` | `eu-central-1` |
| `Storage__AccessKey` / `Storage__SecretKey` | the Supabase S3 key pair |
| `Storage__Bucket` | `urbanfix-media` |

The first build takes 5 to 10 minutes. Then `https://<core>.onrender.com/health/ready` should return `{"status":"ready"}` (the first start creates the schema).

### Or use the Blueprint
`render.yaml` in the repo defines both services and prompts for the secret values: New > Blueprint, branch `wave-3-to-5-complete`.

## Mobile app
Set `EXPO_PUBLIC_API_URL` in `mobile/eas.json` to `https://<core>.onrender.com` and build the APK (see `docs/deploy-free.md`, "Mobile-only testing").

## Things to know
- **Free instances sleep after 15 minutes idle**; the first request then takes about a minute, and background jobs only run while awake. A sleeping API can make the app's sign-in look stuck once.
- **Free instances have 512 MB of RAM.** The .NET API fits for light testing; if it restarts with out-of-memory errors, use the Starter plan.
- **Demo sign-in is public** (Development mode, code `123456`): fake data only.
- **Build config:** the API is built in Debug because ImageSharp 4 needs a license key for Release builds; set `SIXLABORS_LICENSE_KEY` as an environment variable to change that.
- **Updates:** Render redeploys when you push to the branch.
