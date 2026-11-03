<#
.SYNOPSIS
  Redeploy the UrbanFix stack to the Linux server: pull latest branch, rebuild, restart.
.DESCRIPTION
  Runs over SSH. Prompts for the SSH password. Assumes the server user is in the `docker` group
  (so docker needs no sudo); if not, run once on the server: sudo usermod -aG docker $USER && newgrp docker
.EXAMPLE
  ./deploy/server/redeploy.ps1
.EXAMPLE
  ./deploy/server/redeploy.ps1 -ServerHost 46.250.243.215 -User shafiq -Branch wave-3-to-5-complete
#>
param(
  [string]$ServerHost = "46.250.243.215",
  [string]$User       = "shafiq",
  [string]$Branch     = "wave-3-to-5-complete"
)
$ErrorActionPreference = "Stop"

$remote = @"
set -e
cd ~/UrbanFixAI
echo '==> Pulling $Branch'
git fetch origin
git reset --hard origin/$Branch
git log -1 --oneline
cd deploy/server
echo '==> Building and restarting'
docker compose up -d --build
echo '==> Status'
docker compose ps
echo '==> Core health (expect: ready):'
sleep 5
curl -s http://localhost:443 >/dev/null 2>&1 || true
docker compose exec -T core sh -c 'wget -qO- http://localhost:8080/health/ready || true' 2>/dev/null || true
"@

Write-Host "Connecting to $User@$ServerHost ... enter the SSH password when prompted." -ForegroundColor Cyan
$remote | ssh -t "$User@$ServerHost" 'bash -s'
Write-Host "`nDone. Open https://urbanfix.vitonta.com" -ForegroundColor Green
