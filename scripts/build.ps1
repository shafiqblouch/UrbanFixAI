# One-command build + test for every project (FD-01). Run from anywhere.
$ErrorActionPreference = "Continue"  # native stderr noise is not failure; exit codes are checked in Step
$root = Split-Path $PSScriptRoot -Parent

function Step($name, $dir, [scriptblock]$cmd) {
    Write-Host "== $name ==" -ForegroundColor Cyan
    Push-Location (Join-Path $root $dir)
    try { & $cmd; if ($LASTEXITCODE -ne 0) { throw "$name failed" } } finally { Pop-Location }
}

Step "core build"  "core" { dotnet build UrbanFix.sln -nologo }
# QA-01: every core module must keep >= 80% line coverage. Billing/ledger modules, when added, get
# their own 100% gate (separate test run with /p:Threshold=100 and /p:Include=[UrbanFix.Billing]*).
Step "core test"   "core" { dotnet test UrbanFix.sln -nologo /p:CollectCoverage=true "/p:Include=[UrbanFix.*]*" "/p:Exclude=[UrbanFix.Tests]*" /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=minimum }
Step "ai install"  "ai"   { python -m pip install -q -e ".[dev]" }
Step "ai lint"     "ai"   { python -m ruff check . }
Step "ai test"     "ai"   { python -m pytest -q --cov=app --cov-fail-under=80 }
# QA-04: replays the recorded model answers (free, deterministic). A model/prompt/case change fails here until `python -m eval --live` refreshes them.
Step "ai eval"     "ai"   { python -m eval }
Step "web install" "web"  { pnpm install }
Step "web build"   "web"  { pnpm build }
Step "web test"    "web"  { pnpm test }
# The app links the shared web packages, so it builds after `web install`. Metro bundling proves the whole app, shared packages included, compiles for Android.
Step "mobile install" "mobile" { pnpm install }
Step "mobile lint"    "mobile" { pnpm lint }
Step "mobile test"    "mobile" { pnpm test }
Step "mobile bundle"  "mobile" { pnpm exec expo export --platform android --output-dir dist }

Write-Host "All projects built." -ForegroundColor Green

