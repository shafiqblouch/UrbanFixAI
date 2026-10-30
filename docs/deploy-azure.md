# Test deployment on one Azure VM

Everything (Postgres, file storage, AI service, API and an HTTPS proxy) runs in Docker Compose on one Ubuntu VM.
Files: `deploy/azure/docker-compose.yml`, `Caddyfile`, `.env.example`.

## 1. Create the VM (Azure portal)

1. **Create a resource > Virtual machine.**
   - Image: **Ubuntu Server 22.04 LTS**. Size: **Standard_B2s** (2 vCPU, 4 GB; the build needs about 3 GB, so do not go smaller; a B2ms is more comfortable).
   - Authentication: SSH public key (download the `.pem`), or a password if you prefer. Username e.g. `azureuser`.
   - Inbound ports: allow **SSH (22), HTTP (80), HTTPS (443)**.
2. After it is created, open **Networking > Add inbound port rule**: destination port **9000**, TCP (object storage), any source.
3. Open the VM's **Public IP address** resource > **Configuration**: set **Assignment: Static** and a **DNS name label** (e.g. `urbanfix-test`). The VM is then reachable at `urbanfix-test.<region>.cloudapp.azure.com`. This name is your `PUBLIC_HOST` (HTTPS certificates need a name, not a bare IP).

## 2. Install Docker on the VM

```
ssh azureuser@<public-host>
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && exit      # log in again so the group applies
```

## 3. Get the code and configure

```
git clone --branch wave-3-to-5-complete https://github.com/shafiqblouch/UrbanFixAI.git
cd UrbanFixAI/deploy/azure
cp .env.example .env
nano .env
```

Fill in `.env`: `PUBLIC_HOST`, and long random letters-and-digits values for `DB_PASSWORD`, `APP_ROLE_PASSWORD`, `ADMIN_PASSWORD`, `AI_SERVICE_TOKEN`, `STORAGE_SECRET_KEY`, plus a new `OPENROUTER_API_KEY`. (`openssl rand -hex 16` makes a good value.)

## 4. Start

```
docker compose up -d --build
docker compose logs -f core
```

The first build takes 5 to 15 minutes. On first start the API creates the database schema. Check:

- `https://<public-host>/health/ready` returns `{"status":"ready"}`.
- `https://<public-host>:9000/minio/health/live` answers (storage).

## 5. Point the mobile app at it

In `mobile/eas.json` set `EXPO_PUBLIC_API_URL` to `https://<public-host>`, then build the APK as in `docs/deploy-free.md` ("Mobile-only testing"). Sign in with a demo phone number and code `123456`.

## Things to know

- **Demo sign-in is public.** The API only allows fixed codes in Development mode, so anyone who finds the address can sign in. Fake data only; shut the VM down when you are not testing.
- **Cost control.** A free trial has a credit limit. **Stop (deallocate)** the VM in the portal when idle; a stopped VM does not bill compute, but a static public IP still costs a little.
- **Update the code:** `git pull && docker compose up -d --build` in `deploy/azure`.
- **Storage** is MinIO on the VM's disk: fine for testing, not durable. Back up before deleting the VM.
- **ImageSharp license:** the API is built in Debug (allowed for testing). Set `SIXLABORS_LICENSE_KEY` in `.env` for a Release build.
- **Push notifications** need Firebase credentials and are not configured.
- **If the certificate does not issue:** ports 80 and 443 must be open in the VM's network rules, and `PUBLIC_HOST` must match the DNS name exactly. See `docker compose logs caddy`.
