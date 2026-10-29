# Deploying a test copy on Render

`render.yaml` at the repo root defines four pieces: Postgres, the AI service, the core API and the portal.
The portal (nginx) is the only address people visit; it serves the web app and proxies `/api` (REST and SignalR)
to the core API over Render's private network, so there is no CORS setup.

## Steps

1. Push the repo to GitHub (or GitLab). It has no remote yet: `git remote add origin <url>` then `git push -u origin wave-3-to-5-complete`.
2. Create a bucket on an S3-compatible store (Cloudflare R2 has a free tier; Render has no object storage). Note its endpoint URL, access key, secret key and bucket name.
3. Render dashboard: **New > Blueprint**, pick the repo and branch. Render asks for the values marked `sync: false`:
   - `OPENROUTER_API_KEY` (use a **new** key; the one pasted in chat earlier should be rotated).
   - `Storage__ServiceUrl`, `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket`.
   - `Ai__BaseUrl`: leave blank for now.
4. When `urbanfix-ai` is live, copy its URL (`https://urbanfix-ai-xxxx.onrender.com`) into `Ai__BaseUrl` on `urbanfix-core` and redeploy it.
5. Open the `urbanfix-portal` URL. Sign in with any `name@demo.test`, password `Demo-Pass-12345!`, code `123456`.
   Platform admin: `admin@urbanfix.local` with the generated `Bootstrap__PlatformAdminPassword` (shown in the core service's environment tab).

## Things to know

- **Demo sign-in is public.** The API only allows fixed codes when `ASPNETCORE_ENVIRONMENT=Development`, so the test deploy runs in that mode. Anyone with the URL can sign in as a demo user. Use fake data only, and do not share the URL widely. For anything real, switch the environment to `Production` and remove the demo codes, which also needs a real SMS sender for phone sign-in.
- **ImageSharp license.** Release builds need a Six Labors license key. Without `SIXLABORS_LICENSE_KEY` the core image builds in Debug, which the license allows for testing. Add the key as an environment variable on `urbanfix-core` for a Release build.
- **Free plans sleep.** Free web services spin down after 15 minutes idle (the first request takes about a minute) and background jobs only run while awake. Free Postgres expires after 30 days. Use the Starter plan on `urbanfix-core` if jobs must keep running.
- **Database roles.** On first start the migrator creates the `urbanfix_app` role and the `vector` extension using the Render database login. Check the core logs on the first deploy; if Render rejects `CREATE ROLE`, that is the cause.
- **Mobile app.** Not deployed here. Point Expo at the portal URL with `EXPO_PUBLIC_API_URL=https://<portal>/api`.
- **Vercel.** Possible for the portal alone, but it would still need a rewrite of `/api` to the core URL, and the other three services still need a host such as Render, so the blueprint keeps everything in one place.
