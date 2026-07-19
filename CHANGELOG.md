# Changelog

## 2026-07-19 (2) — stop colliding with RevitMCPAddin in the shared Addins folder

The dev deploy copied every DLL flat into `%APPDATA%\Autodesk\Revit\Addins\<year>\`,
including `RevitMCP.Core.dll`. RevitMCPAddin ships a file of that same name, and the two
are pinned to **different branches with incompatible APIs** (this repo → the
`feat/extract-revit-mcp-core` fork; RevitMCPServer → `main`). Whichever add-in built last
won, and the other failed to load — observed live as *"Revit cannot run the external
application 'Revit MCP Addin'"*.

- **Dev deploy now writes assemblies to `…\Addins\<year>\RevitAssistant\`**, leaving only
  the `.addin` manifest in the shared folder. Nothing of ours occupies a shared filename
  any more.
- **The source `.addin` now points into the subfolder**, so the dev deploy and the
  installer use the same manifest and the same layout — the installer no longer rewrites
  it, removing a place the two could drift apart.
- ✅ **Verified in Revit 2026**: the add-in loads from the subfolder and answers queries.
  This closes the installer's biggest open risk — whether Revit resolves an add-in's
  dependencies from its own subfolder. It does, and the dev deploy now exercises that path
  on every build.

End users were never affected: the installer already used this layout. The collision was
dev-machine-only, on boxes with both add-ins installed.

## 2026-07-19 — fix context overflow that was silently disabling the system prompt

Found by running the add-in in Revit for real. The model had become unusable: it answered
in Spanish, skipped `echo_interpretation`, gave canned replies and invented a tool called
`query`.

**Cause.** Tool definitions plus the system prompt are sent on every request. That fixed
cost had reached ~9,530 tokens against a `num_ctx` of **8192**. Ollama truncates from the
front of the prompt, so what got cut was the system prompt itself — the model lost "always
answer in Vietnamese", the workflow rules and the list of valid tools. The code was
correct and every unit test passed; only the product was broken.

Measured, before → after this fix:

| | before my changes | at the regression | now |
|---|---|---|---|
| tools offered | 28 | 32 | 30 |
| fixed cost | ~8,347 tok | ~9,530 tok | ~8,803 tok |
| % of `num_ctx` | 101.9% (8192) | 116.3% (8192) | **53.7%** (16384) |

The surface was *already* over budget at 28 tools; the four tools added on 2026-07-15 took
it from marginal to badly over.

- **Dropped `raycast_headroom` and `create_detail_line` from the model's tool surface.**
  They cost ~630 tokens of JSON schema for tools that need raw (x,y) coordinates a chat
  user never supplies. Both remain in `ToolPolicy` as dispatchable-but-not-LLM-callable, so
  internal callers and a future UI keep them and the write gating is unchanged.
- **Raised `num_ctx` 8192 → 16384**, now single-sourced as `OllamaClient.DefaultNumCtx`.
  The previous value could not hold even the pre-existing surface.
- **Added `ToolSurfaceBudgetTests`** — fails if tools + prompt exceed 70% of the window, if
  they exceed it outright, or if any single tool costs more than ~800 tokens. Verified it
  goes red at the old 8192 setting. Nothing guarded this before, which is why a silent
  product regression survived a green test suite.
- Fixed `DiagnosticsRedactor` mangling escaped non-ASCII: `ContextSnapshot` is JSON inside
  JSON, so Vietnamese arrives as `kh\\u00f4ng`, and the UNC pattern matched the doubled
  backslashes — every diagnostic message was logged as `kh[path] t[path]`.

Verified live in Revit 2026 afterwards: *"có bao nhiêu phòng trong model?"* → **"Có 54
phòng trong model."** in Vietnamese, context at 30%.

## 2026-07-18 (2) — atomic undo, diagnostics control, installer scaffolding

- **Undo is now genuinely atomic.** It previously issued one `set_parameter_batch` per
  distinct before-value; each call was atomic on its own, so a failure part-way through
  left the earlier groups restored. `IRevitBridge` gained `CallBatchAsync`, which maps to
  Core's batch dispatcher — that runs every step inside a **single Revit transaction** and
  rolls the whole thing back on the first failure. A restore now lands completely or not
  at all. The batch path enforces the same `ToolPolicy` allowlist per step, so batching
  cannot be used to smuggle a command past the gate.
- **Users can delete their diagnostics from the UI.** `IFeedbackSink.Clear()` is wired to a
  🛡 button in the chat header; previously the only way to remove the log was to find
  `feedback.jsonl` under `%APPDATA%` by hand.
- **Installer scaffolding** — `installer/inno/RevitAssistant.iss` and
  `installer/build-installer.ps1`. Installs into a per-add-in subfolder
  (`…\Addins\<year>\RevitAssistant\`) with the manifest pointing into it, so dependencies
  cannot collide with another add-in's copies; refuses to run while Revit is open; emits a
  SHA-256 next to the setup.
  **Neither has been compiled or installed** — Inno Setup was not available. The staging
  half of the build script was exercised (17 assemblies, manifest rewrite, no PDBs) and
  correctly exits 2 when the compiler is missing, but the `.iss` itself is unverified. See
  [installer/README.md](installer/README.md) for the acceptance checklist; `docs/INSTALL.md`
  no longer implies a released installer exists.

Suite: 266 passing.

## 2026-07-18 — undo/import correctness, offline + privacy enforcement

### P1

- **Undo is now refused where it cannot restore faithfully.** `update_where` records
  before-values as display strings (`Parameter.AsValueString`), which only round-trips for
  String storage — a Double comes back unit-formatted ("2100 mm") and an ElementId comes back
  as the target's *name*, so feeding either back wrote the wrong value or failed. A type-scope
  edit was worse: the restore addressed instance ids for a parameter living on the type. Undo
  is now offered only for instance-scope String parameters; the storage type is probed via
  `get_parameter` at capture time.
- **Undo no longer half-applies or loses its state.** Restores run `atomic: true`, every group
  is rehearsed with a dry-run and nothing is written unless all groups pass, and the undo state
  is cleared only after a fully successful restore — previously it was dropped *before* the
  restore ran, so a failure left the user with a changed model and no way back.
- **Import now validates for real.** The dry-run calls `import_parameters` with `dryRun: true`
  (Core runs ModelWrite in a transaction and rolls back), so parameter existence, read-only
  status, storage type and whether the value actually took are all proven before the user
  confirms. Previously the "dry-run" only built a lookup, and the commit was the first genuine
  write attempt. Commit reuses the same item builder, so what was validated is what is written.
- **Ambiguous and truncated imports are blocked** instead of silently guessing: duplicate match
  keys used to first-win (writing a row's data to an arbitrary one of the matching elements),
  and a category at/over the 5000-element fetch limit produced an incomplete lookup that
  quietly turned matched rows into "not found".
- **Stubs fail loudly.** `ComplianceEvaluator.EvaluateAsync` returned an empty list — which
  reads as "no violations" for a model it never evaluated — and `ModelSchemaExporter.Export`
  returned `{}`, silently stripping the prompt of real category/parameter names. Both now throw
  `NotImplementedException`. Neither had production callers.

### P2

- **Loopback is enforced by default.** The Ollama endpoint came from an environment variable
  with no validation, so a stray value could ship prompts — which quote real project and
  parameter data — off-box, in cleartext. Remote endpoints now require
  `REVIT_ASSISTANT_ALLOW_REMOTE_LLM=1` *and* https; anything rejected falls back to loopback
  and tells the user why rather than silently ignoring their setting.
- **Diagnostics are redacted.** Feedback entries wrote the assistant's reply and a conversation
  snapshot verbatim, routinely including full model paths and the Windows account name. Paths
  (drive, UNC and JSON-escaped) and the user name are now scrubbed before the log touches disk,
  and `FileFeedbackSink.Clear()` lets the user delete the log.
- **CSV export defuses formula injection.** Cells starting `=`, `+`, `-` or `@` are exported as
  text, so a Comments value like `=HYPERLINK(...)` no longer becomes a live formula on open.

Suite: 263 passing. Each new guard was verified to fail when its check is removed.

## 2026-07-17 (2) — write-path safety

- **Deny-by-default tool policy (P0-C).** New `ToolPolicy` is the single source of truth for
  which tools exist, which mutate the model, and how each is previewed. Enforced twice: the
  orchestrator rejects any name the model is not allowed to use, and `RevitBridge` refuses to
  hand an unlisted name to Core. Previously anything not in two hard-coded write sets fell
  through to a plain dispatch, so a model naming a real-but-unexposed Core command
  (`delete_elements`, `move_element`, …) reached Revit with no preview and no confirmation.
- **Four writes that were reachable without confirmation are now gated**: `tag_all_in_view`,
  `copy_parameters`, `configure_schedule` and `create_detail_line`. `raycast_headroom` is
  classified `TransientWrite` — Core marks it non-read-only, but it creates and deletes its
  own scratch view inside the transaction, so it dispatches like a read.
- **Every model write now dry-runs before the preview.** Core runs `ModelWrite` commands in a
  transaction and rolls back on `dryRun`, so the previous "these commands don't support
  dryRun" assumption was wrong. Arguments are now validated against the real model before the
  user is asked to approve anything.
- **Preview is bound to the document and the outcome (P0-D).** A pending write records the
  document identity plus a digest of the dry-run result. `ConfirmAsync` re-checks both and
  refuses to commit if the user switched project or the model changed while the confirmation
  was on screen — previously confirm simply re-ran the call against whatever document was
  active, so it could touch a different element set than the one previewed.
- Tests: added `ToolPolicyTests` (policy invariants) and orchestrator tests for blocked tools
  and both stale-preview paths. Verified each fails when the corresponding gate is removed.
  Write-flow tests now exercise `update_where` rather than the internal-only
  `set_parameter_batch`. Suite: 229 passing.

## 2026-07-17

- **Reverted the submodule pin back to `9c22e50`**, undoing the 2026-07-16 re-pin to `main`.
  That re-pin was based on an upstream claim that `main` was a strict superset of the
  `feat/extract-revit-mcp-core` branch. It is not: `query_where`, `update_where` and
  `import_parameters` exist only on that branch (RevitMCPServer commit `5ac811d`). Pinning to
  `main` removed the assistant's primary read/count/list tool, its primary edit tool and the
  CSV/XLSX import commit path — the assistant was broken at runtime while all 192 unit tests
  still passed, because they are pure logic and never reach Core.
- Reverted the two tool renames that came with it: `spatial_get_room_boundary` →
  `get_room_boundary`, `spatial_raycast_headroom` → `raycast_headroom`.
- **Added `ToolSurfaceCoreContractTests`** (4 tests) asserting that every non-virtual tool in
  `ToolSpecAdapter`, every command hard-coded in a `CallAsync(...)`, and every name in the
  write gates actually exists in the pinned Core's `CommandRegistry`. Verified these fail —
  naming the exact missing commands — when pinned back to the bad commit.

## 2026-07-16

- **Re-pinned the `extern/RevitMCPCore` submodule** from the `feat/extract-revit-mcp-core`
  fork (`9c22e50`) to `origin/main` (`33d60b6`, v0.8.15, 91 commands). The fork's fixes for
  `find_elements`/`list_elements` pagination and type-parameter resolution (documented in
  [docs/handoffs/HANDOFF_revitmcp-find-elements-fix.md](docs/handoffs/HANDOFF_revitmcp-find-elements-fix.md))
  turned out to already be on `main` (v0.8.6/v0.8.11); the one gap `main` had — `view_id`
  scoping — was ported upstream in `33d60b6`, making it a strict superset of the fork.
- Renamed 2 tool calls to match `main`'s naming: `get_room_boundary` → `spatial_get_room_boundary`,
  `raycast_headroom` → `spatial_raycast_headroom` (`get_doors` and `create_detail_line` kept
  their names).

## 2026-07-15

- **Fixed `find_elements`/`list_elements` pagination and type-parameter resolution**
  (`extern/RevitMCPCore` submodule). `offset` was declared but never consumed, so every page
  returned the same first N elements; both commands now page correctly and return
  `total`/`offset`/`hasMore`/`nextOffset`. `find_elements` also only read instance
  parameters, so type-level params (Fire Rating, door Width, materials, assembly codes) came
  back empty in both the `fields` projection and `filters` — both now fall back to the
  element's Type via a cached lookup.
- **Wired in the spatial/QC commands** the submodule already shipped but the assistant never
  exposed: `get_doors`, `get_room_boundary`, `raycast_headroom`, `create_detail_line`. Added
  tool definitions, confirmation flow for the write tool (`create_detail_line`), and prompt
  guidance; also removed a stale prompt line that told the model `get_doors` didn't exist.
- **Added `README.md`** — project overview, features, layout, requirements, quick start, and
  a docs index.
- Updated version labels in `PLAN.md` and `CORE_EXTRACTION.md` to reflect the current
  submodule pin (commit `9c22e50`, 86 commands) without rewriting the historical inspection
  notes they document.
- Fixed a stale clone URL in `docs/INSTALL.md` (`RevitAssistant` → `RevitLocalAssistant`).
