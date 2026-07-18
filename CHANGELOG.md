# Changelog

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
