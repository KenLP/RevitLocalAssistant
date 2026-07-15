# Changelog

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
