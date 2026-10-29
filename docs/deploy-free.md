# Free test deployment (no card): Supabase + Hugging Face Spaces + Vercel

| Piece | Where | Why |
|---|---|---|
| Postgres + file storage | Supabase | Free, has pgvector and an S3-compatible bucket |
| Core API (.NET) and AI service (Python) | Hugging Face Spaces (Docker) | Free, no card, 16 GB RAM |
| Portal | Vercel | Free, static site with an `/api` rewrite |

Everything below is done in the browser. Do the steps in order; each gives a value the next one needs.

## 1. Supabase (database and storage)

1. Create a project at supabase.com. Save the database password you choose.
2. **Connect** button > **Session pooler** > copy the connection string (`postgresql://postgres.<ref>:<password>@aws-...pooler.supabase.com:5432/postgres`). Use the session pooler: the direct host is IPv6 only. Put your password in; URL-encode special characters (`@` becomes `%40`).
3. **Storage** > New bucket named `urbanfix-media` (private). Then **Storage > S3 Connection**: note the endpoint URL and region, and create an access key pair.

## 2. Hugging Face: AI service

1. huggingface.co > New Space > SDK **Docker** > blank, name `urbanfix-ai`, visibility Public (private also works).
2. Upload the two files from `deploy/hf-ai/` (README.md and Dockerfile) with **Files > Add file**.
3. **Settings > Variables and secrets**, add secrets: `AI_SERVICE_TOKEN` (any long random string) and `OPENROUTER_API_KEY` (a new key).
4. Wait for it to build. Its URL is `https://<user>-urbanfix-ai.hf.space`; `/health` should answer.

## 3. Hugging Face: core API

1. New Space, Docker, name `urbanfix-core`. Upload the two files from `deploy/hf-core/`.
2. Add these as secrets/variables:

| Name | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` (turns on the demo sign-in, see warning below) |
| `DATABASE_URL` | the Supabase session-pooler string from step 1 |
| `Database__AppRolePassword` | a new random password (the app database role is created with it) |
| `Database__MigrateOnStartup` | `true` |
| `Bootstrap__PlatformAdminEmail` | `admin@urbanfix.local` |
| `Bootstrap__PlatformAdminPassword` | a new strong password |
| `Ai__BaseUrl` | the AI Space URL from step 2 |
| `Ai__ServiceToken` | the same value as the AI Space's `AI_SERVICE_TOKEN` |
| `Storage__ServiceUrl` | Supabase S3 endpoint |
| `Storage__Region` | the S3 region Supabase shows |
| `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket` | from step 1 |

3. Wait for the build. `https://<user>-urbanfix-core.hf.space/health/ready` should return `ready`.

## 4. Vercel: portal

1. Edit `web/vercel.json` and replace `https://urbanfix-core.onrender.com` with the core Space URL; push.
2. vercel.com/new > import `shafiqblouch/UrbanFixAI`, branch `wave-3-to-5-complete`, **Root Directory `web`**. Deploy.
3. Open the Vercel URL. Sign in with any `name@demo.test`, password `Demo-Pass-12345!`, code `123456`.

## Things to know

- **Demo sign-in is public.** Fixed codes only work with `ASPNETCORE_ENVIRONMENT=Development`; anyone with the URL can sign in. Fake data only.
- **Sleeping:** Spaces sleep after 48 hours idle and wake on the next request (a minute or two). Background jobs only run while awake.
- **If migration fails** on first start, read the core Space logs. The likely cause is the database login not being allowed to create the `urbanfix_app` role or the `vector` extension; Supabase's `postgres` user can do both.
- **Updating:** pushing to GitHub does not redeploy a Space. Use **Settings > Factory rebuild** on the Space to pull the latest code.
- **ImageSharp license:** the core builds in Debug (allowed for testing). A production Release build needs a Six Labors key.

## Mobile-only testing (API + AI + database, no portal)

Skip step 4 (Vercel). Do steps 1 to 3, then build an installable Android app that talks to the core Space:

1. In `mobile/eas.json`, replace `REPLACE-WITH-CORE-SPACE-URL.hf.space` with your core Space host, and push.
2. Make a free account at expo.dev, then from `web` run `pnpm install`, and from `mobile`: `pnpm install`, `npx eas-cli login`, `npx eas-cli build --platform android --profile preview`.
3. EAS builds in the cloud (a free-tier queue, often 10 to 30 minutes) and gives a link and QR code to the APK. Open it on any Android phone and install it (allow "install unknown apps").
4. Sign in with a demo phone number and code `123456` (Development mode accepts it).

Notes: iPhones need an Apple developer account for a standalone build, so use Android for this. Push notifications need Firebase (FCM) credentials and do not work in this build; everything else does. Because the API address is baked in at build time, rebuild if the Space URL changes.
