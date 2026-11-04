# UrbanFix — Deploy & Mobile APK Runbook (single server)

Everything needed to (re)deploy the whole stack to the Linux server and to build a fresh Android APK that talks to it.

## 0. Coordinates

| Thing | Value |
|---|---|
| Server IP | `46.250.243.215` |
| SSH user | `shafiq` |
| Public hostname | `urbanfix.vitonta.com` |
| Repo on server | `~/UrbanFixAI` (branch `wave-3-to-5-complete`) |
| Stack folder | `~/UrbanFixAI/deploy/server` |
| Portal / API | `https://urbanfix.vitonta.com` |
| Media (photos/audio) | `https://urbanfix.vitonta.com/urbanfix-media/...` (same host, path → MinIO) |
| Mobile API URL (baked into APK) | `https://urbanfix.vitonta.com/api` |

This is a **shared** server: a host `nginx` owns port **80** (another app) and another container uses **5215**. Our stack therefore uses **443 only** (portal, API and media all share the one host — Caddy serves the SPA/API and routes the `/urbanfix-media/*` path to MinIO) — it never touches port 80, needs no custom media port, and needs no extra DNS record.

---

## 0b. TLS certificate — explicit DigiCert wildcard (NOT Let's Encrypt)

This provider's firewall blocks inbound 443 from Let's Encrypt's validators, so ACME (TLS-ALPN) can never issue a cert here. Instead Caddy serves a **DigiCert wildcard certificate for `*.vitonta.com`** supplied by the hosting/network team.

- Cert + key are mounted read-only into the caddy container from **`/home/shafiq/certs/`** (`fullchain.pem` = leaf+intermediate, `privkey.key`). The Caddyfile binds them with `tls /certs/fullchain.pem /certs/privkey.key` — there is no ACME.
- **Renewal (current cert expires 2027-03-06):** when the team issues a new cert, replace the two files in `/home/shafiq/certs/` and run `docker compose restart caddy`. If Caddy crash-loops with `failed to find any PEM data`, re-emit clean PEM: `openssl x509 -in leaf.crt -out leaf.pem && openssl x509 -in intermediate.crt -out int.pem && cat leaf.pem int.pem > fullchain.pem && openssl pkey -in team.key -out privkey.key` (ensures a newline between certs and a PKCS#8 key).

---

## 1. DNS configuration — REQUIRED (this is what makes HTTPS and the mobile app work)

Only one name is needed: `urbanfix`. Media is served from this same host under `/urbanfix-media/*`, so there is **no second DNS record**. The name must point **straight at the server** so the browser reaches our Caddy (which serves the DigiCert cert above). If the record is proxied through Cloudflare (orange cloud), you can get **Error 522** when the provider firewall blocks Cloudflare from reaching origin 443 — this is the single most common failure.

In the **Cloudflare dashboard → `vitonta.com` → DNS**, set exactly this record:

| Type | Name | Content | Proxy status | TTL |
|---|---|---|---|---|
| **A** | `urbanfix` | `46.250.243.215` | **DNS only (grey cloud)** | Auto |

And **delete** any `AAAA` (IPv6) record for `urbanfix` (the server is IPv4-only; a leftover proxied AAAA re-introduces the 522).

> Grey cloud is the documented, cert-safe setup. Because media now shares this host on 443, whatever proxy setting makes the portal work also makes media work — there is no longer a separate media host or `:9000` to open.

Verify from your PC (should print `46.250.243.215`, not a `104.x` Cloudflare address):

```powershell
nslookup urbanfix.vitonta.com 1.1.1.1
```

### Why the mobile APK depends on this DNS

- The APK is built with `EXPO_PUBLIC_API_URL=https://urbanfix.vitonta.com/api`. Android requires a **valid, trusted TLS certificate** on that host — which only exists once the DNS is grey-cloud and Caddy has issued the cert.
- Media (photos/voice notes) come back as signed links on `https://urbanfix.vitonta.com/urbanfix-media/...` (the same host). The phone and portal fetch those directly, so that host must resolve and serve real HTTPS — nothing extra beyond the one record above.
- Change the hostname later ⇒ you must **rebuild the APK** (the URL is compiled in).

---

## 2. First-time server deploy

Already done once, but to reproduce on a fresh box:

```bash
ssh shafiq@46.250.243.215
sudo apt-get update && sudo apt-get install -y git        # if git missing
git clone -b wave-3-to-5-complete https://github.com/shafiqblouch/UrbanFixAI.git
cd UrbanFixAI/deploy/server
cp .env.example .env && nano .env                          # fill PUBLIC_HOST + secrets
# one-time: let your user run docker without sudo
sudo usermod -aG docker $USER && newgrp docker
docker compose up -d --build
docker compose logs -f core                                # watch the DB migration finish
```

`.env` must have `PUBLIC_HOST=urbanfix.vitonta.com` and a real `OPENROUTER_API_KEY` (left blank = AI features off, everything else works).

---

## 3. Redeploy after a code change (run from Windows)

From this repo on your PC:

```powershell
./deploy/server/redeploy.ps1
```

It SSHes in, pulls the latest `wave-3-to-5-complete`, rebuilds, and restarts the stack. (It prompts for the SSH password.) Equivalent manual commands:

```bash
ssh shafiq@46.250.243.215
cd ~/UrbanFixAI && git fetch origin && git reset --hard origin/wave-3-to-5-complete
cd deploy/server && docker compose up -d --build && docker compose ps
```

> Pushing to GitHub does **not** auto-deploy. Run the redeploy to pull changes onto the server.

---

## 4. Build a fresh mobile APK (run from Windows)

Prerequisites: Node 20+, and a free account at https://expo.dev. The API URL is already set in `mobile/eas.json`.

```powershell
./mobile/build-apk.ps1
```

It installs the workspace deps, logs you into EAS (browser), and starts a cloud Android build with the `preview` profile. EAS returns a **download link + QR code** (build takes ~10–30 min on the free queue). Install the APK on any Android phone (allow "install unknown apps"). Equivalent manual commands:

```powershell
cd web ; corepack enable ; pnpm install --frozen-lockfile ; cd ..
cd mobile ; pnpm install ; npx eas-cli login ; npx eas-cli build --platform android --profile preview
```

Sign in on the phone with a demo account: any phone number / email and code **`123456`** (Development mode), or the admin email + password from `.env`.

> If you change `urbanfix.vitonta.com`, edit `EXPO_PUBLIC_API_URL` in `mobile/eas.json` and rebuild — the URL is compiled into the APK.

---

## 5. Verify a deployment

```powershell
# DNS points at the server (not Cloudflare) — check both names:
nslookup urbanfix.vitonta.com 1.1.1.1
# API is healthy (expects: ready):
curl.exe https://urbanfix.vitonta.com/api/health/ready
# Media path routes to MinIO (expects an XML error like AccessDenied/NoSuchKey, NOT the SPA HTML):
curl.exe https://urbanfix.vitonta.com/urbanfix-media/
# Portal loads:
start https://urbanfix.vitonta.com
```

On the server:

```bash
cd ~/UrbanFixAI/deploy/server
docker compose ps                     # all services "running"
docker compose logs --tail=50 caddy   # cert issued, no ACME errors
docker compose logs --tail=50 core    # migration done, "ready"
```

---

## 6. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| **Error 522** in browser | DNS still proxied (orange cloud) | Set the A record to **DNS only (grey cloud)**, delete AAAA (section 1) |
| HTTPS cert never issues / `caddy` logs ACME errors | Port 443 unreachable, or DNS not pointing at server | Confirm grey cloud; confirm nothing else holds 443; wait a minute for retry |
| Portal loads but API 502/timeout | `core` not healthy | `docker compose logs core` — usually the DB migration; see below |
| Migration fails on first start | DB role/extension perms | Check `core` logs; `postgres` owner creates the app role + `vector` ext automatically |
| Photos/audio broken in app or portal | Media path not routing to MinIO, or signed-URL host wrong | Check `curl https://urbanfix.vitonta.com/urbanfix-media/` returns MinIO XML (not SPA HTML); confirm `Storage__PublicUrl=https://urbanfix.vitonta.com` and the Caddy `/urbanfix-media/*` handler; `docker compose restart caddy` |
| `docker: permission denied` | user not in docker group | `sudo usermod -aG docker $USER && newgrp docker` |
| AI triage/vision/speech does nothing | `OPENROUTER_API_KEY` blank | Set it in `.env`, `docker compose up -d` |
| Build error `safety-terms.json not found` | old Dockerfile/context | Ensure `core.Dockerfile` + `context: ../..` (already fixed on this branch) |

### Day-2

- Logs: `docker compose logs -f <core|ai|web|caddy|postgres|minio>`
- Restart one: `docker compose restart core`
- DB backup: `docker compose exec postgres pg_dump -U urbanfix urbanfix > ~/backup-$(date +%F).sql`
- Data lives in volumes `pgdata` and `miniodata` — back these up.
