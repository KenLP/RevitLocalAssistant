# Reliable Execution Architecture — Handoff for RevitLocalAssistant

> **Origin:** This was designed & implemented (in TypeScript, inside the *BIMClaw* project) to fix the
> parameter-edit accuracy problem you reported. It belongs here in **RevitLocalAssistant** (C#/.NET).
> This doc is **self-contained**: the problem, root cause, the architecture/flow, the full reference
> code (TypeScript), and **how to port it to C# (in-process Revit add-in)** — which is actually
> *simpler and more reliable* than the TS/MCP version because you call the Revit API directly.

---

## 1. The problem (verbatim)

> "ví dụ tôi kêu là các cửa có giá trị fire rating là NR → update comment là 'Cap nhat' → nhưng kết
> quả ra không đúng, cửa có giá trị khác cũng bị ghi nhầm. Tính chính xác chưa ổn… nên tối ưu cấu
> trúc LLM ở bìa, rồi determine flow khi thực thi… dạy AI hiểu từng lệnh, đảm bảo memory và thực hiện
> chính xác."

Symptoms seen in the screenshot:
- "Set 'Fire Rating' on 0/38 elements (38 failed)" — parameter not found.
- Doors with a *different* Fire Rating also got `Comments = "Cap nhat"` (wrong set edited).

## 2. Root cause

The LLM was doing the **precise work itself** — choosing which elements match and hand-building the
find/set calls. LLMs are reliable at *understanding intent*, unreliable at *exact set operations*.

Two concrete Revit facts made it worse (both confirmed live on R26 Snowdon Architectural):

1. **"Fire Rating" on doors is a TYPE parameter**, not instance. `element.LookupParameter("Fire Rating")`
   on a door *instance* returns `null` → "not found", and any instance-level filter silently matches nothing
   (so the wrong fallback set got written).
2. **Instance vs type scope must be resolved deterministically.** "Comments" is an *instance* param;
   "Fire Rating" is a *type* param. The same command touches both scopes.

## 3. The architecture / flow

> **LLM ở bìa → Operation Spec (validated) → Deterministic Executor → Read-back Verify.**
> Move the LLM OUT of the execution loop; confine it to a one-time "compile" step. (Backed by
> literature: ~79% of agent failures are spec/coordination, not infra — validate the spec, then let
> deterministic code execute.)

```
Natural-language command
   │  (1) COMPILE  — the ONLY LLM step
   ▼
Operation Spec (typed + schema-validated)   e.g.
   { op:"set_parameter",
     target:{ category:"Doors", where:[{parameter:"Fire Rating", operator:"equals", value:"NR"}] },
     set:{ parameter:"Comments", value:"Cap nhat" } }
   │  (2) RESOLVE + PLAN  — NO LLM
   ▼   • category "Doors" → BuiltInCategory OST_Doors
       • resolve each parameter → real name + SCOPE (instance vs type); if not found → STOP + suggest candidates
       • filter to the EXACT matching set in code (read values at the correct scope, compare)
       • if the SET param is a TYPE param → warn it changes ALL instances of those types (collateral)
   │  (3) PREVIEW + APPROVE  (human-in-the-loop for writes)
   ▼   "Set Comments='Cap nhat' on exactly these N doors; others untouched."
   │  (4) EXECUTE + VERIFY  — NO LLM
   ▼   • one transaction; set on the exact targets
       • READ-BACK: matched all hold the new value? a sample of EXCLUDED elements unchanged?
       • report confirmed / mismatched / collateral
```

**The LLM never enumerates element ids or decides matches.** It only emits the Spec.

## 4. Revit facts you must encode (hard-won, verified on R26)

| Fact | Implication |
|------|-------------|
| `Element.LookupParameter(name)` searches the element's **own** params (instance for a FamilyInstance) | Type params (Fire Rating) need the **type element** (`doc.GetElement(e.GetTypeId())`) |
| Param name match is **exact** (no fuzzy) | Normalize names (lowercase, strip non-alphanumerics) when resolving; keep the real name for the API call |
| Setting a **type** parameter changes ALL instances of that type | Must warn: "affects N instances of M types", not just the matched subset |
| Filter/UI uses **BuiltInCategory** ("OST_Doors"), not "Doors" | Resolve human category → BuiltInCategory first |
| Batch write should be **one transaction**; verify by read-back | Atomicity + provenance; roll back if verify fails |
| Door type names often encode the rating, e.g. `36" x 84" (180 MIN)` | Don't rely on the name; read the actual `Fire Rating` type param |

Operators worth supporting (generic, any param): `equals, not_equals, contains, not_contains,
matches (regex), not_matches, greater, less, greater_equal, less_equal, exists, not_exists`.
Numeric ops parse a **leading number** ("90 MIN" → 90) so `> 60` works; `equals/contains` are
string-based (trim, case-insensitive). `matches` is a great fit for "X MIN format" compliance.

## 5. Live verification (TS impl, on R26)

- `Doors`, `Fire Rating [type] == "180 MIN"` → matched **14/142** (only the 180 MIN doors). ✓
- Execute on 1 door (combined TYPE filter `Fire Rating=180 MIN` + INSTANCE filter `Mark=S10`,
  set `Comments`) → wrote 1/1, **read-back verified 1/1, 0 collateral**; confirmed independently;
  reverted. ✓
- Agent (LLM) emitted the Spec and called the safe tool — never raw find/set.

---

## 6. Reference implementation (TypeScript — the algorithm to port)

> These run against an injectable client interface so they're unit-testable with a fake model.
> In C# you replace the client with **direct Revit API calls** (see §8).

### 6.1 Operation Spec (Zod)
```ts
export const COMPARE_OPS = ['equals','not_equals','contains','not_contains','matches','not_matches',
  'greater','less','greater_equal','less_equal','exists','not_exists'] as const;
export type CompareOp = (typeof COMPARE_OPS)[number];

export const whereClauseSchema = z.object({
  parameter: z.string().min(1),
  operator: z.enum(COMPARE_OPS),
  value: z.union([z.string(), z.number(), z.boolean()]).optional(),
});
export const setParameterWhereSchema = z.object({
  op: z.literal('set_parameter'),
  target: z.object({ category: z.string().min(1), where: z.array(whereClauseSchema).default([]) }),
  set: z.object({ parameter: z.string().min(1), value: z.union([z.string(), z.number(), z.boolean()]) }),
});
```

### 6.2 Value comparison (pure, predictable)
```ts
function norm(x: unknown): string { return String(x ?? '').trim().toLowerCase(); }
function isPresent(x: unknown): boolean { return x !== null && x !== undefined && String(x).trim() !== ''; }
function leadingNumber(x: unknown): number | null {
  if (typeof x === 'number') return Number.isFinite(x) ? x : null;
  const m = String(x ?? '').match(/-?\d+(\.\d+)?/); if (!m) return null;
  const n = Number(m[0]); return Number.isFinite(n) ? n : null;
}
function safeRegex(p: unknown): RegExp | null { try { return new RegExp(String(p), 'i'); } catch { return null; } }

export function compareValue(actual: unknown, op: CompareOp, target?: unknown): boolean {
  if (op === 'exists') return isPresent(actual);
  if (op === 'not_exists') return !isPresent(actual);
  if (!isPresent(actual)) return op === 'not_equals' || op === 'not_contains' || op === 'not_matches';
  switch (op) {
    case 'equals': return norm(actual) === norm(target);
    case 'not_equals': return norm(actual) !== norm(target);
    case 'contains': return norm(actual).includes(norm(target));
    case 'not_contains': return !norm(actual).includes(norm(target));
    case 'matches': { const re = safeRegex(target); return re ? re.test(String(actual)) : false; }
    case 'not_matches': { const re = safeRegex(target); return re ? !re.test(String(actual)) : false; }
    case 'greater': case 'less': case 'greater_equal': case 'less_equal': {
      const a = leadingNumber(actual), t = leadingNumber(target);
      if (a === null || t === null) return false;
      if (op === 'greater') return a > t;
      if (op === 'less') return a < t;
      if (op === 'greater_equal') return a >= t;
      return a <= t;
    }
    default: return false;
  }
}
```

### 6.3 Parameter resolver (discover real name + scope)
```ts
export function normalizeParamName(s: string): string { return s.toLowerCase().replace(/[^a-z0-9]/g, ''); }

export async function resolveParameter(client, category, humanName, opts = {}) {
  const want = normalizeParamName(humanName);
  const instances = await client.listElements(category, { onlyInstances: true, limit: opts.scanLimit ?? 50 });
  if (!instances.length) return { found: false, candidates: [], note: `No instances in "${category}".` };

  // sample up to N instances spanning DISTINCT types (params can vary by family/type)
  const seenTypes = new Set<number>(); const samples = [];
  for (const e of instances) { const t = e.typeId ?? -1; if (!seenTypes.has(t)) { seenTypes.add(t); samples.push(e); } if (samples.length >= (opts.maxSamples ?? 5)) break; }

  const instanceParams = new Map(), typeParams = new Map(); const seenTypeInfo = new Set<number>();
  for (const inst of samples) {
    const info = await client.getElementInfo(inst.id);                 // INSTANCE params
    for (const p of info.parameters) instanceParams.set(normalizeParamName(p.name), p);
    if (info.typeId != null && !seenTypeInfo.has(info.typeId)) {
      seenTypeInfo.add(info.typeId);
      const t = await client.getElementInfo(info.typeId);              // TYPE params
      for (const p of t.parameters) typeParams.set(normalizeParamName(p.name), p);
    }
  }
  const inst = instanceParams.get(want); if (inst) return { found:true, realName:inst.name, scope:'instance', storageType:inst.storageType, isReadOnly:inst.isReadOnly, candidates:[] };
  const typ = typeParams.get(want);      if (typ)  return { found:true, realName:typ.name,  scope:'type',     storageType:typ.storageType,  isReadOnly:typ.isReadOnly,  candidates:[] };
  // not found → suggest candidates (substring matches first)
  const all = [...new Set([...[...instanceParams.values()].map(p=>p.name), ...[...typeParams.values()].map(p=>p.name)])];
  const close = all.filter(n => { const nn = normalizeParamName(n); return nn.includes(want) || want.includes(nn); });
  return { found:false, candidates:(close.length?close:all).sort() };
}
```

### 6.4 Category resolver
```ts
function normCat(s){ return s.toLowerCase().replace(/[^a-z0-9]/g,''); }
export async function resolveCategory(client, human) {
  const h = human.trim();
  if (/^ost_/i.test(h)) return { builtInCategory: h, name: h };
  const cats = await client.listCategories();           // [{name, builtInCategory}]
  const want = normCat(h);
  const hit = cats.find(c=>normCat(c.name)===want) ?? cats.find(c=>normCat(c.builtInCategory)===want) ?? cats.find(c=>normCat(c.builtInCategory)===`ost${want}`);
  if (hit) return { builtInCategory: hit.builtInCategory, name: hit.name };
  throw new CategoryNotFoundError(human, cats.filter(c=>normCat(c.name).includes(want)).map(c=>c.name));
}
```

### 6.5 Matcher (scope-aware filtering, shared by query + set)
```ts
async function applyClause(client, category, candidates, resolved, clause, maxElements) {
  const realName = resolved.realName;
  if (resolved.scope === 'instance') {
    const found = await client.findElements(category, { fields:[realName], limit:maxElements }); // values in one call
    const byId = new Map(found.map(f => [f.id, f.fields[realName]]));
    return candidates.filter(e => compareValue(byId.get(e.id), clause.operator, clause.value));
  }
  // TYPE scope: read once per distinct type, map back to instances
  const typeIds = [...new Set(candidates.map(e=>e.typeId).filter(t=>t!=null))];
  const byType = new Map();
  for (const t of typeIds) { const pv = await client.getParameter(t, realName); byType.set(t, pv?.value ?? null); }
  return candidates.filter(e => e.typeId!=null && compareValue(byType.get(e.typeId), clause.operator, clause.value));
}

export async function matchElements(client, category, where, opts = {}) {
  const max = opts.maxElements ?? 5000;
  const allInstances = await client.listElements(category, { onlyInstances:true, limit:max });
  let matched = allInstances; const filters = [];
  for (const clause of where) {
    const rp = await resolveParameter(client, category, clause.parameter);
    if (!rp.found) throw new ParamNotFoundError(clause.parameter, category, rp.candidates);
    filters.push({ parameter: rp.realName, scope: rp.scope, operator: clause.operator, value: clause.value });
    matched = await applyClause(client, category, matched, rp, clause, max);
  }
  return { allInstances, matched, filters };
}
```

### 6.6 Executor (set + verify) — the core
```ts
export async function runSetParameterWhere(client, spec, opts = {}) {
  const max = opts.maxElements ?? 5000;
  const { builtInCategory, name: categoryName } = await resolveCategory(client, spec.target.category);

  const setParam = await resolveParameter(client, builtInCategory, spec.set.parameter);
  if (!setParam.found) throw new ParamNotFoundError(spec.set.parameter, categoryName, setParam.candidates);
  if (setParam.isReadOnly) throw new Error(`"${setParam.realName}" is read-only.`);

  const { allInstances, matched, filters } = await matchElements(client, builtInCategory, spec.target.where, { maxElements:max });
  const matchedIds = matched.map(e=>e.id);

  // targets + scope (warn on type-param writes)
  let targets, affectedTypeIds; const warnings = [];
  if (setParam.scope === 'instance') { targets = matchedIds; }
  else {
    affectedTypeIds = [...new Set(matched.map(e=>e.typeId).filter(t=>t!=null))];
    targets = affectedTypeIds;
    const n = allInstances.filter(e=>e.typeId!=null && affectedTypeIds.includes(e.typeId)).length;
    warnings.push(`"${setParam.realName}" is a TYPE parameter — changes ${affectedTypeIds.length} type(s), affecting ALL ${n} instance(s), not only the ${matchedIds.length} matched.`);
  }

  const excluded = allInstances.filter(e=>!matchedIds.includes(e.id));
  const plan = { category:categoryName, filters, setParameter:setParam.realName, setScope:setParam.scope,
    value:spec.set.value, totalInCategory:allInstances.length, matchedCount:matchedIds.length,
    excludedCount:excluded.length, matchedSample:matched.slice(0,10), affectedTypeIds, warnings };
  if (!opts.execute) return { plan, executed:false };

  // collateral baseline (instance writes) → execute (atomic) → read-back verify
  const collateralIds = setParam.scope==='instance' ? excluded.slice(0,15).map(e=>e.id) : [];
  const before = await readValues(client, collateralIds, setParam.realName);
  const batch = await client.setParameterBatch(targets, setParam.realName, spec.set.value, { atomic:true });
  const verifyIds = setParam.scope==='instance' ? matchedIds : targets;
  const after = await readValues(client, verifyIds, setParam.realName);
  const want = norm(spec.set.value);
  const confirmed = after.filter(v=>norm(v.value)===want).length;
  const mismatched = after.filter(v=>norm(v.value)!==want);
  const afterColl = await readValues(client, collateralIds, setParam.realName);
  const collateral = afterColl.filter((v,i)=> norm(v.value)!==norm(before[i]?.value));
  return { plan, executed:true, batch, verify:{ expected:verifyIds.length, confirmed, mismatched, collateralChecked:collateralIds.length, collateral } };
}
```

### 6.7 Client interface (what the executor needs)
```ts
interface RevitClient {
  listCategories(): Promise<{name:string; builtInCategory:string}[]>;
  listElements(category, opts?): Promise<{id:number; name:string; typeId:number|null}[]>;
  findElements(category, {filters?, fields?, limit?}): Promise<{id; name; typeId; fields:Record<string,unknown>}[]>;
  getElementInfo(id): Promise<{id; name; typeId:number|null; parameters:{name;storageType;isReadOnly;value;valueString}[]}>;  // INSTANCE params
  getParameter(id, name): Promise<{value; valueString; ...}|null>;   // null if not found
  setParameterBatch(ids, name, value, {atomic?}): Promise<{total; succeeded; failed; errors:{id;code;error}[]}>;
}
```

## 7. Agent integration (so the LLM can't go wrong)

- Expose **two** high-level tools to the LLM and **nothing else** for parameters:
  - `revit_query_where({ category, where[] })` → count + sample (read).
  - `revit_update_where({ category, where[], set_parameter, set_value, execute })` → plan; on `execute=true` writes + verifies.
- **Disallow** the raw `find_elements` / `set_parameter` / `set_parameter_batch` tools for the LLM
  (safe mode). The LLM emits the Spec; deterministic code does the rest.
- Writes: dry-run by default; `execute=true` requires human approval; everything is audited.

System-prompt rule that worked:
> "To find or change parameters across elements you MUST use `revit_query_where` / `revit_update_where`
> — never hand-build find + set. They handle instance-vs-TYPE scope and read-back verify. If a
> parameter name isn't found, the tool returns candidates — pick from those or ask; do not guess."

## 8. Porting to C# (RevitLocalAssistant) — *simpler & more reliable in-process*

Because RevitLocalAssistant is an **in-process Revit add-in**, you don't need MCP/HTTP/JSON at all —
call the Revit API directly. Map the client interface to the API:

| Reference (TS client) | C# (in-process Revit API) |
|---|---|
| `resolveCategory("Doors")` | `BuiltInCategory.OST_Doors` (map by `Category.Name` via `doc.Settings.Categories`, or `Enum.TryParse`) |
| `listElements(cat)` | `new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements()` |
| `getElementInfo(instance)` params | iterate `element.Parameters` (instance) |
| type params | `var t = doc.GetElement(e.GetTypeId()) as ElementType; t.LookupParameter(name)` (or `((FamilyInstance)e).Symbol`) |
| `getParameter(id,name)` | `element.LookupParameter(name)` (instance) or on the type element |
| `setParameterBatch` | one `Transaction`; `param.Set(value)` per target; commit |
| read-back verify | re-read the param after `Set`; if any mismatch/collateral → `transaction.RollBack()` and report |

**C# executor sketch (the deterministic core):**
```csharp
// 1) resolve category → BuiltInCategory
// 2) resolve set param scope: instance? element.LookupParameter ; type? typeElem.LookupParameter (normalize names)
// 3) collect instances; for each where-clause, read value at the resolved scope and Compare(op, value)
//    - type-scope: read once per GetTypeId(), reuse
// 4) build matched set; if set-param is type-scope, warn (affects all instances of those types)
using (var tx = new Transaction(doc, "Assistant: set param"))
{
    tx.Start();
    foreach (var el in matched) targetParam(el).Set(value);   // instance scope; for type scope set on the type element(s)
    // read-back verify
    int ok = matched.Count(el => Norm(Read(targetParam(el))) == Norm(value));
    bool collateral = SampleExcluded().Any(el => Read(targetParam(el)) != baseline[el]);
    if (ok != matched.Count || collateral) { tx.RollBack(); return Error("verify failed"); }
    tx.Commit();
}
```
- Keep `Compare(actual, op, target)` exactly as in §6.2 (string/regex/leading-number).
- Keep the **LLM-at-edge** rule: LLM → JSON Operation Spec (deserialize to a C# record + validate) →
  this executor. The LLM never gets the element ids.
- In-process gives you a huge reliability win: a single `Transaction` with **rollback on failed
  verify** = atomic + provably-correct (no half-applied edits, no collateral).

## 9. Suggested next steps in RevitLocalAssistant
1. Add the `OperationSpec` record + a JSON schema/validator (the LLM's only output contract).
2. Implement `ParameterResolver` (instance/type scope + normalize + candidates) and `Comparer` (§6.2).
3. Implement `SetParameterWhereExecutor` with the Transaction + read-back verify + rollback.
4. Add `QueryWhere` (count/sample) for read questions.
5. Restrict the LLM to emit-spec-only; remove any direct "set parameter by id list" tool.
6. Unit-test with a fake/mock doc reproducing the bug (doors: Fire Rating = TYPE param, Comments =
   instance) → assert only matched instances change, zero collateral. (This test caught the bug in TS.)

---

*The full working TypeScript reference lives in `D:\AIProjects\BIMClaw\src\revit\` (`spec.ts`,
`compare.ts`, `param-resolver.ts`, `category-resolver.ts`, `match.ts`, `executor.ts`, `query.ts`) and
`src\connectors\revit\` (`types.ts`, `client.ts`), with tests in `tests\revit-*.test.ts` and
`tests\helpers\fake-revit.ts`. Read those for exact details when porting.*
