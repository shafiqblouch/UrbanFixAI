<#
.SYNOPSIS
  Build a fresh Android APK (EAS cloud build) that talks to the deployed server.
.DESCRIPTION
  The API URL is read from mobile/eas.json (preview profile -> EXPO_PUBLIC_API_URL),
  currently https://urbanfix.vitonta.com/api. To target a different host, edit that file first.
  Prerequisites: Node 20+, and a free account at https://expo.dev.
.EXAMPLE
  ./mobile/build-apk.ps1
#>
param(
  [switch]$SkipInstall   # skip the pnpm installs if deps are already in place
)
$ErrorActionPreference = "Stop"
$mobile = $PSScriptRoot
$web    = Join-Path (Split-Path $mobile -Parent) "web"

Write-Host "==> Enabling pnpm (corepack)" -ForegroundColor Cyan
corepack enable

if (-not $SkipInstall) {
  Write-Host "==> Installing web workspace deps (mobile links to web/packages/*)" -ForegroundColor Cyan
  Push-Location $web
  pnpm install --frozen-lockfile
  Pop-Location

  Write-Host "==> Installing mobile deps" -ForegroundColor Cyan
  Push-Location $mobile
  pnpm install
  Pop-Location
}

Push-Location $mobile
try {
  $apiUrl = (Get-Content eas.json -Raw | ConvertFrom-Json).build.preview.env.EXPO_PUBLIC_API_URL
  Write-Host "==> APK will target API: $apiUrl" -ForegroundColor Yellow
  Write-Host "==> Logging into EAS (a browser window may open)" -ForegroundColor Cyan
  npx eas-cli login
  Write-Host "==> Starting Android build (preview profile, APK). This runs in the cloud (~10-30 min)." -ForegroundColor Cyan
  npx eas-cli build --platform android --profile preview
  Write-Host "`n==> When it finishes, EAS prints a download link + QR. Install the APK on an Android phone." -ForegroundColor Green
}
finally {
  Pop-Location
}
