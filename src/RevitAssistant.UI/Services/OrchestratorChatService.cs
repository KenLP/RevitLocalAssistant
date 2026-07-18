using System.Text.Json.Nodes;
using RevitAssistant.Llm;

namespace RevitAssistant.UI;

/// <summary>
/// The Phase 4 engine: runs the agentic loop between the local LLM and Revit.
///
///   user → LLM → tool calls
///     · echo_interpretation → shown as an interpretation bubble
///     · clarify             → question bubble, stop and await the user
///     · read tool           → executed immediately, result fed back to the LLM
///     · write tool          → dry-run, build a ChangePreview, STOP and await confirm
///   …loop until the LLM returns a plain-text answer.
///
/// On confirm, the write commits for real and the loop resumes so the LLM can
/// summarise the result. After a successful update_where commit, <see cref="UndoAsync"/>
/// can restore the previous parameter values.
///
/// Protocol invariant: every assistant tool_call gets exactly one tool response
/// before the next LLM call. The single pending write is the only call left
/// unanswered while we wait for the user; Confirm/Cancel always answers it.
/// </summary>
public sealed class OrchestratorChatService : IChatService
{
    // Which tools exist, which mutate the model, and how each one is previewed all
    // come from ToolPolicy (deny-by-default). See RevitAssistant.Llm.ToolPolicy.

    private const int MaxIterations = 6;

    private readonly ILlmClient _llm;
    private readonly IRevitBridge _revit;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly string? _modelSchemaJson;

    private readonly int _contextTokens;
    private readonly int _trimCeiling;    // start trimming above this
    private readonly int _trimTarget;     // trim down to this

    private readonly List<ChatMessage> _conversation = new();
    private readonly Dictionary<string, double> _levelElev = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResultTable> _turnTables = new();
    private PendingWrite? _pendingWrite;

    /// <summary>
    /// A model write that has been previewed and is awaiting the user's confirmation,
    /// pinned to the document and the exact outcome the user was shown. ConfirmAsync
    /// re-checks both before committing, so switching project or editing the model
    /// while the confirmation sits on screen can never commit against a different
    /// document or a different set of elements than the preview described.
    /// </summary>
    private sealed record PendingWrite(ToolCall Call, string DocumentKey, string Digest);
    private UndoPayload? _lastUndo;
    private ImportedTable? _pendingImport;
    private ImportPending? _pendingImportCommit;

    // Stores the before-values of a successful update_where for a one-shot undo.
    private sealed record UndoPayload(
        string SetParameter,
        IReadOnlyList<(long Id, string? Before)> Changes);

    public OrchestratorChatService(
        ILlmClient llm, IRevitBridge revit, string? modelSchemaJson = null, int contextTokens = 8192)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _modelSchemaJson = modelSchemaJson;
        _tools = ToolSpecAdapter.BuildToolSurface();
        _contextTokens = contextTokens > 0 ? contextTokens : 8192;
        _trimCeiling = (int)(_contextTokens * 0.90);   // never feed the model more than this
        _trimTarget = (int)(_contextTokens * 0.65);    // after trimming, aim for this
    }

    /// <summary>Estimated context fill (0..1) of the current conversation.</summary>
    private double CurrentUsage() =>
        Math.Min(1.0, (double)ContextEstimator.Estimate(_conversation) / _contextTokens);

    public async Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        // If an import dry-run is waiting for confirmation and the user types a
        // confirmation intent, commit directly without going through the LLM.
        if (_pendingImportCommit is not null && IsConfirmIntent(userInput))
            return await CommitImportAsync(ct).ConfigureAwait(false);

        // If a table is waiting, inject its summary so the LLM knows what to map.
        var content = userInput;
        if (_pendingImport is { } imp)
            content = BuildImportContext(imp) + "\n---\n" + userInput;

        _conversation.Add(ChatMessage.User(content));
        _pendingWrite = null;
        _lastUndo = null;           // new turn invalidates old undo
        _turnTables.Clear();
        var turn = await RunLoopAsync(ct).ConfigureAwait(false);
        return turn with { ContextUsage = CurrentUsage(), Tables = _turnTables.ToList() };
    }

    private static string BuildImportContext(ImportedTable table)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[Dữ liệu đã nhập từ \"");
        sb.Append(table.FileName);
        sb.Append("\" — ");
        sb.Append(table.TotalRowCount);
        sb.Append(" dòng, cột: ");
        sb.AppendLine(string.Join(", ", table.Columns));
        sb.AppendLine("Mẫu 5 dòng đầu:");
        sb.AppendLine(string.Join(" | ", table.Columns));
        foreach (var row in table.Rows.Take(5))
            sb.AppendLine(string.Join(" | ", row));
        sb.Append("]");
        return sb.ToString();
    }

    public ChatTurn IngestImport(ImportedTable table)
    {
        _pendingImport = table;
        var preview = table.Rows.Take(10).Select(r => r.ToList() as IReadOnlyList<string>).ToList();
        var resultTable = new ResultTable(table.Columns, preview, table.TotalRowCount,
            table.TotalRowCount > 10);
        var summary = $"Đã đọc **{table.FileName}** — {table.TotalRowCount} dòng, {table.Columns.Count} cột. " +
            "Mô tả bạn muốn làm gì với dữ liệu này.";
        return new ChatTurn(new[] { new ChatReply(summary) }, Tables: new[] { resultTable });
    }

    /// <summary>Clear the conversation (and any pending write) — the "new chat" action.</summary>
    public void Reset()
    {
        _conversation.Clear();
        _levelElev.Clear();
        _pendingWrite = null;
        _lastUndo = null;
        _pendingImport = null;
        _pendingImportCommit = null;
    }

    /// <summary>Compact snapshot of the recent backend conversation (for feedback logs).</summary>
    public string SnapshotContext()
    {
        var arr = new JsonArray();
        var start = Math.Max(0, _conversation.Count - 14);
        for (var i = start; i < _conversation.Count; i++)
        {
            var m = _conversation[i];
            var o = new JsonObject { ["role"] = m.Role.ToString() };
            if (!string.IsNullOrEmpty(m.Content)) o["content"] = Truncate(m.Content!, 800);
            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var tc in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["name"] = tc.FunctionName,
                        ["args"] = Truncate(tc.ArgumentsJson, 400),
                    });
                o["tool_calls"] = calls;
            }
            arr.Add(o);
        }
        return arr.ToJsonString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// On the first message, seed the system prompt. If no static schema was
    /// supplied, fetch the real levels + categories + active view + sample params
    /// from the active document so the model is grounded in names that actually exist.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_conversation.Count > 0) return;

        var schema = _modelSchemaJson ?? await TryBuildSchemaAsync(ct).ConfigureAwait(false);
        _conversation.Add(ChatMessage.System(AssistantPrompt.Build(schema)));
    }

    private async Task<string?> TryBuildSchemaAsync(CancellationToken ct)
    {
        try
        {
            var empty = new JsonObject();
            var levels = await _revit.CallAsync("list_levels", empty, false, ct).ConfigureAwait(false);
            var cats = await _revit.CallAsync("list_categories", empty, false, ct).ConfigureAwait(false);
            PopulateLevelOrder(levels);

            JsonObject? activeView = null;
            try { activeView = await _revit.CallAsync("get_active_view", empty, false, ct).ConfigureAwait(false); }
            catch { /* non-fatal — view info is a nice-to-have */ }

            var paramsByCategory = await SampleParamsAsync(cats, ct).ConfigureAwait(false);

            return ModelSchema.Build(levels, cats, activeView, paramsByCategory);
        }
        catch
        {
            return null;   // no document / dispatcher not ready → fall back to generic prompt
        }
    }

    // Architecturally important categories whose params the model must know regardless
    // of element count. Populous but less critical categories fill remaining slots.
    private static readonly string[] PriorityCats =
    {
        "OST_Walls", "OST_Floors", "OST_Doors", "OST_Windows",
        "OST_Rooms", "OST_StructuralColumns", "OST_Ceilings", "OST_Stairs",
    };

    private const int MaxSampleCats = 5;

    /// <summary>
    /// Samples real parameter names per category. Priority categories (Wall, Floor,
    /// Door, Room…) are always included first because they matter most for BIM
    /// queries regardless of element count. Remaining slots fill from the most
    /// populous categories that are not already covered.
    /// Wrapped in try/catch per category so a failure on one doesn't abort the rest.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> SampleParamsAsync(
        JsonObject? catsEnv, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();

        if (catsEnv?["data"]?["categories"] is not JsonArray arr) return result;

        // Build lookup: BIC → count (only categories present in this model)
        var available = arr
            .OfType<JsonObject>()
            .Select(o => (
                Bic: o["builtInCategory"]?.GetValue<string>(),
                Count: TryGetInt(o["instanceCount"]) ?? 0))
            .Where(x => !string.IsNullOrEmpty(x.Bic) && x.Count > 0)
            .ToDictionary(x => x.Bic!, x => x.Count, StringComparer.OrdinalIgnoreCase);

        // 1. Priority categories that exist in this model (in fixed order)
        var toSample = PriorityCats
            .Where(available.ContainsKey)
            .ToList();

        // 2. Fill remaining slots from most populous non-priority categories
        var extras = available.Keys
            .Where(k => !toSample.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(k => available[k])
            .Take(MaxSampleCats - toSample.Count);
        toSample.AddRange(extras);

        foreach (var bic in toSample.Take(MaxSampleCats))
        {
            try
            {
                var findArgs = new JsonObject { ["category"] = bic, ["limit"] = 1 };
                var findEnv = await _revit.CallAsync("find_elements", findArgs, false, ct).ConfigureAwait(false);
                if (!IsOk(findEnv)) continue;

                var firstId = TryGetLong(findEnv["data"]?["elements"]?[0]?["id"]);
                if (firstId is null) continue;

                var infoArgs = new JsonObject { ["id"] = firstId.Value };
                var infoEnv = await _revit.CallAsync("get_element_info", infoArgs, false, ct).ConfigureAwait(false);
                if (!IsOk(infoEnv)) continue;

                var names = new List<string>();
                if (infoEnv["data"]?["parameters"] is JsonArray pArr)
                {
                    foreach (var p in pArr)
                    {
                        var name = p?["name"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name) && name!.Length > 1 && !name.StartsWith("__"))
                            names.Add(name!);
                    }
                }

                if (names.Count > 0) result[bic!] = names;
            }
            catch { /* skip this category */ }
        }

        return result;
    }

    /// <summary>Capture level name → elevation so "group by Level" can sort low→high.</summary>
    private void PopulateLevelOrder(JsonObject levelsEnv)
    {
        _levelElev.Clear();
        if (levelsEnv["data"]?["levels"] is not JsonArray arr) return;
        foreach (var l in arr)
        {
            var name = l?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var elev = TryGetDouble(l?["elevationFeet"]) ?? TryGetDouble(l?["elevationMeters"]) ?? 0;
            _levelElev[name!] = elev;
        }
    }

    private IReadOnlyDictionary<string, double>? LevelOrderFor(string? groupBy) =>
        !string.IsNullOrWhiteSpace(groupBy) &&
        groupBy!.Contains("level", StringComparison.OrdinalIgnoreCase) &&
        _levelElev.Count > 0
            ? _levelElev
            : null;

    private static double? TryGetDouble(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        return null;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    public async Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
    {
        if (_pendingImportCommit is not null)
            return await CommitImportAsync(ct).ConfigureAwait(false);

        if (_pendingWrite is null)
            return new ChatTurn(new[] { new ChatReply("Không có thay đổi nào đang chờ xác nhận.") });

        var pending = _pendingWrite;
        _pendingWrite = null;
        _turnTables.Clear();

        var write = pending.Call;
        var replies = new List<ChatReply>();

        // The preview was computed against one document and one specific outcome. While it
        // sat on screen the user could have switched project, changed view, or edited the
        // model — committing now could touch a different element set than was approved.
        // Re-verify both; on any mismatch refuse and make the user preview again.
        var docKey = await DocumentKeyAsync(ct).ConfigureAwait(false);
        if (!string.Equals(docKey, pending.DocumentKey, StringComparison.Ordinal))
        {
            AppendToolResult(write, JsonResultError("document_changed",
                "Document changed between preview and confirm; write refused."));
            return new ChatTurn(new[] { new ChatReply(
                "⚠️ Tài liệu đã thay đổi kể từ lúc xem trước — chưa ghi gì cả. " +
                "Hãy yêu cầu lại để xem trước trên tài liệu hiện tại.", IsError: true) });
        }

        var recheck = await ExecuteAsync(write, dryRun: true, ct).ConfigureAwait(false);
        if (!IsOk(recheck) || !string.Equals(DigestOf(recheck), pending.Digest, StringComparison.Ordinal))
        {
            AppendToolResult(write, JsonResultError("preview_stale",
                "Model changed between preview and confirm; write refused."));
            return new ChatTurn(new[] { new ChatReply(
                "⚠️ Model đã thay đổi kể từ lúc xem trước nên kết quả sẽ khác — chưa ghi gì cả. " +
                "Hãy yêu cầu lại để xem trước bản mới.", IsError: true) });
        }

        var result = await ExecuteAsync(write, dryRun: false, ct).ConfigureAwait(false);
        AppendToolResult(write, result);

        // Build undo payload for a successful update_where
        var canUndo = false;
        if (write.FunctionName == "update_where" && IsOk(result))
        {
            _lastUndo = BuildUndoPayload(result);
            canUndo = _lastUndo is not null;
        }
        else
        {
            _lastUndo = null;
        }

        replies.Add(IsOk(result)
            ? new ChatReply("✅ " + SummaryOf(result, fallback: "Đã ghi thay đổi vào model."))
            : new ChatReply("❌ " + ErrorOf(result), IsError: true));

        var tail = await RunLoopAsync(ct).ConfigureAwait(false);
        replies.AddRange(tail.Replies);
        return new ChatTurn(replies, tail.Pending, CurrentUsage(), _turnTables.ToList(), canUndo);
    }

    private async Task<ChatTurn> CommitImportAsync(CancellationToken ct)
    {
        var pending = _pendingImportCommit!;
        _pendingImportCommit = null;
        _turnTables.Clear();

        var result = await ImportExecutor.CommitAsync(_revit, pending, ct).ConfigureAwait(false);

        if (result.Error is not null)
            return new ChatTurn(new[] { new ChatReply($"❌ Lỗi khi nhập: {result.Error}", IsError: true) });

        var msg = result.Failed == 0
            ? $"✅ Đã nhập thành công {result.Applied} phần tử."
            : $"⚠️ Đã nhập {result.Applied} phần tử. {result.Failed} lỗi.";
        return new ChatTurn(new[] { new ChatReply(msg) },
            ContextUsage: CurrentUsage(), Tables: _turnTables.ToList());
    }

    private static bool IsConfirmIntent(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "xác nhận" or "ok" or "yes" or "có" or "đồng ý" or "tiếp tục"
                 or "confirm" or "go" or "proceed" or "apply" or "thực hiện" or "nhận"
            || t.Contains("xác nhận") || t.Contains("đồng ý") || t.Contains("tiếp tục");
    }

    public void CancelPending()
    {
        if (_pendingImportCommit is not null)
        {
            _pendingImportCommit = null;
            return;
        }
        if (_pendingWrite is null) return;
        // Answer the dangling tool_call so the conversation stays well-formed.
        AppendToolResult(_pendingWrite.Call,
            JsonResultError("cancelled", "Người dùng đã hủy thao tác này."));
        _pendingWrite = null;
    }

    // ── Preview ↔ commit binding ─────────────────────────────────────────────

    /// <summary>
    /// Identity of the document a preview was computed against. Title + path only:
    /// isModified/heartbeat-style fields change constantly and would false-positive.
    /// </summary>
    private async Task<string> DocumentKeyAsync(CancellationToken ct)
    {
        var env = await _revit.CallAsync("get_document_info", new JsonObject(), false, ct)
                              .ConfigureAwait(false);
        var data = env["data"] as JsonObject;
        return string.Concat(data?["title"]?.ToString() ?? "", " ", data?["pathName"]?.ToString() ?? "");
    }

    /// <summary>
    /// Fingerprint of what a dry-run says WOULD happen — the matched elements and their
    /// before/after values. Comparing it at confirm time catches any model change that
    /// would make the commit differ from the preview the user approved.
    /// </summary>
    private static string DigestOf(JsonObject dryRunResult)
    {
        var payload = dryRunResult["data"]?.ToJsonString() ?? "";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    // ── Undo ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the parameter values that were overwritten by the last confirmed
    /// update_where. Groups elements by their before-value so a single
    /// set_parameter_batch call handles each unique value (often just one group).
    /// </summary>
    public async Task<ChatTurn> UndoAsync(CancellationToken ct = default)
    {
        if (_lastUndo is null)
            return new ChatTurn(new[] { new ChatReply("Không có thao tác nào để hoàn tác.") });

        var undo = _lastUndo;
        _lastUndo = null;

        // Group by before-value: one set_parameter_batch call per unique value.
        var groups = undo.Changes
            .GroupBy(c => c.Before ?? "")
            .ToList();

        var totalRestored = 0;
        var errorMessages = new List<string>();

        foreach (var group in groups)
        {
            var ids = new JsonArray();
            foreach (var (id, _) in group) ids.Add(id);

            var batchArgs = new JsonObject
            {
                ["ids"] = ids,
                ["parameterName"] = undo.SetParameter,
                ["value"] = group.Key,
                ["atomic"] = false,
            };

            var res = await _revit.CallAsync("set_parameter_batch", batchArgs, false, ct).ConfigureAwait(false);
            if (IsOk(res))
                totalRestored += group.Count();
            else
                errorMessages.Add(ErrorOf(res));
        }

        if (errorMessages.Count == 0)
            return new ChatTurn(new[] { new ChatReply(
                $"✅ Đã hoàn tác '{undo.SetParameter}' trên {totalRestored} phần tử.") });

        var failCount = undo.Changes.Count - totalRestored;
        var msg = totalRestored > 0
            ? $"⚠️ Hoàn tác một phần: {totalRestored} OK, {failCount} lỗi: {string.Join("; ", errorMessages)}"
            : $"❌ Hoàn tác thất bại: {string.Join("; ", errorMessages)}";
        return new ChatTurn(new[] { new ChatReply(msg, IsError: totalRestored == 0) });
    }

    /// <summary>Builds an undo payload from an update_where result envelope.</summary>
    private static UndoPayload? BuildUndoPayload(JsonObject result)
    {
        var data = result["data"] as JsonObject;
        var paramName = data?["setParameter"]?.GetValue<string>();
        if (string.IsNullOrEmpty(paramName)) return null;

        var results = data?["results"] as JsonArray;
        if (results is null) return null;

        var changes = new List<(long, string?)>();
        foreach (var r in results)
        {
            if (r is not JsonObject o) continue;
            if (o["ok"] is not JsonValue okV || !okV.TryGetValue<bool>(out var ok) || !ok) continue;
            var id = TryGetLong(o["id"]);
            if (id is null) continue;
            var before = o["before"]?.GetValue<string>();
            changes.Add((id.Value, before));
        }

        return changes.Count > 0 ? new UndoPayload(paramName!, changes) : null;
    }

    // ── Import handling ───────────────────────────────────────────────────────

    /// <summary>
    /// Handle the LLM's import_data tool call. Parses the mapping spec, runs a
    /// dry-run, and returns a <see cref="ChangePreview"/> for the confirm/cancel flow.
    /// Returns null if an error was already fed back to the LLM (and the loop should continue).
    /// </summary>
    private async Task<ChangePreview?> HandleImportDataAsync(ToolCall tc, CancellationToken ct)
    {
        if (_pendingImport is null)
        {
            AppendToolResult(tc, JsonResultError("no_data",
                "Chưa có dữ liệu nhập. Người dùng phải nhấn '📎 Nhập file' trước."));
            return null;
        }

        ImportSpec? spec;
        try { spec = ParseImportSpec(tc); }
        catch (Exception ex)
        {
            AppendToolResult(tc, JsonResultError("bad_spec", ex.Message));
            return null;
        }

        var dryRun = await ImportExecutor.DryRunAsync(_revit, _pendingImport, spec, ct)
            .ConfigureAwait(false);

        if (dryRun.HasError)
        {
            AppendToolResult(tc, JsonResultError("dry_run_failed", dryRun.Error!));
            return null;
        }

        // Feed a summary back to the LLM so it can explain to the user.
        var dryJson = BuildImportDryRunJson(dryRun);
        AppendToolResult(tc, JsonResultOk(dryJson));

        // Store for confirm
        _pendingImportCommit = new ImportPending(_pendingImport, spec, dryRun);
        _pendingImport = null;   // consumed — cleared so repeat calls don't stack

        return BuildImportPreview(dryRun, _pendingImportCommit.Table);
    }

    private static ImportSpec ParseImportSpec(ToolCall tc)
    {
        var a = tc.ParseArguments();
        var op = a["operation"]?.GetValue<string>()
            ?? throw new InvalidOperationException("import_data cần 'operation'.");

        if (string.Equals(op, "create_sheets", StringComparison.OrdinalIgnoreCase))
        {
            var numCol = a["numberColumn"]?.GetValue<string>()
                ?? throw new InvalidOperationException("create_sheets cần 'numberColumn'.");
            var nameCol = a["nameColumn"]?.GetValue<string>()
                ?? throw new InvalidOperationException("create_sheets cần 'nameColumn'.");
            return new CreateSheetsSpec(numCol, nameCol);
        }

        // update_parameters
        var cat = a["category"]?.GetValue<string>()
            ?? throw new InvalidOperationException("update_parameters cần 'category'.");
        var match = a["match"] as JsonObject
            ?? throw new InvalidOperationException("update_parameters cần 'match'.");
        var matchCol = match["column"]?.GetValue<string>()
            ?? throw new InvalidOperationException("match cần 'column'.");
        var matchParam = match["param"]?.GetValue<string>()
            ?? throw new InvalidOperationException("match cần 'param'.");

        var sets = new List<ColParamPair>();
        if (a["set"] is JsonArray setArr)
        {
            foreach (var s in setArr.OfType<JsonObject>())
            {
                var col = s["column"]?.GetValue<string>();
                var param = s["param"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(col) && !string.IsNullOrEmpty(param))
                    sets.Add(new ColParamPair(col!, param!));
            }
        }
        if (sets.Count == 0)
            throw new InvalidOperationException("update_parameters cần ít nhất một mục trong 'set'.");

        return new UpdateParamsSpec(cat, new ColParamPair(matchCol, matchParam), sets);
    }

    private static JsonObject BuildImportDryRunJson(ImportDryRunResult d) =>
        d.Spec is UpdateParamsSpec
            ? new JsonObject
            {
                ["matchedRows"] = d.MatchedRows.Count,
                ["unmatchedRows"] = d.UnmatchedRows.Count,
                ["totalRevitElements"] = d.TotalRevitElements,
            }
            : new JsonObject
            {
                ["sheetsToCreate"] = d.SheetsToCreate.Count,
                ["sheetsSkipped"] = d.SheetsSkipped.Count,
            };

    private static ChangePreview BuildImportPreview(ImportDryRunResult d, ImportedTable table)
    {
        if (d.Spec is UpdateParamsSpec up)
        {
            var title = $"Nhập từ {table.FileName}";
            var summary = $"Khớp {d.MatchedRows.Count}/{table.TotalRowCount} dòng với '{up.Match.Param}'. " +
                (d.UnmatchedRows.Count > 0 ? $"{d.UnmatchedRows.Count} dòng không tìm thấy." : "");
            var rows = d.MatchedRows.Take(20).Select(m =>
            {
                var sets = up.Sets.Select(s =>
                {
                    var ci = IndexOfCol(m.Columns, s.Column);
                    var v = ci >= 0 && ci < m.Row.Count ? m.Row[ci] : "?";
                    return $"{s.Param}={v}";
                });
                return new PreviewRow($"Dòng {m.RowNum} [{m.MatchValue}]",
                    string.Join(", ", sets));
            }).ToList();
            if (d.UnmatchedRows.Count > 0 && rows.Count < 20)
                foreach (var (rn, mv) in d.UnmatchedRows.Take(5))
                    rows.Add(new PreviewRow($"Dòng {rn} [{mv}]",
                        "⚠ Không tìm thấy trong Revit", IsFailure: true));
            return new ChangePreview(title, summary, rows, d.MatchedRows.Count);
        }
        else
        {
            var cs = (CreateSheetsSpec)d.Spec;
            var title = $"Tạo bản vẽ từ {table.FileName}";
            var summary = $"Sẽ tạo {d.SheetsToCreate.Count} bản vẽ mới. " +
                (d.SheetsSkipped.Count > 0 ? $"{d.SheetsSkipped.Count} đã tồn tại (bỏ qua)." : "");
            var rows = d.SheetsToCreate.Take(20)
                .Select(t => new PreviewRow(t.Number, t.Name))
                .ToList();
            return new ChangePreview(title, summary, rows, d.SheetsToCreate.Count);
        }
    }

    private static ChangePreview BuildConfirmExecPreview(ToolCall tc)
    {
        var a = tc.ParseArguments();
        return tc.FunctionName switch
        {
            "change_element_type" => new ChangePreview(
                "Đổi loại phần tử (Type)",
                $"Sẽ đổi type của phần tử ID {a["id"]} → typeId {a["typeId"]}.",
                [new PreviewRow($"ID {a["id"]}", $"→ typeId {a["typeId"]}")], 1),

            "set_level_elevation" => new ChangePreview(
                "Điều chỉnh cao độ tầng",
                $"⚠️ Sẽ đặt cao độ tầng ID {a["id"]} = {a["elevation"]} {a["units"]?.GetValue<string>() ?? "m"}. Ảnh hưởng toàn bộ phần tử trên tầng này.",
                [new PreviewRow($"Level ID {a["id"]}", $"elevation = {a["elevation"]} {a["units"]?.GetValue<string>() ?? "m"}")], 1),

            "apply_view_template" => new ChangePreview(
                "Áp dụng View Template",
                $"Sẽ áp dụng template '{a["templateName"]?.GetValue<string>() ?? a["templateId"]?.ToString() ?? "?"}' lên View ID {a["viewId"]}.",
                [new PreviewRow($"View ID {a["viewId"]}", $"template = {a["templateName"]?.GetValue<string>() ?? a["templateId"]?.ToString() ?? "?"}")], 1),

            "create_detail_line" => new ChangePreview(
                "Vẽ detail line",
                $"Sẽ vẽ một detail line trong {(a["viewId"] is not null ? $"View ID {a["viewId"]}" : "view đang mở")}.",
                [new PreviewRow("start", a["start"]?.ToJsonString() ?? "?"),
                 new PreviewRow("end", a["end"]?.ToJsonString() ?? "?")], 1),

            _ => new ChangePreview(tc.FunctionName, $"Xác nhận thực hiện '{tc.FunctionName}'.", [], 1),
        };
    }

    // ── Core loop ────────────────────────────────────────────────────────────

    private async Task<ChatTurn> RunLoopAsync(CancellationToken ct)
    {
        var replies = new List<ChatReply>();

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            // Slide the window so a long conversation never overflows the model's
            // context (which would silently drop the system prompt → wrong behaviour).
            ConversationTrimmer.TrimToFit(_conversation, _trimCeiling, _trimTarget);

            ChatResponse resp;
            try
            {
                resp = await _llm.ChatAsync(_conversation, _tools, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                replies.Add(new ChatReply($"Lỗi kết nối LLM: {ex.Message}", IsError: true));
                return new ChatTurn(replies);
            }

            _conversation.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = resp.TextContent,
                ToolCalls = resp.HasToolCalls ? resp.ToolCalls : null,
            });

            if (!resp.HasToolCalls)
            {
                if (!string.IsNullOrWhiteSpace(resp.TextContent))
                    replies.Add(new ChatReply(resp.TextContent!));
                return new ChatTurn(replies);
            }

            var calls = resp.ToolCalls;
            var reprompt = false;

            for (var k = 0; k < calls.Count; k++)
            {
                var tc = calls[k];

                // Deny-by-default. The model may only name tools in ToolPolicy; a
                // hallucinated name — or a real Core command we never exposed, such as
                // delete_elements — is answered with an error and never dispatched.
                if (!ToolPolicy.IsLlmCallable(tc.FunctionName))
                {
                    AppendToolResult(tc, JsonResultError("tool_not_allowed",
                        $"Tool '{tc.FunctionName}' không tồn tại. Chỉ dùng các tool đã được cung cấp."));
                    reprompt = true;
                    continue;
                }

                if (tc.FunctionName == "echo_interpretation")
                {
                    var echo = ParseEcho(tc);
                    if (echo is not null) replies.Add(new ChatReply(echo));
                    AppendToolResult(tc, JsonResultOk(new JsonObject { ["acknowledged"] = true }));
                    continue;
                }

                if (tc.FunctionName == "clarify")
                {
                    replies.Add(new ChatReply(ParseClarify(tc)));
                    AppendToolResult(tc, JsonResultOk(new JsonObject { ["asked"] = true }));
                    DeferRemaining(calls, k + 1);
                    return new ChatTurn(replies);
                }

                if (tc.FunctionName == "count_elements")
                {
                    // Virtual tool handled here: query via find_elements, then count /
                    // group deterministically in C# so the model never miscounts.
                    var counted = await CountElementsAsync(tc, ct).ConfigureAwait(false);
                    AppendToolResult(tc, counted);
                    TryAddTable(counted);
                    continue;
                }

                if (tc.FunctionName == "aggregate_elements")
                {
                    var agg = await AggregateElementsAsync(tc, ct).ConfigureAwait(false);
                    AppendToolResult(tc, agg);
                    TryAddTable(agg);
                    continue;
                }

                if (tc.FunctionName == "find_elements")
                {
                    // Intercepted so filtering runs client-side (rich operators +
                    // correct numeric/text matching), not via Core's limited filters.
                    var found = await FindElementsAsync(tc, ct).ConfigureAwait(false);
                    AppendToolResult(tc, found);
                    continue;
                }

                if (tc.FunctionName == "import_data")
                {
                    var importResult = await HandleImportDataAsync(tc, ct).ConfigureAwait(false);
                    if (importResult is not null)
                    {
                        DeferRemaining(calls, k + 1);
                        return new ChatTurn(replies, importResult);
                    }
                    // Error was fed back already via AppendToolResult; continue loop.
                    continue;
                }

                if (ToolPolicy.RequiresConfirmation(tc.FunctionName))
                {
                    // Every model write dry-runs first. Core runs ModelWrite commands in a
                    // transaction and rolls back on dryRun, so this both validates the
                    // arguments against the real model and captures the exact outcome the
                    // user is about to approve.
                    var dry = await ExecuteAsync(tc, dryRun: true, ct).ConfigureAwait(false);

                    if (!IsOk(dry))
                    {
                        // Feed the failure back silently and let the model self-correct
                        // (it often invents an id on the first try, then calls
                        // find_elements). Surfacing every intermediate failure as a red
                        // bubble just confuses the user; the final answer explains.
                        AppendToolResult(tc, dry);
                        DeferRemaining(calls, k + 1);
                        reprompt = true;
                        break;
                    }

                    _pendingWrite = new PendingWrite(
                        tc,
                        await DocumentKeyAsync(ct).ConfigureAwait(false),
                        DigestOf(dry));
                    DeferRemaining(calls, k + 1);

                    var preview = ToolPolicy.PreviewFor(tc.FunctionName) == PreviewStrategy.FromDryRun
                        ? PreviewBuilder.Build(tc, dry)
                        : BuildConfirmExecPreview(tc);
                    return new ChatTurn(replies, preview);
                }

                // Read tool — safe to run and feed straight back.
                var read = await ExecuteAsync(tc, dryRun: false, ct).ConfigureAwait(false);
                AppendToolResult(tc, read);
                TryAddTable(read);   // query_where / list_* → render as a table
            }

            if (!reprompt && calls.Count == 0)
                break;
        }

        replies.Add(new ChatReply(
            "Đã đạt giới hạn số bước xử lý. Hãy thử diễn đạt yêu cầu cụ thể hơn."));
        return new ChatTurn(replies);
    }

    /// <summary>Answer any not-yet-handled tool_calls so the protocol stays valid.</summary>
    private void DeferRemaining(IReadOnlyList<ToolCall> calls, int from)
    {
        for (var j = from; j < calls.Count; j++)
            AppendToolResult(calls[j],
                JsonResultError("deferred", "Sẽ xử lý sau khi thao tác trước được giải quyết."));
    }

    private const int FetchLimit = 5000;   // Core's hard cap
    private const int ListLimit = 40;      // rows returned when listing (== ResultTrimmer cap)
    private const string FetchTruncatedNote =
        "⚠️ Danh mục có hơn 5000 phần tử; chỉ lọc trên 5000 phần tử đầu — " +
        "số đếm là CHƯA đầy đủ. Hãy nói rõ với người dùng.";

    /// <summary>
    /// Fetch a category from Revit (projecting the fields the filters/output need),
    /// then filter CLIENT-SIDE with the rich operator set (ends_with, regex,
    /// is_empty, correct numeric compares). Returns a find_elements-shaped envelope
    /// whose data.elements/count are already filtered; errors pass through.
    /// Optional <paramref name="viewId"/>: pass to Core so only elements visible
    /// in that view are collected (uses FilteredElementCollector(doc, viewId)).
    /// </summary>
    private async Task<JsonObject> FetchFilteredAsync(
        string category, JsonArray? filtersJson, IEnumerable<string> extraFields,
        CancellationToken ct, long? viewId = null)
    {
        var conds = ElementFilter.Parse(filtersJson);

        var fields = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ElementFilter.Params(conds).Concat(extraFields))
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) fields.Add(p);

        var findParams = new JsonObject { ["category"] = category, ["limit"] = FetchLimit };
        if (fields.Count > 0) findParams["fields"] = fields;
        if (viewId.HasValue) findParams["view_id"] = viewId.Value;

        var env = await _revit.CallAsync("find_elements", findParams, false, ct).ConfigureAwait(false);
        if (!IsOk(env)) return env;

        var data = env["data"] as JsonObject ?? new JsonObject();
        var elements = data["elements"] as JsonArray ?? new JsonArray();
        var truncated = data["truncated"] is JsonValue tv && tv.TryGetValue<bool>(out var t) && t;

        // Move matched nodes out of the fetched array (we own and discard it) instead
        // of cloning — avoids copying up to 5000 nodes for count/aggregate callers.
        HashSet<JsonNode>? matchedSet = conds.Count == 0
            ? null
            : new HashSet<JsonNode>(ElementFilter.Apply(elements, conds), ReferenceEqualityComparer.Instance);

        var outArr = new JsonArray();
        for (var i = 0; i < elements.Count; i++)
        {
            var node = elements[i];
            if (node is null) continue;
            if (matchedSet is null || matchedSet.Contains(node))
            {
                elements[i] = null;         // detach → re-parent without cloning
                outArr.Add(node);
            }
        }

        var outData = new JsonObject
        {
            ["count"] = outArr.Count,
            ["elements"] = outArr,
            ["truncated"] = truncated,
        };
        if (truncated) outData["note"] = FetchTruncatedNote;
        return new JsonObject { ["ok"] = true, ["data"] = outData };
    }

    /// <summary>find_elements: list elements matching rich client-side filters.</summary>
    private async Task<JsonObject> FindElementsAsync(ToolCall tc, CancellationToken ct)
    {
        JsonObject args;
        try { args = tc.ParseArguments(); }
        catch (Exception ex) { return JsonResultError("bad_arguments", ex.Message); }

        var category = args["category"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(category))
            return JsonResultError("bad_request", "find_elements cần 'category'.");

        var viewId = TryGetLong(args["view_id"]);

        var env = await FetchFilteredAsync(
            category!, args["filters"] as JsonArray, StringList(args["fields"]), ct, viewId)
            .ConfigureAwait(false);
        if (!IsOk(env)) return env;

        // Cap the returned rows to the model's limit (≤ ListLimit so ResultTrimmer
        // never re-trims); keep data.count = the true match total and say so.
        var displayLimit = Math.Clamp(TryGetInt(args["limit"]) ?? ListLimit, 1, ListLimit);
        var data = (JsonObject)env["data"]!;
        var total = TryGetInt(data["count"]) ?? 0;
        if (data["elements"] is JsonArray arr && arr.Count > displayLimit)
        {
            var capped = new JsonArray();
            for (var i = 0; i < displayLimit; i++) { var n = arr[i]; arr[i] = null; capped.Add(n); }
            data["elements"] = capped;
            data["listNote"] = $"Hiển thị {displayLimit}/{total} kết quả khớp.";
        }
        return env;
    }

    /// <summary>count_elements: count (+ groupBy) over rich-filtered elements.</summary>
    private async Task<JsonObject> CountElementsAsync(ToolCall tc, CancellationToken ct)
    {
        JsonObject args;
        try { args = tc.ParseArguments(); }
        catch (Exception ex) { return JsonResultError("bad_arguments", ex.Message); }

        var category = args["category"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(category))
            return JsonResultError("bad_request", "count_elements cần 'category'.");

        var groupBy = args["groupBy"]?.GetValue<string>();
        var extra = string.IsNullOrWhiteSpace(groupBy) ? Array.Empty<string>() : new[] { groupBy! };
        var viewId = TryGetLong(args["view_id"]);
        var sortByValue = IsSortByValue(args);

        var env = await FetchFilteredAsync(
            category!, args["filters"] as JsonArray, extra, ct, viewId)
            .ConfigureAwait(false);
        return Aggregator.Summarize(env, groupBy, LevelOrderFor(groupBy), IsDescending(args), sortByValue);
    }

    private static bool IsDescending(JsonObject args) =>
        string.Equals(args["order"]?.GetValue<string>(), "desc", StringComparison.OrdinalIgnoreCase);

    private static bool IsSortByValue(JsonObject args) =>
        string.Equals(args["sortBy"]?.GetValue<string>(), "value", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// aggregate_elements: sum/min/max/avg of a numeric parameter over rich-filtered
    /// elements, with unit conversion (ft²→m², ft³→m³) + optional top-N / group-by.
    /// Floors &amp; walls expose computed Area/Volume, so totals need no thickness.
    /// </summary>
    private async Task<JsonObject> AggregateElementsAsync(ToolCall tc, CancellationToken ct)
    {
        JsonObject args;
        try { args = tc.ParseArguments(); }
        catch (Exception ex) { return JsonResultError("bad_arguments", ex.Message); }

        var category = args["category"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(category))
            return JsonResultError("bad_request", "aggregate_elements cần 'category'.");

        var parameter = args["parameter"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(parameter))
            return JsonResultError("bad_request", "aggregate_elements cần 'parameter' (vd 'Area', 'Volume').");

        var groupBy = args["groupBy"]?.GetValue<string>();
        var unit = args["unit"]?.GetValue<string>();
        var top = Math.Clamp(TryGetInt(args["top"]) ?? 0, 0, 40);
        var (factor, label) = Aggregator.ResolveUnit(unit, parameter!);
        var viewId = TryGetLong(args["view_id"]);
        var sortByValue = IsSortByValue(args);

        var extra = new List<string> { parameter! };
        if (!string.IsNullOrWhiteSpace(groupBy)) extra.Add(groupBy!);

        var env = await FetchFilteredAsync(
            category!, args["filters"] as JsonArray, extra, ct, viewId)
            .ConfigureAwait(false);
        return Aggregator.SummarizeNumeric(
            env, parameter!, factor, label, top, groupBy, LevelOrderFor(groupBy), IsDescending(args), sortByValue);
    }

    private static IEnumerable<string> StringList(JsonNode? node)
    {
        if (node is not JsonArray arr) yield break;
        foreach (var n in arr)
        {
            var s = (n as JsonValue)?.TryGetValue<string>(out var v) == true ? v : n?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s!;
        }
    }

    private static int? TryGetInt(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<int>(); } catch { }
        try { return (int)n.GetValue<long>(); } catch { }
        return null;
    }

    private static int IndexOfCol(IReadOnlyList<string> cols, string name)
    {
        for (var i = 0; i < cols.Count; i++)
            if (string.Equals(cols[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static long? TryGetLong(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<long>(); } catch { }
        try { return (long)n.GetValue<int>(); } catch { }
        return null;
    }

    private Task<JsonObject> ExecuteAsync(ToolCall tc, bool dryRun, CancellationToken ct)
    {
        JsonObject args;
        try { args = tc.ParseArguments(); }
        catch (Exception ex)
        {
            return Task.FromResult(JsonResultError("bad_arguments", ex.Message));
        }
        return _revit.CallAsync(tc.FunctionName, args, dryRun, ct);
    }

    private void AppendToolResult(ToolCall tc, JsonObject result) =>
        // Trim large arrays so a big query result can't truncate the system prompt.
        _conversation.Add(ChatMessage.ToolResult(tc.Id, ResultTrimmer.Trim(result).ToJsonString()));

    /// <summary>If a tool result is tabular, capture it as a table to render in the UI.</summary>
    private void TryAddTable(JsonObject result)
    {
        var table = TableExtractor.TryExtract(result);
        if (table is { Rows.Count: > 0 }) _turnTables.Add(table);
    }

    // ── Parsing helpers ──────────────────────────────────────────────────────

    private static string? ParseEcho(ToolCall tc)
    {
        try
        {
            var a = tc.ParseArguments();
            var vi = a["vi"]?.GetValue<string>();
            var en = a["en"]?.GetValue<string>();
            if (vi is null && en is null) return null;
            return en is null ? vi : $"{vi}\n_({en})_";
        }
        catch { return null; }
    }

    private static string ParseClarify(ToolCall tc)
    {
        try { return tc.ParseArguments()["question"]?.GetValue<string>() ?? "Bạn muốn làm gì?"; }
        catch { return "Bạn muốn làm gì?"; }
    }

    // ── Envelope helpers ─────────────────────────────────────────────────────

    private static bool IsOk(JsonObject env) =>
        env["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string SummaryOf(JsonObject env, string fallback)
    {
        var data = env["data"] as JsonObject;
        return data?["changeSummary"]?.GetValue<string>() ?? fallback;
    }

    private static string ErrorOf(JsonObject env)
    {
        var err = env["error"] as JsonObject;
        return err?["message"]?.GetValue<string>() ?? "lỗi không xác định";
    }

    private static JsonObject JsonResultOk(JsonNode? data) =>
        new() { ["ok"] = true, ["data"] = data };

    private static JsonObject JsonResultError(string code, string message) =>
        new()
        {
            ["ok"] = false,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
}
