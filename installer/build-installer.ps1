<#
.SYNOPSIS
  Stages per-Revit-year build output and compiles the Inno Setup installer.

.DESCRIPTION
  For each supported Revit year this:
    1. builds RevitAssistant.Addin in Release for that year,
    2. stages the output under artifacts\installer\<year>\,
    3. writes a per-year .addin manifest whose <Assembly> points into the add-in's
       own subfolder (so its dependencies cannot collide with another add-in's),
    4. runs ISCC to produce artifacts\RevitAssistantSetup-<version>.exe,
    5. writes a SHA-256 checksum next to it.

  NOTE: this script and installer\inno\RevitAssistant.iss have never been executed
  end-to-end — Inno Setup was not available on the machine they were written on.
  Run them on a box with Inno Setup 6 and verify against installer\README.md before
  publishing anything.

.EXAMPLE
  .\installer\build-installer.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string[]]$RevitYears = @("2025", "2026"),
    [string]$IsccPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$addinProj = Join-Path $repoRoot "src\RevitAssistant.Addin\RevitAssistant.Addin.csproj"
$manifestSrc = Join-Path $repoRoot "src\RevitAssistant.Addin\RevitAssistant.addin"
$artifacts = Join-Path $repoRoot "artifacts"
$payload = Join-Path $artifacts "installer"

if (-not (Test-Path $addinProj)) { throw "Add-in project not found: $addinProj" }

if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

foreach ($year in $RevitYears) {
    Write-Host "== Building for Revit $year ==" -ForegroundColor Cyan

    # DeployToRevit=false: packaging must never touch the developer's live Revit install.
    dotnet build $addinProj -c Release -p:RevitVersion=$year -p:DeployToRevit=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $year" }

    $binDir = Join-Path $repoRoot "src\RevitAssistant.Addin\bin\Release"
    if (-not (Test-Path $binDir)) { throw "Build output not found: $binDir" }

    $yearDir = Join-Path $payload $year
    New-Item -ItemType Directory -Path $yearDir -Force | Out-Null

    # Ship runtime assemblies only — .pdb are debug symbols, not user payload.
    Copy-Item (Join-Path $binDir "*.dll") -Destination $yearDir -Force

    # Manifest lives beside the folder and points into it.
    $manifest = Get-Content $manifestSrc -Raw
    $manifest = $manifest -replace `
        '<Assembly>RevitAssistant\.dll</Assembly>', `
        '<Assembly>RevitAssistant\RevitAssistant.dll</Assembly>'
    Set-Content -Path (Join-Path $payload "$year.addin") -Value $manifest -Encoding UTF8

    $count = (Get-ChildItem $yearDir -Filter *.dll).Count
    Write-Host "   staged $count assemblies -> $yearDir"
}

if (-not (Test-Path $IsccPath)) {
    Write-Warning "Inno Setup compiler not found at '$IsccPath'."
    Write-Warning "Payload is staged under '$payload' but no installer was produced."
    Write-Warning "Install Inno Setup 6 (https://jrsoftware.org/isdl.php) and re-run."
    exit 2
}

Write-Host "== Compiling installer ==" -ForegroundColor Cyan
& $IsccPath "/DAppVersion=$Version" (Join-Path $PSScriptRoot "inno\RevitAssistant.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $artifacts "RevitAssistantSetup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected installer not found: $setup" }

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash  $(Split-Path $setup -Leaf)" | Set-Content "$setup.sha256" -Encoding ASCII

Write-Host ""
Write-Host "Installer: $setup" -ForegroundColor Green
Write-Host "SHA-256:   $hash" -ForegroundColor Green
