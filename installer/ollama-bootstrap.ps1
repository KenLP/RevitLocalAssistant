#Requires -Version 5.1
<#
.SYNOPSIS
    Check Ollama installation and pull the default model for RevitAssistant.
    Run once after installing the addin, or from the Inno Setup installer.

.PARAMETER Model
    Ollama model tag to pull. Default: qwen2.5:7b-instruct
#>
param(
    [string]$Model = "qwen2.5:7b-instruct"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) { Write-Host "[RevitAssistant] $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }

# ── 1. Check Ollama is installed ────────────────────────────────────────────
Write-Step "Checking Ollama..."

$ollamaPath = (Get-Command ollama -ErrorAction SilentlyContinue)?.Source
if (-not $ollamaPath) {
    Write-Warn "Ollama not found on PATH. Installing via winget..."
    winget install --id Ollama.Ollama --silent --accept-package-agreements --accept-source-agreements
    # Refresh PATH
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")
}

$ollamaVer = (ollama --version 2>&1) | Select-Object -First 1
Write-Ok "Ollama: $ollamaVer"

# ── 2. Ensure Ollama service is running ─────────────────────────────────────
Write-Step "Checking Ollama service..."
try {
    $health = Invoke-RestMethod -Uri "http://localhost:11434/" -TimeoutSec 3 -ErrorAction Stop
    Write-Ok "Ollama service is running."
} catch {
    Write-Step "Starting Ollama service..."
    Start-Process ollama -ArgumentList "serve" -WindowStyle Hidden
    Start-Sleep -Seconds 3
}

# ── 3. Pull the model ────────────────────────────────────────────────────────
Write-Step "Pulling model: $Model (this may take a while on first run)..."
ollama pull $Model
Write-Ok "Model ready: $Model"

Write-Host ""
Write-Ok "RevitAssistant setup complete. Restart Revit to activate the Assistant."
