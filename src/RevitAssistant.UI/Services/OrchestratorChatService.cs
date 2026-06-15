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
            "set_parameter", "set_parameter_batch", "rename_element",
        };

    private const int MaxIterations = 6;

    private readonly ILlmClient _llm;
    private readonly IRevitBridge _revit;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly string? _modelSchemaJson;

    private readonly List<ChatMessage> _conversation = new();
    private ToolCall? _pendingWrite;

    public OrchestratorChatService(ILlmClient llm, IRevitBridge revit, string? modelSchemaJson = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _modelSchemaJson = modelSchemaJson;
        _tools = ToolSpecAdapter.BuildToolSurface();
    }

    public async Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        _conversation.Add(ChatMessage.User(userInput));
        _pendingWrite = null;
        return await RunLoopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Clear the conversation (and any pending write) — the "new chat" action.</summary>
    public void Reset()
    {
        _conversation.Clear();
        _pendingWrite = null;
    }

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
            return ModelSchema.Build(levels, cats);
        }
        catch
        {
            return null;   // no document / dispatcher not ready → fall back to generic prompt
        }
    }

    public async Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
    {
        if (_pendingWrite is null)
            return new ChatTurn(new[] { new ChatReply("Không có thay đổi nào đang chờ xác nhận.") });

        var write = _pendingWrite;
        _pendingWrite = null;

        var replies = new List<ChatReply>();
        var result = await ExecuteAsync(write, dryRun: false, ct).ConfigureAwait(false);
        AppendToolResult(write, result);

        replies.Add(IsOk(result)
            ? new ChatReply("✅ " + SummaryOf(result, fallback: "Đã ghi thay đổi vào model."))
            : new ChatReply("❌ " + ErrorOf(result), IsError: true));

        var tail = await RunLoopAsync(ct).ConfigureAwait(false);
        replies.AddRange(tail.Replies);
        return new ChatTurn(replies, tail.Pending);
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

    /// <summary>
    /// count_elements: run find_elements (high limit, projecting the groupBy field)
    /// then aggregate the rows in C#. Returns an exact { total, groups } summary.
    /// </summary>
    private async Task<JsonObject> CountElementsAsync(ToolCall tc, CancellationToken ct)
    {
        JsonObject args;
        try { args = tc.ParseArguments(); }
        catch (Exception ex) { return JsonResultError("bad_arguments", ex.Message); }

        var category = args["category"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(category))
            return JsonResultError("bad_request", "count_elements cần 'category'.");

        var groupBy = args["groupBy"]?.GetValue<string>();

        var findParams = new JsonObject
        {
            ["category"] = category,
            ["limit"] = 5000,
        };
        if ((args["filters"] as JsonArray)?.DeepClone() is JsonArray filters)
            findParams["filters"] = filters;
        if (!string.IsNullOrWhiteSpace(groupBy))
            findParams["fields"] = new JsonArray { groupBy };

        var env = await _revit.CallAsync("find_elements", findParams, false, ct).ConfigureAwait(false);
        return Aggregator.Summarize(env, groupBy);
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
