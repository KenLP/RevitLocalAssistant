# RevitLocalAssistant — "Trợ lý Revit AI Local"

A Revit add-in chat assistant that runs **entirely offline** on a local LLM (Ollama). Chat in
**Vietnamese or English**, query live model data, run **compliance checks**, and perform
**bulk parameter edits** with a dry-run preview — no cloud API, no per-seat licence, no data
leaving the machine.

It reuses the mature **RevitMCPServer** service layer (Revit API access, transactions,
validation) as a git submodule, and adds an in-process tool-spec adapter, an Ollama
orchestrator loop, a WPF chat panel, a VI↔EN BIM glossary, a compliance rule engine, and a
packaged installer on top of it.

---

## Why

LLMs are reliable at *understanding intent*, unreliable at *exact set operations*. Early
testing showed the model itself choosing which elements match and hand-building filter/set
calls — which silently wrote the wrong parameter on the wrong elements when a filter
parameter turned out to live on the Type instead of the instance (see
[RELIABLE_EXECUTION_HANDOFF.md](RELIABLE_EXECUTION_HANDOFF.md)).

The fix: move the LLM **out of** the execution loop and confine it to a one-time "compile"
step —

```
Natural language → LLM picks a tool + args (query_where / update_where / …)
                  → Revit deterministically resolves scope (instance vs type),
                    matches elements, and — for writes — dry-runs + shows a preview
                  → user confirms
                  → Revit executes in one transaction and reads back every write to verify
```

The LLM never hand-builds an element-ID list, never counts or sums by eye, and never guesses
whether a parameter lives on the instance or the type.

---

## Features

- **Bilingual chat (VI/EN)** — always answers in Vietnamese; understands both.
- **Deterministic query/edit** — `query_where` / `update_where` resolve instance-vs-type
  parameter scope automatically, filter/match in Revit (not by the LLM), and verify every
  write by reading it back before committing.
- **Exact counting & aggregation** — `count_elements` / `aggregate_elements` compute sums,
  min/max, averages, and group-by breakdowns in Revit; the model reports the numbers
  verbatim instead of adding them up itself.
- **Compliance rule engine** — YAML rule packs (`assertion` syntax over Revit parameters)
  evaluated against the live model; see [docs/COMPLIANCE_RULES.md](docs/COMPLIANCE_RULES.md).
- **Spatial / QC geometry** — door width + location, room boundary polylines (finish face,
  metres), vertical headroom raycasts, and detail-line markup for visual QC.
- **CSV / XLSX import** — map a spreadsheet to Revit parameters or generate sheets, with a
  dry-run preview before anything is written.
- **Live model grounding** — the system prompt is injected with the project's actual
  categories, parameters, levels, and the active view, so the model never guesses names.
- **Fully offline** — talks to a local Ollama instance; no cloud calls, no telemetry.
- **Runs alongside `RevitMCPAddin`** — different Client IDs, no port conflict; the assistant
  itself opens no HTTP port (in-process dispatch only).

---

## Project layout

```
RevitAssistant.slnx
├── src/
│   ├── RevitAssistant.Core        Thin wrapper — ProjectReferences the RevitMCP.Core
│   │                               classlib from the extern/RevitMCPCore submodule
│   │                               (CommandRegistry, ~86 commands, ExternalEventHandler,
│   │                               transaction/validation/unit-conversion policy)
│   ├── RevitAssistant.Addin       IExternalApplication entry point, ribbon button,
│   │                               DockablePane registration
│   ├── RevitAssistant.UI          WPF MVVM chat panel (net8.0-windows, CommunityToolkit.Mvvm)
│   ├── RevitAssistant.Llm         Ollama orchestrator — OllamaClient, ToolSpecAdapter
│   │                               (curated ~32-tool surface), AssistantPrompt, BimGlossary,
│   │                               IntentParser (no Revit dependency; unit-testable)
│   ├── RevitAssistant.Compliance  YAML rule engine → findings (no Revit dependency)
│   └── RevitAssistant.Schema      Live model schema export (categories + params → JSON)
├── tests/                          xUnit, no Revit dependency (Llm, UI, Compliance)
├── extern/RevitMCPCore/            git submodule — the vendored Revit service layer
├── installer/                      Ollama bootstrap + install scripts
└── docs/                           Architecture, install guide, compliance rule format,
                                     VI BIM glossary
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for threading invariants and the install
layout, and [PLAN.md](PLAN.md) for the full design rationale and phase history.

---

## Requirements

| Requirement | Version | Notes |
|---|---|---|
| Revit | 2025, 2026, or 2027 | 2026 is the primary target; 2027 uses `net10.0-windows` |
| Ollama | 0.6+ | `winget install Ollama.Ollama` |
| A local model | — | Default: `ollama pull qwen2.5:7b-instruct` |
| .NET SDK | 8.0+ and 10.0+ | Needed to build both Revit-version target frameworks |

---

## Quick start (developer)

```powershell
git clone --recurse-submodules https://github.com/KenLP/RevitLocalAssistant
cd RevitLocalAssistant
dotnet build -p:RevitVersion=2026   # auto-deploys to %APPDATA%\Autodesk\Revit\Addins\2026\
```

Restart Revit → the assistant's ribbon button appears in the Add-ins tab. Full instructions
(other Revit versions, end-user installer, running side-by-side with `RevitMCPAddin`) are in
[docs/INSTALL.md](docs/INSTALL.md).

## Testing

```powershell
dotnet test tests/RevitAssistant.Llm.Tests/RevitAssistant.Llm.Tests.csproj
dotnet test tests/RevitAssistant.UI.Tests/RevitAssistant.UI.Tests.csproj
dotnet test tests/RevitAssistant.Compliance.Tests/RevitAssistant.Compliance.Tests.csproj
```

All three test projects are pure logic (no Revit dependency) and run on plain `dotnet test`.
CI (`.github/workflows/ci.yml`) builds and tests against both Revit 2025 and 2026 target
frameworks on every push.

---

## Docs

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — project map, threading invariants, install layout
- [docs/INSTALL.md](docs/INSTALL.md) — prerequisites, dev + end-user install, model options
- [docs/COMPLIANCE_RULES.md](docs/COMPLIANCE_RULES.md) — YAML rule format + assertion syntax
- [docs/VI_BIM_GLOSSARY.md](docs/VI_BIM_GLOSSARY.md) — Vietnamese↔English BIM term glossary
- [RELIABLE_EXECUTION_HANDOFF.md](RELIABLE_EXECUTION_HANDOFF.md) — why the LLM stays out of
  the execution loop, and how it's enforced
- [PLAN.md](PLAN.md) — full build plan, decisions log, phase history
- [CHANGELOG.md](CHANGELOG.md) — notable changes by date

---

## Status

Actively developed. The submodule at `extern/RevitMCPCore` is currently pinned to a specific
commit (see [PLAN.md](PLAN.md) for the exact hash) rather than tracking upstream `main`, so
that spatial-QC tooling added on a feature branch isn't lost on the next bump — check PLAN.md
before assuming submodule state.
