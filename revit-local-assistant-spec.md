# Revit Local Assistant (R25/R26) — Design Spec & Handoff

> Conversation export for Claude Code. Goal: build a local-running Revit add-in assistant (similar to Autodesk Assistant in R27) that queries model info and performs bulk parameter-value edits, understands EN/VI, and can leverage the existing **RevitMCPServer v0.4.0**.

---

## 1. Requirement Summary

Build an **Assistant add-in** for Revit **R25/R26** that:

- Runs **entirely local** on the user's machine (offline-capable).
- Can **query model information**.
- Can perform **bulk edits / updates — parameter values only** (no geometry create/delete).
- Understands **English and Vietnamese**.
- May use local LLMs (Gemma 3 / Qwen / Llama, etc.).

---

## 2. Feasibility Assessment — HIGH (~85–90%)

The chosen scope (query + bulk parameter edit) is the "sweet spot":

- No geometry create/delete → low risk, simple transactions.
- Parameter API is very stable since Revit 2020+.
- Bulk edit maps cleanly to structured output (JSON) → easy to validate before commit.

The only hard part is **NLP → intent → accurate filter logic**, not the Revit API itself.

---

## 3. Overall Architecture

```
┌─────────────────────────────────────────────────┐
│  Revit Add-in (.NET, IExternalApplication)        │
│  ┌──────────────┐    ┌──────────────────────┐    │
│  │ WPF Panel    │◄──►│ Command Dispatcher    │    │
│  │ (chat UI)    │    │ (ExternalEvent queue) │    │
│  └──────────────┘    └──────────┬───────────┘    │
│                                  │                 │
│  ┌───────────────────────────────▼──────────────┐ │
│  │ Revit Service Layer (real API)               │ │
│  │ - QueryEngine (FilteredElementCollector)     │ │
│  │ - ParameterEditor (Transaction + bulk set)   │ │
│  │ - SchemaExporter (categories/params → JSON)  │ │
│  └───────────────────────────────────────────────┘ │
└──────────────────┬──────────────────────────────────┘
                   │ (local HTTP / IPC)
        ┌──────────▼──────────┐
        │ LLM Layer (local)   │
        │ Ollama / LM Studio  │
        │ → function-calling  │
        └─────────────────────┘
```

### Processing flow (most important) — tool/function-calling pattern

Do **not** let the LLM "edit directly". Use the tool-calling pattern:

| Step | Component | Role |
|------|-----------|------|
| 1 | Add-in | Export model schema (categories, type/instance params, units) → context |
| 2 | LLM | Parse VI/EN command → structured intent (JSON: action, filter, target_param, value) |
| 3 | Add-in | **Dry-run**: run filter, return list of N affected elements |
| 4 | User | Confirm (preview table) |
| 5 | Add-in | `Transaction` + `ExternalEvent` → commit bulk edit |

`ExternalEvent` is **mandatory** — all model edits must run on Revit's main thread; never call directly from an async/HTTP callback.

---

## 4. LLM Choice — Can Gemma be used?

Yes, with conditions. (Note: it's **Gemma**, current version is **Gemma 3**, not "Gemma4".)

The deciding factor is **structured-output + function-calling quality**, not general "understanding".

| Model (local) | VRAM | Function-calling | VI quality | Recommendation |
|---------------|------|------------------|-----------|----------------|
| **Qwen 2.5 7B/14B Instruct** | 8–16GB | Very good | Good | ⭐ Best for VI + tool use |
| **Gemma 3 12B** | ~10GB | Decent | Decent | Usable, weaker VI than Qwen |
| **Llama 3.1 8B** | 8GB | Good | Medium | OK for EN, VI so-so |
| **Phi-4 14B** | ~12GB | Good | Weak VI | Not recommended for VI |

**Recommendation:** start with **Qwen 2.5 14B Instruct** via **Ollama** — noticeably stronger Vietnamese than Gemma and stable native tool-calling, ideal for bulk-edit requiring precise JSON.

> Note on hardware: target dev machine has an NVIDIA RTX A4000 Laptop GPU (8GB VRAM). 14B may need quantization (Q4) or fallback to 7B for comfortable headroom; validate locally.

---

## 5. Critical Technical Notes

1. **Don't trust small models to generate element IDs** — let the LLM produce only *filter criteria*; the add-in resolves elements via API.
2. **Separate validation layer**: check param data type (Double/Int/String/ElementId), read-only flags, and units (ForgeTypeId/UnitUtils) before setting.
3. **Shared/built-in params**: built-ins are easy; shared params need GUID/definition mapping.
4. **Always preview before commit** + wrap in a `TransactionGroup` for clean rollback.

---

## 6. Reusing the Existing RevitMCPServer (v0.4.0)

RevitMCPServer already covers ~70–80% of the hardest work. An MCP server and a "local assistant add-in" differ mainly in **transport layer** and **who calls the LLM**.

### Reusable as-is

| Layer | In RevitMCPServer | Reuse for Assistant |
|-------|-------------------|---------------------|
| **Revit Service Layer** | Query/edit logic, FilteredElementCollector wrappers | ✅ 100% — highest-value part |
| **ExternalEvent dispatcher** | Bridges MCP call → main thread | ✅ 100% — identical need |
| **Tool definitions** | MCP tools (query model, set parameter…) | ✅ Map directly to function-calling schema |
| **Transaction handling** | Already wrapped | ✅ 100% |
| **Param type/unit validation** | If present | ✅ Reuse |

### The 2 core differences

```
RevitMCPServer (current):
  Claude Desktop ──MCP/stdio──► Server ──ExternalEvent──► Revit
  (LLM external, transport = MCP protocol)

Local Assistant (target):
  WPF Panel ──┐
              ├──► Orchestrator ──► [Ollama local] ──function-call──► Service Layer
  Service ────┘
  (LLM internal, transport = in-process / local HTTP)
```

Two things to **add new**:
1. **WPF chat panel** (UI inside Revit instead of Claude Desktop).
2. **Internal LLM orchestrator** — call Ollama, parse tool-call, loop confirm → commit.

### Recommended architecture strategy — keep MCP, run side by side

Split into 3 projects:

```
RevitMCP.Core        ← Service Layer + Tools + Validation (shared, unchanged)
RevitMCP.Server      ← MCP transport (kept as-is, for Claude Desktop)
RevitMCP.Assistant   ← WPF panel + Ollama orchestrator (new)
```

Both the MCP server and the Assistant reference `Core`. One codebase, two front-ends:
- **Online mode**: Claude Desktop via MCP (stronger model, needs internet).
- **Local mode**: WPF + Qwen/Gemma (offline, on user's machine).

Your tool definitions are already JSON schema → MCP tool spec and Ollama/OpenAI function-calling spec are nearly equivalent; just need a thin adapter to convert formats.

### Remaining real work

| Task | Effort | Notes |
|------|--------|-------|
| Refactor Service+Tools into `Core` | Low | Mostly namespace moves |
| Tool-spec adapter (MCP ↔ function-call) | Low | ~1 mapping file |
| WPF chat panel + DockablePane | Medium | UI + binding |
| Ollama orchestrator (call → parse → loop) | Medium | The truly "new" part |
| Dry-run/preview + confirm UX | Medium | Bulk-edit safety |

---

## 7. Suggested Next Steps (open options)

- **(A)** Concrete refactor of current RevitMCPServer → 3-project layout (needs current folder/namespace structure).
- **(B)** Adapter converting MCP tool definitions → Ollama function-calling format.
- **(C)** Ollama orchestrator skeleton (call → parse tool_calls → dry-run → confirm → commit loop).

---

## Context / Environment

- Existing project: **RevitMCPServer v0.4.0** (GitHub: KenLP), local HTTP server at `127.0.0.1:7891`, stored at `D:\AIProjects` (Windows).
- Target Revit versions: **R25 / R26**.
- Dev GPU: NVIDIA RTX A4000 Laptop GPU (8GB VRAM).
- Languages: English + Vietnamese.

*Prepared by Ken Phuc.*
