# One-command build + test for every project (FD-01). Run from anywhere.
$ErrorActionPreference = "Continue"  # native stderr noise is not failure; exit codes are checked in Step
$root = Split-Path $PSScriptRoot -Parent

function Step($name, $dir, [scriptblock]$cmd) {
    Write-Host "== $name ==" -ForegroundColor Cyan
    Push-Location (Join-Path $root $dir)
    try { & $cmd; if ($LASTEXITCODE -ne 0) { throw "$name failed" } } finally { Pop-Location }
}

Step "core build"  "core" { dotnet build UrbanFix.sln -nologo }
Step "core test"   "core" { dotnet test UrbanFix.sln -nologo --no-build }
Step "ai install"  "ai"   { python -m pip install -q -e ".[dev]" }
Step "ai lint"     "ai"   { python -m ruff check . }
Step "ai test"     "ai"   { python -m pytest -q }
Step "web install" "web"  { pnpm install }
Step "web build"   "web"  { pnpm build }

Write-Host "All projects built." -ForegroundColor Green

