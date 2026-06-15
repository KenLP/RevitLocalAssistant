# Architecture

See `PLAN.md` §1–3 for the full design rationale.

## Project map

```
RevitAssistant.sln
├── src/
│   ├── RevitAssistant.Core        ← PLACEHOLDER (Phase 0)
│   │                                 Phase 1: replaced by git submodule RevitMCP.Core
│   │                                 Contains: CommandRegistry, IRevitCommand,
│   │                                 ExternalEventHandler, all ~70 commands,
│   │                                 ParamUtil, UnitConversionPolicy, BatchPolicy
│   │
│   ├── RevitAssistant.Addin       ← IExternalApplication entry point
│   │                                 Ribbon button, DockablePane registration
│   │                                 Wires up Core + UI + Orchestrator
│   │
│   ├── RevitAssistant.UI          ← WPF MVVM (net8.0-windows, UseWPF=true)
│   │                                 ChatView, PreviewTable, ComplianceReport
│   │                                 CommunityToolkit.Mvvm
│   │
│   ├── RevitAssistant.Llm         ← Ollama orchestrator (no Revit dep)
│   │                                 OllamaClient, ToolSpecAdapter,
│   │                                 BimGlossary, IntentParser
│   │
│   ├── RevitAssistant.Compliance  ← Rule engine (YAML rules → findings)
│   │                                 ComplianceRule, ComplianceEvaluator
│   │
│   └── RevitAssistant.Schema      ← Live model schema export
│                                     Categories + params → compact JSON context
│
└── tests/
    ├── RevitAssistant.Llm.Tests        ← unit tests (net8.0, no Revit)
    └── RevitAssistant.Compliance.Tests ← unit tests (net8.0, no Revit)
```

## Threading invariants

```
UI Thread (Revit main)
  ↕  WPF DockablePane (data-bound to ChatViewModel)
  ↕  ExternalEvent.Raise() — only way to call Revit API

Background Thread (async)
  ↕  await OllamaClient.ChatAsync(...)  ← slow, never block UI
  ↕  await ExternalEventHandler.EnqueueAsync(...)  ← returns Task<JsonObject>
```

Rule: **nothing calls Revit API directly from a background thread.**
The existing `RevitMCPExternalEventHandler.EnqueueAsync` already enforces this.

## Install layout (per-user, Revit 2026)

```
%APPDATA%\Autodesk\Revit\Addins\2026\
  RevitAssistant.addin           ← manifest (auto-deployed on build)
  RevitAssistant.dll             ← entry point (IExternalApplication)
  RevitAssistant.Core.dll        ← service layer (from submodule)
  RevitAssistant.UI.dll          ← WPF panel
  RevitAssistant.Llm.dll         ← Ollama client
  RevitAssistant.Compliance.dll  ← rule engine
  RevitAssistant.Schema.dll      ← schema exporter
  YamlDotNet.dll                 ← compliance YAML parser
  CommunityToolkit.Mvvm.dll      ← WPF MVVM
  Rules\                         ← bundled compliance rule packs
    fire-safety.yaml
    room-completeness.yaml
    naming.yaml
    dimensional.yaml
```

Both `RevitMCPAddin.dll` (MCP server) and `RevitAssistant.dll` (this) can coexist
in the same addins folder — different ClientIds, no port conflict (the assistant
has no HTTP server).
