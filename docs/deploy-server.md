# Single-server deployment (everything on one Linux box)

The whole stack runs on one Linux server with Docker Compose: **portal (frontend), core API (.NET 9), AI API (Python), Postgres+pgvector, and MinIO object storage**, behind a **Caddy** reverse proxy that gets HTTPS certificates automatically.

```
                         ┌──────── your Linux server ────────┐
  phone / browser  ─────▶│  Caddy (:80/:443/:9000, HTTPS)     │
                         │    ├─▶ web (nginx: portal + /api)  │
                         │    │      └─▶ core (.NET API :8080)│
                         │    │             ├─▶ ai (:8000)    │
                         │    │             ├─▶ postgres      │
                         │    │             └─▶ minio         │
                         │    └─▶ minio (:9000, media links)  │
                         └───────────────────────────────────┘
```

Files live in [`deploy/server/`](../deploy/server/): `docker-compose.yml`, `Caddyfile`, `.env.example`, `setup.sh`.

## Requirements

- A Linux server (Ubuntu 22.04/24.04 or Debian 12 recommended), ~2 vCPU / 4 GB RAM minimum, 20 GB+ disk. The first build compiles the .NET API and the portal, so more RAM is faster.
- **Ports 80, 443, and 9000 open** to the internet (80/443 for the site and for Caddy's certificate challenge; 9000 for media links).
- **A hostname that resolves to the server's IP.** A real domain is best — add an `A` record pointing to the server. No domain yet? Use `<ip>.sslip.io` as the hostname (e.g. `203.0.113.9.sslip.io`); it resolves to that IP automatically and Caddy can still issue a valid certificate.
- Root/sudo and outbound HTTPS from the server (to pull images, clone the repo, reach OpenRouter and Let's Encrypt).

## Steps

Once you have SSH access to the server:

```bash
# 1. Get the code
sudo apt-get update && sudo apt-get install -y git
git clone -b wave-3-to-5-complete https://github.com/shafiqblouch/UrbanFixAI.git
cd UrbanFixAI/deploy/server

# 2. Create the settings file and fill it in
cp .env.example .env
nano .env            # set PUBLIC_HOST and the passwords/keys (see below)

# 3. Bring everything up (installs Docker if missing, opens the firewall, builds, starts)
sudo bash setup.sh
```

`setup.sh` runs `docker compose up -d --build`. The **core** container then runs the database migration and creates the admin account on first start — watch it finish with:

```bash
docker compose logs -f core
```

When it's ready:

- Portal: `https://<PUBLIC_HOST>/`
- API health: `https://<PUBLIC_HOST>/api/health/ready` → `ready`
- Sign in (Development mode): any `name@demo.test` / password `Demo-Pass-12345!` / code `123456`, or the admin email/password you set.

## What to put in `.env`

| Name | What it is |
|---|---|
| `PUBLIC_HOST` | The server's hostname (domain or `<ip>.sslip.io`). No `https://`, no port. |
| `DB_PASSWORD` | Postgres owner password (generate: `openssl rand -hex 24`). |
| `APP_ROLE_PASSWORD` | Password for the app's limited DB role. |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | First platform-admin login, created on startup. |
| `AI_SERVICE_TOKEN` | Shared secret between core and AI (any long random string). |
| `OPENROUTER_API_KEY` | OpenRouter key for triage/vision/speech. |
| `STORAGE_ACCESS_KEY` / `STORAGE_SECRET_KEY` | MinIO credentials (secret: `openssl rand -hex 24`). |
| `CORE_ENVIRONMENT` | `Development` (demo sign-in on) or `Production` (see below). |
| `SIXLABORS_LICENSE_KEY` | Optional; without it the API builds in Debug (allowed for testing). |

## Day-2 operations

- **Update to the latest code:** `git pull && docker compose up -d --build`
- **Logs:** `docker compose logs -f core` (or `ai`, `web`, `caddy`, `postgres`)
- **Stop / start:** `docker compose down` / `docker compose up -d`
- **Backups:** the data lives in named volumes `pgdata` (database) and `miniodata` (files). Back these up. DB dump: `docker compose exec postgres pg_dump -U urbanfix urbanfix > backup.sql`
- **Restart one service:** `docker compose restart core`

## Mobile app

The Android app bakes in the API address at build time. Point it at this server by setting the core host to `<PUBLIC_HOST>` in `mobile/eas.json`, then build with EAS (see [deploy-free.md](deploy-free.md) "Mobile-only testing"). Because `PUBLIC_HOST` serves valid HTTPS, the app works without extra trust configuration.

## Notes and warnings

- **Development mode is public demo access.** The fixed code `123456` works only with `CORE_ENVIRONMENT=Development`; anyone with the URL can sign in. Use fake data, or switch to Production.
- **Production mode** (`CORE_ENVIRONMENT=Production`) turns off the demo codes and requires a real SMS/TOTP sign-in path to be configured; without a provider wired up, no one can log in. Keep `Development` until that's set up.
- **Certificates:** Caddy needs ports 80+443 reachable and `PUBLIC_HOST` resolving to this server, or certificate issuance fails. Check `docker compose logs caddy` if HTTPS doesn't come up.
- **First build is slow** (compiles .NET + portal) and needs enough RAM; if the build gets OOM-killed, add swap or use a larger instance.
- **Media over HTTPS:** signed photo/audio links use `https://<PUBLIC_HOST>:9000`, which is why port 9000 must be open and proxied by Caddy.
```
