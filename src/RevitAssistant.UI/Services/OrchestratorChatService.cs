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
/// summarise the result.
///
/// Protocol invariant: every assistant tool_call gets exactly one tool response
/// before the next LLM call. The single pending write is the only call left
/// unanswered while we wait for the user; Confirm/Cancel always answers it.
/// </summary>
public sealed class OrchestratorChatService : IChatService
{
    private static readonly HashSet<string> WriteTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "update_where", "set_parameter", "set_parameter_batch", "rename_element",
        };

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
    private ToolCall? _pendingWrite;

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
        _conversation.Add(ChatMessage.User(userInput));
        _pendingWrite = null;
        _turnTables.Clear();
        var turn = await RunLoopAsync(ct).ConfigureAwait(false);
        return turn with { ContextUsage = CurrentUsage(), Tables = _turnTables.ToList() };
    }

    /// <summary>Clear the conversation (and any pending write) — the "new chat" action.</summary>
    public void Reset()
    {
        _conversation.Clear();
        _levelElev.Clear();
        _pendingWrite = null;
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

    /// <summary>
    /// On the first message, seed the system prompt. If no static schema was
    /// supplied, fetch the real levels + categories from the active document so
    /// the model is grounded in names that actually exist.
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
            var cats = await _revit.CallAsync("list_categories", new JsonObject(), false, ct).ConfigureAwait(false);
            PopulateLevelOrder(levels);
            return ModelSchema.Build(levels, cats);
        }
        catch
        {
            return null;   // no document / dispatcher not ready → fall back to generic prompt
        }
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

    public async Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
    {
        if (_pendingWrite is null)
            return new ChatTurn(new[] { new ChatReply("Không có thay đổi nào đang chờ xác nhận.") });

        var write = _pendingWrite;
        _pendingWrite = null;
        _turnTables.Clear();

        var replies = new List<ChatReply>();
        var result = await ExecuteAsync(write, dryRun: false, ct).ConfigureAwait(false);
        AppendToolResult(write, result);

        replies.Add(IsOk(result)
            ? new ChatReply("✅ " + SummaryOf(result, fallback: "Đã ghi thay đổi vào model."))
            : new ChatReply("❌ " + ErrorOf(result), IsError: true));

        var tail = await RunLoopAsync(ct).ConfigureAwait(false);
        replies.AddRange(tail.Replies);
        return new ChatTurn(replies, tail.Pending, CurrentUsage(), _turnTables.ToList());
    }

    public void CancelPending()
    {
        if (_pendingWrite is null) return;
        // Answer the dangling tool_call so the conversation stays well-formed.
        AppendToolResult(_pendingWrite,
            JsonResultError("cancelled", "Người dùng đã hủy thao tác này."));
        _pendingWrite = null;
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

                if (WriteTools.Contains(tc.FunctionName))
                {
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

                    _pendingWrite = tc;
                    DeferRemaining(calls, k + 1);
                    return new ChatTurn(replies, PreviewBuilder.Build(tc, dry));
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
    /// </summary>
    private async Task<JsonObject> FetchFilteredAsync(
        string category, JsonArray? filtersJson, IEnumerable<string> extraFields, CancellationToken ct)
    {
        var conds = ElementFilter.Parse(filtersJson);

        var fields = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ElementFilter.Params(conds).Concat(extraFields))
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) fields.Add(p);

        var findParams = new JsonObject { ["category"] = category, ["limit"] = FetchLimit };
        if (fields.Count > 0) findParams["fields"] = fields;

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

        var env = await FetchFilteredAsync(
            category!, args["filters"] as JsonArray, StringList(args["fields"]), ct).ConfigureAwait(false);
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

        var env = await FetchFilteredAsync(category!, args["filters"] as JsonArray, extra, ct)
            .ConfigureAwait(false);
        return Aggregator.Summarize(env, groupBy, LevelOrderFor(groupBy), IsDescending(args));
    }

    private static bool IsDescending(JsonObject args) =>
        string.Equals(args["order"]?.GetValue<string>(), "desc", StringComparison.OrdinalIgnoreCase);

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

        var extra = new List<string> { parameter! };
        if (!string.IsNullOrWhiteSpace(groupBy)) extra.Add(groupBy!);

        var env = await FetchFilteredAsync(category!, args["filters"] as JsonArray, extra, ct)
            .ConfigureAwait(false);
        return Aggregator.SummarizeNumeric(
            env, parameter!, factor, label, top, groupBy, LevelOrderFor(groupBy), IsDescending(args));
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
