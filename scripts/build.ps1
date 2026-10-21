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
Step "web install" "web"  { pnpm install }
Step "web build"   "web"  { pnpm build }
Step "web test"    "web"  { pnpm test }

Write-Host "All projects built." -ForegroundColor Green

