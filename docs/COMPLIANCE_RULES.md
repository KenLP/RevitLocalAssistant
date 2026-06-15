# Compliance Rule Format

> Hướng dẫn viết rule kiểm tra tuân thủ (compliance) cho RevitAssistant.
> Rule files nằm ở `src/RevitAssistant.Compliance/Rules/*.yaml`.

---

## Rule Schema (YAML)

```yaml
- id: string            # unique ID, e.g. "FR-001", "ROOM-001"
  description_vi: string   # mô tả tiếng Việt
  description_en: string   # professional English description
  category: string      # BuiltInCategory, e.g. "OST_Doors"
  scope_filter: string | null   # optional pre-filter (same syntax as find_elements filters)
  assertion: string     # condition that must be TRUE to PASS
  severity: "error" | "warning" | "info"
```

---

## Assertion Syntax

```
<ParameterName> <operator> <value>
```

| Operator | Meaning | Example |
|---|---|---|
| `=`, `==` | equals | `Comments = "thoát hiểm"` |
| `!=` | not equals | `Name != ""` |
| `>`, `>=` | numeric | `Fire Rating >= 60` |
| `<`, `<=` | numeric | `Width < 0.3` |
| `contains` | string | `Type Name contains "FRW"` |
| `is not empty` | non-null/non-blank | `Department is not empty` |
| `AND`, `OR` | combine | `Name is not empty AND Department is not empty` |

---

## Starter Rule Packs

### 1. Fire Safety (`Rules/fire-safety.yaml`)
- FR-001: Exit doors → Fire Rating ≥ 60 min
- FR-002: Fire-rated walls → Fire Rating ≥ 120 min
- FR-003: Stair enclosure walls → Fire Rating ≥ 120 min

### 2. Room Completeness (`Rules/room-completeness.yaml`)
- ROOM-001: All rooms have Name + Department
- ROOM-002: All rooms have area > 0 (no zero-area rooms)
- ROOM-003: Room numbers are unique per level

### 3. Naming Convention (`Rules/naming.yaml`)
- NAME-001: Walls — Type Name matches regex `[A-Z]{2}-\d{3}`
- NAME-002: Doors — Mark is not empty
- NAME-003: Windows — Mark is not empty

### 4. Dimensional (`Rules/dimensional.yaml`)
- DIM-001: Habitable rooms — clear height ≥ 2.4 m (QCVN 04)
- DIM-002: Corridors — clear width ≥ 1.2 m
- DIM-003: Exit doors — clear width ≥ 0.8 m

---

## Workflow (Phase 6)

1. User: *"kiểm tra cấp chống cháy tất cả cửa"* (VI)
2. LLM maps → compliance query: `{ruleIds: ["FR-001"], or category: OST_Doors}`
3. Evaluator calls `find_elements(OST_Doors)` via Core dispatcher
4. Per element: evaluate assertion against actual parameter value
5. Return `ComplianceReport` with pass/fail count + failing ElementIds
6. UI renders report table with "Jump to element" + optional "Fix all" → bulk edit flow
