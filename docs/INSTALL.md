# Install Guide

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Revit | 2025 or 2026 | 2026 recommended |
| Ollama | 0.6+ | `winget install Ollama.Ollama` |
| Qwen2.5-7B model | — | `ollama pull qwen2.5:7b-instruct` |
| .NET 8 Runtime | 8.0+ | Bundled with Windows 11 / available from Microsoft |

---

## Quick install (developer)

```powershell
git clone --recurse-submodules https://github.com/KenLP/RevitAssistant
cd RevitAssistant
dotnet build -p:RevitVersion=2026   # auto-deploys to %APPDATA%\Autodesk\Revit\Addins\2026\
```

Restart Revit → the "Assistant" ribbon button appears in the Add-ins tab.

---

## End-user install (Phase 7)

Run `RevitAssistantSetup.exe` → wizard:
1. Detects installed Revit versions (2025/2026)
2. Copies addin files to `%APPDATA%\Autodesk\Revit\Addins\{version}\`
3. Optionally installs Ollama and pulls the default model (qwen2.5:7b-instruct)

Uninstall via Control Panel → Add/Remove Programs.

---

## Side-by-side with RevitMCPAddin

Both addins can be installed in the same Revit instance:
- `RevitMCPAddin` = Claude Desktop / online mode (port 7891)
- `RevitAssistant` = local Ollama / offline mode (no port, in-process)

They share no state and do not conflict.

---

## Ollama model options

| Model | VRAM | Speed | VI quality | Notes |
|---|---|---|---|---|
| `qwen2.5:7b-instruct` | ~5 GB | Fast | Good | **Default** |
| `qwen2.5:14b-instruct` | ~9 GB | Medium | Better | RTX A4000 8GB: tight, may spill to RAM |

Change default model in Settings panel (Phase 3).

---

## Disable for a specific Revit version

Delete or rename `RevitAssistant.addin` from:
`%APPDATA%\Autodesk\Revit\Addins\{version}\`
