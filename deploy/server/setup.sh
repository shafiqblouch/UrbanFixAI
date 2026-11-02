#!/usr/bin/env bash
# One-shot bring-up for a fresh Ubuntu/Debian server. Run as root (or with sudo) from this folder:
#   sudo bash setup.sh
# It installs Docker if missing, checks that .env exists, opens the firewall, and starts the whole stack.
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "ERROR: .env not found. Copy .env.example to .env and fill it in first:"
  echo "  cp .env.example .env && nano .env"
  exit 1
fi

# --- Docker ---
if ! command -v docker >/dev/null 2>&1; then
  echo "==> Installing Docker Engine + Compose plugin"
  curl -fsSL https://get.docker.com | sh
fi
docker compose version >/dev/null 2>&1 || { echo "ERROR: docker compose plugin missing"; exit 1; }

# --- Firewall (only if ufw is active) ---
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  echo "==> Opening ports 80, 443, 9000 in ufw"
  ufw allow 80/tcp  || true
  ufw allow 443/tcp || true
  ufw allow 9000/tcp || true
fi

# --- Build & start ---
echo "==> Building and starting the stack (first run compiles the API and portal; be patient)"
docker compose up -d --build

echo
echo "==> Up. Watch the core API finish its database migration with:"
echo "    docker compose logs -f core"
PUBLIC_HOST=$(grep -E '^PUBLIC_HOST=' .env | cut -d= -f2-)
echo "==> Then open:  https://${PUBLIC_HOST}"
echo "    Health:     https://${PUBLIC_HOST}/api/health/ready   (should say ready)"
