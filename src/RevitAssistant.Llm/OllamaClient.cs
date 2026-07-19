using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

/// <summary>
/// HTTP client for Ollama's NATIVE chat endpoint (POST /api/chat).
///
/// We use the native endpoint rather than the OpenAI-compatible one specifically
/// to control <c>options.num_ctx</c>: the OpenAI shim loads the model at Ollama's
/// small default context (~4k), which silently truncates the system prompt once a
/// large tool result is fed back — the model then "forgets" its instructions
/// (e.g. answers in English, mis-calls tools). A larger window keeps the prompt
/// alive across the agent loop.
///
/// Notes on the native wire format vs OpenAI:
///   - tool_call arguments arrive as a JSON OBJECT (not a stringified blob)
///   - tool_calls carry no id → we synthesise call_0, call_1, …
///   - tool result messages use { role:"tool", content } (no tool_call_id)
/// </summary>
public sealed class OllamaClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _numCtx;
    private const int MaxRetries = 3;
    private const int DefaultTimeoutSeconds = 180;

    /// <summary>
    /// Context window requested from Ollama.
    ///
    /// This has to hold the tool definitions AND the system prompt AND the conversation.
    /// The first two alone are ~8.8k tokens; at the previous 8192 they overflowed the
    /// window, Ollama truncated from the front, and the system prompt — the part carrying
    /// the "answer in Vietnamese" and workflow rules — was what got cut. The model then
    /// replied in the wrong language and invented tool names. ToolSurfaceBudgetTests
    /// guards the headroom; raise this (or shrink the surface) before adding tools.
    /// </summary>
    public const int DefaultNumCtx = 16384;

    public OllamaClient(
        string baseUrl = "http://localhost:11434",
        string model = "qwen2.5:7b-instruct",
        int numCtx = DefaultNumCtx,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _model = model;
        _numCtx = numCtx;
    }

    public async Task<ChatResponse> ChatAsync(
        IList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var currentMessages = new List<ChatMessage>(messages);

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var body = BuildRequest(currentMessages, tools);
            var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage httpResp;
            try
            {
                httpResp = await _http.PostAsync("/api/chat", content, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaException(
                    $"Cannot reach Ollama at {_http.BaseAddress}. Make sure 'ollama serve' is running.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new OllamaException(
                    $"Ollama timed out after {_http.Timeout.TotalSeconds:0}s. The model may still be loading.", ex);
            }

            if (!httpResp.IsSuccessStatusCode)
            {
                var err = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new OllamaException($"Ollama returned HTTP {(int)httpResp.StatusCode}: {err}");
            }

            var responseText = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (parsed, malformedReason) = TryParseResponse(responseText);
            if (parsed is not null) return parsed;

            if (attempt < MaxRetries - 1)
            {
                currentMessages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = $"[malformed tool call: {malformedReason}]",
                });
                currentMessages.Add(ChatMessage.User(
                    "Your previous tool call had invalid JSON arguments. Please retry with valid JSON."));
            }
        }

        throw new OllamaException(
            $"Ollama returned malformed tool-call arguments after {MaxRetries} attempts.");
    }

    // ── Request ──────────────────────────────────────────────────────────────

    private JsonObject BuildRequest(IList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(SerializeMessage(m));

        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = msgArray,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.1,
                ["num_ctx"] = _numCtx,
            },
        };

        if (tools is { Count: > 0 })
        {
            var toolsArray = new JsonArray();
            foreach (var t in tools) toolsArray.Add(t.ToJsonNode());
            body["tools"] = toolsArray;
        }

        return body;
    }

    /// <summary>Serialize one message to Ollama's native /api/chat shape.</summary>
    private static JsonObject SerializeMessage(ChatMessage m)
    {
        var role = m.Role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => "user",
        };

        var obj = new JsonObject { ["role"] = role, ["content"] = m.Content ?? string.Empty };

        if (m.ToolCalls is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var tc in m.ToolCalls)
            {
                JsonNode argsNode;
                try { argsNode = JsonNode.Parse(tc.ArgumentsJson) ?? new JsonObject(); }
                catch { argsNode = new JsonObject(); }

                arr.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.FunctionName,
                        ["arguments"] = argsNode,
                    },
                });
            }
            obj["tool_calls"] = arr;
        }

        return obj;
    }

    // ── Response ─────────────────────────────────────────────────────────────

    private static (ChatResponse? response, string? error) TryParseResponse(string json)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc is null) return (null, "null response body");

            var message = doc["message"];
            if (message is null) return (null, "missing message");

            var finishReason = doc["done_reason"]?.GetValue<string>() ?? "stop";

            if (message["tool_calls"] is JsonArray toolCallsArr && toolCallsArr.Count > 0)
            {
                var toolCalls = new List<ToolCall>();
                for (var i = 0; i < toolCallsArr.Count; i++)
                {
                    var tcNode = toolCallsArr[i];
                    var fnName = tcNode?["function"]?["name"]?.GetValue<string>();
                    if (fnName is null) continue;

                    var argsRaw = tcNode?["function"]?["arguments"];
                    string argsJson;
                    if (argsRaw is JsonValue strVal && strVal.TryGetValue<string>(out var argsStr))
                        argsJson = argsStr;                    // some builds stringify
                    else
                        argsJson = argsRaw?.ToJsonString() ?? "{}";

                    try { JsonNode.Parse(argsJson); }
                    catch (JsonException ex)
                    {
                        return (null, $"tool '{fnName}' arguments not valid JSON: {ex.Message}");
                    }

                    toolCalls.Add(new ToolCall($"call_{i}", fnName, argsJson));
                }

                var textContent = message["content"]?.GetValue<string>();
                return (new ChatResponse(textContent, toolCalls, "tool_calls"), null);
            }

            var content = message["content"]?.GetValue<string>() ?? "";
            return (new ChatResponse(content, [], finishReason), null);
        }
        catch (JsonException ex)
        {
            return (null, $"JSON parse error: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Thrown when Ollama is unreachable or returns an unexpected error.</summary>
public sealed class OllamaException(string message, Exception? inner = null)
    : Exception(message, inner);
