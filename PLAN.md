# RevitLocalAssistant — Detailed Build Plan

> Local-running Revit **R26-first** (R25/R27-ready) add-in assistant. Chat in **Vietnamese or English**, query model info, run **compliance checks**, and perform **safe bulk parameter edits** with dry-run preview. Runs **fully offline** on a local LLM (Ollama). Reuses the mature **RevitMCPServer** service layer.
>
> Prepared for Ken Phuc — 2026-06-15.

> **Current submodule pin (2026-07-16):** `extern/RevitMCPCore` @ `origin/main` commit
> `33d60b6` (v0.8.15) — re-pinned from the `feat/extract-revit-mcp-core` fork (`9c22e50`) once
> `main` became a strict superset (91 commands total). Two spatial-QC tools carry a `spatial_`
> prefix on `main`: `get_room_boundary` → `spatial_get_room_boundary`,
> `raycast_headroom` → `spatial_raycast_headroom`; `get_doors` and `create_detail_line` kept
> their names. See [docs/handoffs/HANDOFF_revitmcp-find-elements-fix.md](docs/handoffs/HANDOFF_revitmcp-find-elements-fix.md)
> for how the fork's remaining gap (`view_id`) was closed upstream. The section below documents
> the original 2026-06-15 inspection as-is; it is historical context for the decisions made,
> not a live version tracker.

> **Decisions locked (2026-06-15):** Core sharing = **git submodule** (extract `RevitMCP.Core` from
> the MCP repo first). Default LLM = **Qwen2.5-7B-Instruct (Q4_K_M)**, 14B opt-in. Installer =
> **Inno Setup .exe** + dev MSBuild deploy target. First target = **Revit 2026 (`net8.0-windows`)**.

---

## 0. What changed after reading the existing code

The spec assumed `RevitMCPServer v0.4.0` with limited coverage. Reality (inspected at
`C:\Users\lep\My Drive\02 RD Projects\00 AI\RevitMCPServer`, git HEAD `e6b00b8`):

- **v0.8.0, ~70 commands** (`RegisterDefaults()` in `CommandRegistry.cs`): full inspection,
  creation, edit, transform, view ops, clash/clearance.
- The hard parts the spec lists as "to build" **already exist and are battle-tested**:
  - **ExternalEvent dispatcher** on the Revit UI thread (`RevitMCPExternalEventHandler.cs`),
    returning `Task<JsonObject>` — async, non-UI-blocking.
  - **Transaction policy** per `ExecutionKind` (ReadOnly / ModelWrite / UiAction), single
    transaction for single ops, one transaction across a batch, mixed-batch rejection.
  - **Dry-run / preview is already implemented at the dispatcher level** — every write runs in a
    transaction that is *rolled back* when `dryRun=true`, returning what *would* have happened.
    This is the spec's "Step 3 dry-run + Step 4 confirm" — already done.
  - **Validation layer**: `SetParameterCommand` / `SetParameterBatchCommand` already check
    `StorageType`, `IsReadOnly`, and do unit conversion (`UnitConversionPolicy`, `ParamUtil`).
  - **Auth** (per-Revit-version bearer token), **localhost HTTP API** (`/health`, `/commands`,
    `/mcp`, `/mcp/batch`), **multi-version port auto-assign** (2026→7891…).
  - **Multi-target build**: `net8.0-windows` for R25/R26, `net10.0-windows` for R27, via
    `-p:RevitVersion=`; CI fallback to Nice3point reference assemblies.

**Consequence:** ~70–80% of the Revit-side work is reusable as-is. The genuinely new work is:
**(1)** an in-process tool-spec adapter, **(2)** an Ollama orchestrator loop, **(3)** a WPF chat
panel, **(4)** a VI↔EN BIM NLU layer, **(5)** a compliance rule engine, **(6)** a packaged installer.

---

## 1. Target architecture

The Assistant **embeds the shared Core in-process** and calls the dispatcher directly — no HTTP
hop, no auth needed inside Revit. The existing MCP server keeps its HTTP server for Claude
Desktop. Both can be installed side-by-side (separate `.addin`, separate `ClientId`).

```
┌──────────────────────── Revit 2026 process ────────────────────────┐
│  RevitAssistant.Addin (IExternalApplication)                        │
│   • Ribbon button → toggles DockablePane                            │
│   • Hosts CommandRegistry + ExternalEvent (in-process, no HTTP)     │
│                                                                     │
│   ┌── WPF DockablePane (RevitAssistant.UI, MVVM) ──────────────┐   │
│   │  Chat (VI/EN)  │ Interpretation line │ Preview table │ ✔/✖ │   │
│   └───────┬──────────────────────────────────────────────────┘   │
│           │ user message                                           │
│   ┌───────▼──── Orchestrator (RevitAssistant.Llm) ────────────┐   │
│   │ 1 build context: glossary + live model schema + tool specs │   │
│   │ 2 await Ollama  → tool_calls (JSON)                         │   │
│   │ 3 echo EN interpretation                                    │   │
│   │ 4 dispatch dryRun=true → preview table                      │   │
│   │ 5 user confirms → dispatch dryRun=false → commit            │   │
│   └───────┬───────────────────────────────┬───────────────────┘   │
│           │ async HTTP (localhost)         │ EnqueueAsync (TCS)     │
│   ┌───────▼─────────┐            ┌─────────▼──────────────────┐    │
│   │ Ollama  :11434  │            │ RevitAssistant.Core         │    │
│   │ Qwen2.5-Instruct│            │  Commands + dispatcher +    │    │
│   │ (offline)       │            │  validation (from MCP repo) │    │
│   └─────────────────┘            └──────────┬──────────────────┘    │
│                                  ExternalEvent → Revit main thread  │
└────────────────────────────────────────────────────────────────────┘
```

**Threading rule (critical):** the WPF panel lives on Revit's UI thread. The Ollama call is slow —
it MUST be `await`ed off the UI thread. Revit edits MUST go through `ExternalEvent` (the existing
`EnqueueAsync` already returns a `Task` resolved on the main thread). So the orchestrator awaits
both Ollama and the dispatcher without ever blocking the UI thread or calling the Revit API off-thread.

---

## 2. Repository layout (standard git)

```
RevitAssistant/
├─ .gitignore .editorconfig .gitattributes
├─ README.md  CHANGELOG.md  LICENSE  PLAN.md
├─ RevitAssistant.sln
├─ Directory.Build.props              # shared TFM/RevitVersion logic (copied pattern from MCP repo)
├─ Directory.Packages.props           # central package versions
├─ docs/
│  ├─ ARCHITECTURE.md  INSTALL.md  ROADMAP.md
│  ├─ VI_BIM_GLOSSARY.md              # the curated VI↔EN ontology (human-readable source of truth)
│  └─ COMPLIANCE_RULES.md             # rule DSL + authoring guide
├─ src/
│  ├─ RevitAssistant.Core/            # SHARED service layer (see §3 for sharing strategy)
│  ├─ RevitAssistant.Addin/           # IExternalApplication, Ribbon, DockablePane, .addin
│  ├─ RevitAssistant.UI/              # WPF MVVM: ChatView, PreviewTable, ComplianceReport
│  ├─ RevitAssistant.Llm/             # OllamaClient, ToolSpecAdapter, Orchestrator, NLU
│  ├─ RevitAssistant.Compliance/      # Rule, RuleSet, Evaluator + rules/*.yaml
│  └─ RevitAssistant.Schema/          # ModelSchemaExporter (categories/params/units → context)
├─ tests/
│  ├─ RevitAssistant.Llm.Tests/       # adapter, glossary, intent parsing (no Revit needed)
│  └─ RevitAssistant.Compliance.Tests/
├─ assets/                            # ribbon icons (16/32px), logo
├─ installer/
│  ├─ inno/RevitAssistant.iss         # Inno Setup script
│  ├─ scripts/install.ps1 uninstall.ps1
│  └─ ollama-bootstrap.ps1            # detect/install Ollama + pull model
└─ .github/workflows/  ci.yml release.yml
```

---

## 3. Core-sharing strategy (the one decision that shapes everything)

The Revit service layer must NOT be duplicated between the two repos (drift = the #1 risk).
Three options — **recommendation: B (git submodule)**:

| Option | How | Pros | Cons |
|--------|-----|------|------|
| **A. Copy-in** | Copy `Commands/` + dispatcher + validation into `RevitAssistant.Core` | Standalone repo, zero changes to MCP repo | Two copies drift over time |
| **B. Submodule** ⭐ | Refactor MCP repo: extract `RevitMCP.Core` classlib; add it to RevitAssistant as a git submodule | One source of truth; both addins always in sync | Requires a (low-risk) refactor of the existing repo first |
| **C. Local project ref** | `RevitAssistant.sln` references the MCP repo's project by relative path | No refactor | Brittle absolute/relative paths; not portable for CI/installer |

Option B refactor in the MCP repo is mostly namespace moves: `Commands/`, `RevitMCPExternalEventHandler.cs`,
`JsonResult.cs`, `ParamUtil.cs`, `UnitConversionPolicy.cs`, `BatchPolicy.cs` → new `RevitMCP.Core`
classlib; `App.cs` + `Server/` stay in the server addin and reference Core. Existing tests keep passing.

---

## 4. The Vietnamese → professional-EN BIM pipeline (key differentiator)

Goal: understand Vietnamese precisely and map it to **exact** Revit BuiltInCategory / parameter
names — not the model's latent guesswork. Five mechanisms, layered:

1. **Curated VI↔EN BIM glossary** (`docs/VI_BIM_GLOSSARY.md` → compiled to `BimGlossary.cs`).
   Bidirectional, domain-accurate. e.g.
   `tường→Walls(OST_Walls)`, `cửa đi→Doors`, `cửa sổ→Windows`, `cột→Columns/StructuralColumns`,
   `dầm→StructuralFraming(Beam)`, `sàn→Floors`, `trần→Ceilings`, `phòng→Rooms`, `cao độ/tầng→Level`,
   `mã hiệu→Mark`, `vật liệu→Material`, `cấp chống cháy→Fire Rating`, `tải trọng→Load`,
   `khối tích→Volume`, `diện tích→Area`, `chiều cao→Height`, `bề dày→Thickness`.
2. **Live model-schema grounding** (`RevitAssistant.Schema`): export the categories that actually
   have elements in *this* model + their real instance/type parameters, storage types, units,
   read-only flags. Inject a compact form into the LLM context so it can only pick names that exist.
3. **Two-stage prompt**: (a) normalize VI → canonical intent, (b) bind to tool calls using glossary
   + schema. Keeps the model from inventing parameter names.
4. **Echo-back interpretation line**: before any dry-run the panel shows *"Hiểu là / Understood:
   set `Fire Rating` = 60 min on all `Doors` where `Function = Exterior`"*. Directly satisfies the
   "convert VI → chuyên môn EN, must be accurate" requirement — the user verifies the translation
   before anything runs.
5. **`clarify` tool**: when ambiguous (e.g. "cột" could be architectural or structural column), the
   LLM asks back **in the user's language** instead of guessing.

---

## 5. Compliance engine (new capability)

Deterministic evaluation, LLM-assisted authoring. The LLM never decides pass/fail — it only turns a
NL request into a rule query; the engine evaluates against real elements via Core `find_elements`.

- **Rule model** (`Rule.cs`): `{ id, description(vi/en), category, scope-filter, assertion
  (param op value), severity }`. Rule sets in `installer`-shipped `rules/*.yaml` (editable).
- **Evaluator**: for each rule → `find_elements(category, scope-filter)` → evaluate assertion per
  element → collect pass/fail with ElementIds → render a **Compliance Report** view (table + jump-to-element).
- **Example (VI)**: *"kiểm tra tất cả cửa thoát hiểm có cấp chống cháy ≥ 60 phút"* →
  rule `{category: Doors, scope: Function=Exterior OR Comments~"thoát hiểm", assert: FireRating ≥ 60}`.
- **Starter rule packs**: required-parameter completeness (every Room has Department/Name),
  naming conventions (Mark matches regex), dimensional limits (clear height ≥ 2.4 m), fire ratings.
- Compliance findings can feed bulk-fix: "fix all failing rooms" → dry-run preview → confirm.

---

## 6. LLM choice & hardware (RTX A4000 Laptop, 8 GB VRAM)

- **Default: `qwen2.5:7b-instruct` (Q4_K_M, ~4.7 GB)** — reliable native tool-calling, strong VI,
  fits 8 GB with headroom. The model only emits small JSON tool-calls, so 7B + glossary/schema
  grounding is sufficient and fast.
- **Opt-in: `qwen2.5:14b-instruct` (Q4_K_M, ~9 GB)** — better VI nuance; tight on 8 GB (will spill
  to RAM/slow). Offer as a settings toggle for users with ≥12 GB.
- Transport: Ollama OpenAI-compatible endpoint `/v1/chat/completions` with `tools` (cleanest C#
  path), fallback to native `/api/chat`. Strict JSON via tool schemas + a validating retry loop.

---

## 7. Tool-spec adapter

`GET /commands` already returns `{name, isReadOnly, riskLevel, executionKind}`. We need the full
JSON Schema for each command's params. Two sources, pick one:

- The TS MCP server's zod schemas (`src/McpServer/src/index.ts`, 63 tools) are the existing spec —
  port the relevant **read + parameter-edit** subset to a C# `ToolSpecAdapter` that emits OpenAI
  function-calling format. (Don't expose geometry create/delete in v1 — matches the spec's
  query + parameter-edit scope.)
- Curate a **minimal v1 tool surface** for the Assistant: `find_elements`, `get_element_info`,
  `get_parameter`, `list_*` (read), `set_parameter`, `set_parameter_batch`, `rename_element`
  (write) + `clarify` + `run_compliance` (assistant-level). Smaller surface = better small-model accuracy.

---

## 8. Milestones (R26-first)

| Phase | Deliverable | Est. |
|-------|-------------|------|
| **0** | Repo scaffold, `.sln`, `Directory.Build.props`, CI skeleton, decide Core-sharing | 0.5–1 d |
| **1** | Core shared & building against R26 (`net8.0-windows`); smoke test in Revit | 1–2 d |
| **2** | `OllamaClient` + `ToolSpecAdapter` + minimal tool surface; console harness (no UI) | 2–3 d |
| **3** | WPF DockablePane + Ribbon + chat MVVM; render messages | 3–4 d |
| **4** | Orchestrator loop: call→parse→**dry-run preview table→confirm→commit** | 3–4 d |
| **5** | VI/EN NLU: `BimGlossary` + `ModelSchemaExporter` + echo-back + `clarify` | 3–4 d |
| **6** | Compliance engine + starter rule packs + report view | 3–5 d |
| **7** | Inno Setup installer + Ollama bootstrap + dev MSBuild deploy; GitHub Release | 2–3 d |
| **8** | Hardening: worksharing-aware edits, edge cases, tests, docs, EN/VI UX polish | ongoing |

**MVP (Phases 0–4)** = working bilingual query + safe bulk parameter edit with preview.
**v1 (through Phase 7)** = + compliance + one-click installer.

---

## 9. Scope guardrails (v1)

- **Edits = parameter values + rename only** (per spec). No geometry create/delete from the
  Assistant in v1 (Core supports it but it's not exposed to the LLM surface).
- **Always dry-run → preview → explicit confirm** before any commit (uses existing dispatcher dryRun).
- **Worksharing ("collaborate"):** v1 = worksharing-*aware* (skip/flag elements not editable/owned,
  report them); full multi-user checkout workflows deferred to v2.
- **Offline-first:** no telemetry, no cloud calls; Ollama is local. Optional online mode = the
  existing MCP/Claude path, untouched.

---

## 10. Top risks & mitigations

| Risk | Mitigation |
|------|-----------|
| Small-model picks wrong param/category name | Schema grounding + glossary + echo-back + validating retry; small tool surface |
| UI thread blocked by Ollama call | All LLM I/O async off-thread; Revit work only via ExternalEvent |
| Core drift between two repos | Git submodule (Option B), shared CI |
| 14B too big for 8 GB | Default to 7B; 14B opt-in with a VRAM warning |
| VI mojibake (em-dash/diacritics) | UTF-8 enforced end-to-end (the MCP repo already hit & fixed this on its HTTP read path) |
| Bulk edit on read-only/worksharing-locked params | Existing `IsReadOnly` checks + worksharing-aware pre-filter + per-element error report |
```
