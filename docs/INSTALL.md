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
git clone --recurse-submodules https://github.com/KenLP/RevitLocalAssistant
cd RevitLocalAssistant
dotnet build -p:RevitVersion=2026   # auto-deploys to %APPDATA%\Autodesk\Revit\Addins\2026\
```

Restart Revit → the "Assistant" ribbon button appears in the Add-ins tab.

---

## End-user install (Phase 7) — NOT READY YET

> ⚠️ There is **no released installer**. The Inno Setup script and its build script
> exist (`installer/inno/RevitAssistant.iss`, `installer/build-installer.ps1`) but have
> never been compiled or installed on a clean machine — see
> [installer/README.md](../installer/README.md) for the acceptance checklist that must
> pass first. Until then, use the developer install above.

Once built, `RevitAssistantSetup-<version>.exe` will:
1. Detect installed Revit versions (2025/2026) and offer only those
2. Install the add-in into its own folder under
   `%APPDATA%\Autodesk\Revit\Addins\{version}\RevitAssistant\`, with the `.addin`
   manifest beside it — dependencies stay isolated from other add-ins
3. Refuse to run while Revit is open (the DLLs would be locked)

Ollama and the default model are **not** installed by the setup; run
`installer/ollama-bootstrap.ps1` separately.

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
