#Requires -Version 5.1
<#
.SYNOPSIS
    Quick dev/testing installer for RevitAssistant.
    Phase 7: superseded by the Inno Setup .exe installer.

.PARAMETER RevitVersion
    Target Revit version year. Default: 2026

.PARAMETER BinDir
    Source directory containing the compiled DLLs.
    Default: the project's Release build output.
#>
param(
    [string]$RevitVersion = "2026",
    [string]$BinDir = ""
)

$ErrorActionPreference = "Stop"
$AddinFolder = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"

if (-not $BinDir) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $BinDir = Join-Path $repoRoot "src\RevitAssistant.Addin\bin\Release"
}

Write-Host "[RevitAssistant] Installing to $AddinFolder"

if (-not (Test-Path $AddinFolder)) {
    New-Item -ItemType Directory -Path $AddinFolder -Force | Out-Null
}

# Copy DLLs
Get-ChildItem -Path $BinDir -Filter "*.dll" | ForEach-Object {
    Copy-Item $_.FullName -Destination $AddinFolder -Force
    Write-Host "  Copied: $($_.Name)"
}

# Copy .addin manifest
$addinSrc = Join-Path (Split-Path $PSScriptRoot -Parent) "src\RevitAssistant.Addin\RevitAssistant.addin"
Copy-Item $addinSrc -Destination $AddinFolder -Force
Write-Host "  Copied: RevitAssistant.addin"

# Copy compliance rules
$rulesDir = Join-Path $BinDir "Rules"
if (Test-Path $rulesDir) {
    $dest = Join-Path $AddinFolder "Rules"
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
    Copy-Item "$rulesDir\*" -Destination $dest -Recurse -Force
    Write-Host "  Copied: Rules/"
}

Write-Host "[RevitAssistant] Done. Restart Revit $RevitVersion to activate." -ForegroundColor Green
