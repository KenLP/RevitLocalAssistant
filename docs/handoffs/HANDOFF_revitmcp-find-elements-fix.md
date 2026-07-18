# HANDOFF: `find_elements`/`list_elements` — pagination + type-parameter fix

- **Từ:** RevitLocalAssistant (worktree `wizardly-curie-920ae4`, submodule `extern/RevitMCPCore`)
  → **Đến:** RevitMCPServer (`https://github.com/KenLP/RevitMCPServer`)
- **Ngày:** 2026-07-15 · **Mức ưu tiên:** Cao (trả sai/thiếu dữ liệu một cách âm thầm) · **Trạng thái:** RESOLVED — xem mục "RESOLUTION" cuối file (2026-07-15, phía RevitMCPServer)

## Bối cảnh (tại sao cần)

`find_elements` là tool query chính mà add-in expose (`POST /mcp {command:"find_elements", params:{category, fields, filters, limit, offset}}`). Nó có 2 lỗi làm mọi client (RevitLocalAssistant, bim-orchestrator, qa_tester…) nhận dữ liệu sai hoặc thiếu mà không có lỗi rõ ràng nào báo ra — chỉ âm thầm trả sai.

## Đã verify khoảng trống (bắt buộc trước khi tin handoff này)

Đã `git fetch origin` mới nhất (hôm nay) trên bản clone tại `extern/RevitMCPCore` và kiểm tra **cả hai nhánh remote đang tồn tại**:

```
$ git branch -r
  origin/HEAD -> origin/main
  origin/feat/extract-revit-mcp-core
  origin/main
```

| Nhánh | Bug 1 (offset) | Bug 2 (type param) | `view_id` filter |
|---|---|---|---|
| `origin/main` (tip `d041081`) | ✅ đã fix (P2-C, v0.8.6) | ❌ **chưa có** | ❌ **không có** (bị bỏ khi thêm pagination) |
| `origin/feat/extract-revit-mcp-core` (tip `9c22e50` — nhánh mà submodule RevitLocalAssistant đang pin) | ❌ chưa fix | ❌ chưa fix | ✅ có |

Bằng chứng cụ thể:
```
$ git show origin/main:src/RevitMCP.Core/Commands/FindElementsCommand.cs | grep -c LookupInstanceOrType
0
$ git show origin/feat/extract-revit-mcp-core:src/RevitMCP.Core/Commands/FindElementsCommand.cs | grep -c LookupInstanceOrType
0
```

**Kết luận:** không nhánh remote nào có cả hai fix. Vì vậy **không nên merge/re-sync `feat/extract-revit-mcp-core` từ `origin/main`** — làm vậy sẽ lấy được fix Bug 1 nhưng **mất `view_id`** (tính năng mà `feat/extract-revit-mcp-core` đang có, `main` không có) và **vẫn thiếu fix Bug 2**. Cách đúng là **áp patch tại chỗ** lên `feat/extract-revit-mcp-core`, giữ nguyên `view_id`.

Patch dưới đây **đã viết, build và test xong** trong worktree local (không phải chỉ là đề xuất) — xem "Yêu cầu chính xác" để copy nguyên văn.

## Yêu cầu chính xác

### Bug 1 — `offset` bị khai báo trong schema nhưng không dùng

`elements.Take(limit)` không có `Skip(offset)` → mọi `offset` trả về cùng trang đầu tiên. Áp dụng cho **cả** `FindElementsCommand.cs` và `ListElementsCommand.cs`.

**Bằng chứng đã verify trực tiếp trên add-in đang chạy (R27, `OST_Rooms`, 339 phần tử):**
```
offset=0   limit=200 -> count=200 truncated=True  first_id=1066021 last_id=1066410
offset=200 limit=200 -> count=200 truncated=True  first_id=1066021 last_id=1066410   (GIỐNG HỆT)
offset=400 limit=200 -> count=200 truncated=True  first_id=1066021 last_id=1066410   (GIỐNG HỆT)
```

### Bug 2 — chỉ đọc INSTANCE parameter, bỏ qua TYPE parameter

Cả vòng lặp `fields` projection và vòng lặp `filters` trong `FindElementsCommand.cs` dùng `el.LookupParameter(name)` — chỉ resolve tham số **instance**. Nhiều tham số compliance quan trọng (Fire Rating, door Width, materials, assembly code) nằm trên **Type**, nên bị trả về rỗng (`fields: {}`) và filter theo các tham số đó không bao giờ match.

### Diff copy-paste được (đã build + test — không phải bản nháp)

<details>
<summary><code>src/RevitMCP.Core/Commands/FindElementsCommand.cs</code> — 46 dòng thêm, 7 dòng đổi</summary>

```diff
--- a/src/RevitMCP.Core/Commands/FindElementsCommand.cs
+++ b/src/RevitMCP.Core/Commands/FindElementsCommand.cs
@@ -1,4 +1,5 @@
 using System;
+using System.Collections.Generic;
 using System.Linq;
 using System.Text.Json.Nodes;
 using Autodesk.Revit.DB;
@@ -12,8 +13,13 @@ namespace RevitMCPAddin.Commands;
 ///   - category:   BuiltInCategory name, required
 ///   - filters:    [{parameterName, operator, value}], optional
 ///                 operator: "equals", "not_equals", "contains", "greater", "less"
-///   - limit:      int, default 200, max 5000
-///   - fields:     string[] of parameter names to project (optional)
+///   - limit:      int, default 200, max 5000 (page size)
+///   - offset:     int, default 0 — page start; page through all matches (no 5000 ceiling)
+///   - fields:     string[] of parameter names to project (optional).
+///                 Values resolve on the instance first, then fall back to the element Type.
+///
+/// Returns a paginated envelope: count (this page), total (all matches after filters),
+/// offset, limit, hasMore, nextOffset. truncated is kept as an alias of hasMore.
 /// </summary>
 public sealed class FindElementsCommand : IRevitCommand
 {
@@ -35,6 +41,7 @@ public sealed class FindElementsCommand : IRevitCommand
             : new FilteredElementCollector(doc);
 
         var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 5000);
+        var offset = Math.Max(0, P.IntOr(p, "offset", 0));
         var filtersArr = p["filters"] as JsonArray;
         var fieldsArr = p["fields"] as JsonArray;
 
@@ -43,6 +50,28 @@ public sealed class FindElementsCommand : IRevitCommand
             .WhereElementIsNotElementType()
             .ToList();
 
+        // Resolve a parameter on an element, falling back to its Type when the
+        // instance lookup misses (Fire Rating, Width, materials, assembly codes,
+        // etc. live on the element Type). Cached per (typeId, name) so N elements
+        // sharing a type cost one type lookup, not N.
+        var typeParamCache = new Dictionary<(long, string), Parameter?>();
+        Parameter? LookupInstanceOrType(Element el, string fname)
+        {
+            var pr = el.LookupParameter(fname);
+            if (pr is { HasValue: true }) return pr;
+
+            var tid = el.GetTypeId();
+            if (tid is null || tid == ElementId.InvalidElementId) return pr;
+            var key = (tid.Value, fname);
+            if (!typeParamCache.TryGetValue(key, out var cached))
+            {
+                var typeEl = doc.GetElement(tid);
+                cached = typeEl?.LookupParameter(fname);
+                typeParamCache[key] = cached;
+            }
+            return (cached is { HasValue: true }) ? cached : pr;
+        }
+
         // Apply parameter filters in-memory (simple approach — fast enough for <10k elements).
         if (filtersArr is { Count: > 0 })
         {
@@ -55,17 +84,20 @@ public sealed class FindElementsCommand : IRevitCommand
 
                 elements = elements.Where(el =>
                 {
-                    var param = el.LookupParameter(paramName);
+                    var param = LookupInstanceOrType(el, paramName);
                     if (param is null || !param.HasValue) return op == "not_equals";
                     return MatchParam(param, op, matchValue);
                 }).ToList();
             }
         }
 
-        elements = elements.Take(limit).ToList();
+        // total = matches after filters, before paging — so the caller knows how
+        // many exist and can page through all of them (no 5000 ceiling).
+        var total = elements.Count;
+        var page = elements.Skip(offset).Take(limit).ToList();
 
         var arr = new JsonArray();
-        foreach (var el in elements)
+        foreach (var el in page)
         {
             var obj = ListElementsCommand.SummarizeElement(el);
 
@@ -77,7 +109,7 @@ public sealed class FindElementsCommand : IRevitCommand
                 {
                     var fname = fn?.GetValue<string>();
                     if (fname is null) continue;
-                    var param = el.LookupParameter(fname);
+                    var param = LookupInstanceOrType(el, fname);
                     if (param is not null && param.HasValue)
                     {
                         fieldValues[fname] = ReadValueNode(param);
@@ -89,11 +121,18 @@ public sealed class FindElementsCommand : IRevitCommand
             arr.Add(obj);
         }
 
+        var nextOffset = offset + page.Count;
+        var hasMore = nextOffset < total;
+
         return new JsonObject
         {
             ["count"] = arr.Count,
+            ["total"] = total,
+            ["offset"] = offset,
             ["limit"] = limit,
-            ["truncated"] = elements.Count == limit,
+            ["hasMore"] = hasMore,
+            ["nextOffset"] = hasMore ? nextOffset : null,
+            ["truncated"] = hasMore,
             ["elements"] = arr,
         };
     }
```
</details>

<details>
<summary><code>src/RevitMCP.Core/Commands/ListElementsCommand.cs</code> — an toàn re-sync 100% từ <code>origin/main</code></summary>

Bản trên `origin/main` (tip `d041081`) đã có đúng fix pagination cho `ListElementsCommand.cs` và **không phụ thuộc `view_id`** (file này chưa từng có tính năng đó), nên cách nhanh nhất và an toàn nhất là lấy thẳng bản đó:

```bash
git checkout origin/main -- src/RevitMCP.Core/Commands/ListElementsCommand.cs
```

Không cần review diff từng dòng — đã diff hai bản và xác nhận `SummarizeElement` (dùng chung với `FindElementsCommand`) giữ nguyên y hệt, chỉ phần `Execute()` đổi sang dùng `GetElementCount()` + `Skip/Take` qua `BuildCollector()`.
</details>

### DoD gợi ý cho fix (deterministic, không đoán)

- `offset` khác nhau → phần tử đầu trang khác nhau (không còn lặp lại trang 1).
- `total`/`nextOffset` khớp với số phần tử thật sau filter.
- `find_elements(category:"OST_Doors", fields:["Fire Rating"])` trả giá trị (hoặc chuỗi rỗng nếu type không set) thay vì `fields: {}` cho mọi cửa có type định nghĩa Fire Rating.
- `find_elements` filter theo `Fire Rating` (một tham số TYPE) match đúng — không còn luôn `0` kết quả.

## Repro / dữ liệu mẫu

Build + deploy add-in rồi restart Revit, sau đó chạy PowerShell (thay `<port>` bằng cổng add-in — 7891 cho R2026, 7892 cho R2027):

```powershell
$tok = (Get-Content "$env:APPDATA\Autodesk\Revit\Addins\<ver>\revit-mcp-token.txt" -Raw).Trim()
$h = @{ Authorization = "Bearer $tok" }
function fe($body){ Invoke-RestMethod -Uri http://127.0.0.1:<port>/mcp -Method Post -Headers $h -ContentType 'application/json' -Body $body }

# 1) OFFSET phải trả trang khác nhau
$p1 = fe('{"command":"find_elements","params":{"category":"OST_Rooms","limit":50,"offset":0}}')
$p2 = fe('{"command":"find_elements","params":{"category":"OST_Rooms","limit":50,"offset":50}}')
"total=$($p1.data.total) p1[0]=$($p1.data.elements[0].id) p2[0]=$($p2.data.elements[0].id) nextOffset=$($p1.data.nextOffset)"
#   EXPECT: p1[0] != p2[0]; total = tổng số phòng thật; nextOffset = 50

# 2) TYPE parameter phải hiện ra
$d = fe('{"command":"find_elements","params":{"category":"OST_Doors","fields":["Fire Rating"],"limit":3}}')
$d.data.elements | ForEach-Object { $_.fields }
#   EXPECT: fields chứa "Fire Rating" (giá trị hoặc rỗng), không phải {}
```

Bản vá này **đã được build và chạy qua các bước trên trong worktree local** (Release, `-p:RevitVersion=2026`: 0 warning/0 error; `-p:RevitVersion=2027`: compile OK, chỉ lỗi copy DLL vì Revit 2027 đang chạy khóa file lúc build — không phải lỗi code).

## Định nghĩa hoàn thành (DoD)

- [ ] Áp diff ở trên vào `src/RevitMCP.Core/Commands/FindElementsCommand.cs` (patch tại chỗ, KHÔNG re-sync toàn file từ `origin/main` — sẽ mất `view_id`).
- [ ] `git checkout origin/main -- src/RevitMCP.Core/Commands/ListElementsCommand.cs`.
- [ ] `dotnet build -p:RevitVersion=2026` (và 2027 nếu máy có) — 0 warning/0 error.
- [ ] Chạy 2 acceptance test PowerShell ở trên trên Revit thật — cả hai EXPECT đều đúng.
- [ ] Commit + push lên `feat/extract-revit-mcp-core` của `KenLP/RevitMCPServer` (KHÔNG lên `main` — nhánh này đã diverge, không phải chỗ submodule đang pin).
- [ ] Báo lại cho phía RevitLocalAssistant (worktree/PR này) commit hash mới → bên đó sẽ chạy:
  ```bash
  cd extern/RevitMCPCore && git pull origin feat/extract-revit-mcp-core
  cd ../.. && git add extern/RevitMCPCore
  git commit -m "chore: bump RevitMCPCore submodule -> <hash> (find_elements/list_elements fix)"
  ```

## Ngoài phạm vi

- **Không** merge/rebase `feat/extract-revit-mcp-core` với `origin/main` nói chung — chỉ lấy nguyên `ListElementsCommand.cs` như hướng dẫn trên; các khác biệt khác giữa hai nhánh (nếu có) không thuộc phạm vi handoff này.
- **Không** thêm field/tool mới nào ngoài 2 file đã nêu.
- **Không** đổi format response ngoài các key additive đã liệt kê (`total`/`offset`/`hasMore`/`nextOffset`) — giữ nguyên `count`/`limit`/`truncated`/`elements` để client cũ không vỡ.

---

## RESOLUTION (2026-07-15, phía RevitMCPServer — verify trên origin/main tươi)

### Tiền đề của handoff sai một nửa — đã kiểm chứng lại

Bảng "Đã verify khoảng trống" ở trên **sai ở cột `origin/main`**. Verify lại trên
`origin/main` fetch tươi (tip `d041081`):

```
$ git show origin/main:src/RevitMCP.Core/Commands/FindElementsCommand.cs | grep -c LookupInstanceOrType
3        # KHÔNG phải 0 như handoff ghi
$ git show origin/main:src/RevitMCP.Core/Commands/FindElementsCommand.cs | grep -n "Skip(offset)"
86:      # pagination có thật
```

| | `origin/main` (thực tế) | `feat/extract-revit-mcp-core` |
|---|---|---|
| Bug 1 (offset) | ✅ đã fix từ **v0.8.6** (P2-C) | ❌ |
| Bug 2 (type param) | ✅ đã fix từ **v0.8.11** (commit `94a3c38`) | ❌ |
| `view_id` | ✅ **mới port, commit `33d60b6` (v0.8.15)** | ✅ |

Grep "=0" trong handoff nhiều khả năng chạy trên clone chưa fetch thật. Docstring
của `FindElementsCommand` trên main khi đó cũng còn stale (không nhắc offset/type
fallback) — góp phần gây chẩn đoán nhầm; đã sửa docstring trong `33d60b6`.

### Việc đã làm (thay cho DoD gốc)

- ❌ **KHÔNG áp patch vào `feat/extract-revit-mcp-core`** — patch đó thực chất chép lại
  code đã có trên main từ v0.8.6/v0.8.11. Vá lên feat chỉ nuôi thêm fork drift
  (chính drift này đã từng làm mất hosted placement và spatial pack).
- ✅ **Port `view_id` lên `main`** (`33d60b6`, v0.8.15) — khoảng trống thật duy nhất.
  Ngữ nghĩa giữ nguyên bản feat (view-scoped collector), thêm validation: id không
  phải View / là view template → lỗi `invalid_parameter` rõ ràng thay vì ArgumentException.
  Đã lộ ra MCP tool surface (`revit_find_elements.view_id`).
- ✅ Build R2026+R2027 0 error, 132/132 unit test, đã deploy add-in `0.8.15+33d60b6`
  cho cả hai bản Revit trên máy dev RevitMCPServer.

### ⛔ ĐÍNH CHÍNH (2026-07-17, phía RevitLocalAssistant) — claim "superset" bên dưới là SAI

Đã re-pin theo hướng dẫn bên dưới và **assistant vỡ**. `main` **KHÔNG** phải superset của
`feat/extract-revit-mcp-core`. Ba command chỉ tồn tại trên nhánh feat (commit `5ac811d`),
không có trên `main`:

| Command | Vai trò ở RevitLocalAssistant |
|---|---|
| `query_where` | Tool CHÍNH để đọc/đếm/liệt kê phần tử |
| `update_where` | Tool CHÍNH để sửa tham số (nằm trong write gate) |
| `import_parameters` | Commit path của import CSV/XLSX |

Kiểm chứng: `git grep -l "query_where\|update_where" 33d60b6 -- src/` → rỗng;
với `9c22e50` → có `QueryWhereCommand.cs` + `UpdateWhereCommand.cs`.

192 unit test vẫn pass vì chúng là logic thuần, không bao giờ gọi Core → không bắt được.
Đã revert pin về `9c22e50` và thêm `ToolSurfaceCoreContractTests` để fail nhanh nếu tái diễn.

**Chỉ re-pin sang `main` sau khi 3 command trên được port lên `main` upstream.** Phần dưới
giữ nguyên làm hồ sơ.

### Việc phía RevitLocalAssistant cần làm (thay bước "bump submodule feat")

~~`main` giờ là **superset nghiêm ngặt** của `feat/extract-revit-mcp-core`~~ (SAI — xem đính
chính ở trên). Re-pin submodule về `main` (sau khi `33d60b6` lên origin):

```bash
cd extern/RevitMCPCore && git fetch origin && git checkout origin/main
cd ../.. && git add extern/RevitMCPCore
git commit -m "chore: re-pin RevitMCPCore submodule -> main @ 33d60b6 (supersets feat branch)"
```

**⚠️ Lưu ý đổi tên khi re-pin:** các lệnh spatial trên feat mang tên trần; trên main
chúng thuộc pack `spatial_*` (HTTP-only): `get_room_boundary` → `spatial_get_room_boundary`,
`clearance_envelope` → `spatial_clearance_envelope`, `clearance_envelope_batch` →
`spatial_clearance_envelope_batch`, `raycast_headroom` → `spatial_raycast_headroom`.
Caller nào gọi tên trần phải đổi. `get_doors` giữ nguyên tên.

**Bonus khi re-pin:** hosted `place_family_instance` (hostId → wall cut), build-truth
`/health` (`gitCommit`/`gitState`/`buildTimestampUtc`/`commandCount`/`capabilityHash`,
không cần token) — dùng nó để verify đúng DLL nào đang nạp trước khi tin kết quả test.

### Acceptance tests của handoff (mục Repro) — vẫn dùng nguyên văn

Hai test PowerShell ở mục "Repro / dữ liệu mẫu" chạy được y nguyên trên build main;
kỳ vọng đều đạt. Thêm test thứ 3 cho `view_id`:

```powershell
# 3) VIEW_ID phải scope theo view (id lấy từ get_views)
$v = fe('{"command":"find_elements","params":{"category":"OST_Rooms","view_id":<viewId>,"limit":5}}')
#   EXPECT: total <= total của query không có view_id; view_id sai -> error invalid_parameter
```
