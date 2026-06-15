using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

/// <summary>
/// HTTP client for Ollama's OpenAI-compatible endpoint.
/// POST http://localhost:11434/v1/chat/completions
///
/// Features:
///   - Supports "tools" array (function-calling) — Qwen2.5-Instruct handles this natively.
///   - Retries up to MaxRetries when tool-call arguments are malformed JSON.
///   - Non-streaming only (stream: false); streaming deferred to Phase 3 UX.
///   - Temperature 0.1 for deterministic structured output.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const int MaxRetries = 3;
    private const int DefaultTimeoutSeconds = 120;

    public OllamaClient(
        string baseUrl = "http://localhost:11434",
        string model = "qwen2.5:7b-instruct",
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _model = model;
    }

    /// <summary>
    /// Send a chat turn to Ollama and return the parsed response.
    /// Retries automatically when tool-call arguments are not valid JSON.
    /// </summary>
    public async Task<ChatResponse> ChatAsync(
        IList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var currentMessages = new List<ChatMessage>(messages);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var body = BuildRequest(currentMessages, tools);
            var content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            HttpResponseMessage httpResp;
            try
            {
                httpResp = await _http.PostAsync("/v1/chat/completions", content, ct)
                                      .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaException(
                    $"Cannot reach Ollama at {_http.BaseAddress}. " +
                    "Make sure 'ollama serve' is running.", ex);
            }

            if (!httpResp.IsSuccessStatusCode)
            {
                var err = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new OllamaException(
                    $"Ollama returned HTTP {(int)httpResp.StatusCode}: {err}");
            }

            var responseText = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            ChatResponse? parsed;
            string? malformedReason;
            (parsed, malformedReason) = TryParseResponse(responseText);

            if (parsed is not null)
                return parsed;

            // Malformed tool-call JSON — tell the model to retry.
            if (attempt < MaxRetries - 1)
            {
                currentMessages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = $"[malformed tool call: {malformedReason}]",
                });
                currentMessages.Add(ChatMessage.User(
                    "Your previous tool call had invalid JSON arguments. " +
                    "Please retry with valid JSON."));
            }
        }

        throw new OllamaException(
            $"Ollama returned malformed tool-call arguments after {MaxRetries} attempts.");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private JsonObject BuildRequest(IList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(m.ToJsonNode());

        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["temperature"] = 0.1,
            ["messages"] = msgArray,
        };

        if (tools is { Count: > 0 })
        {
            var toolsArray = new JsonArray();
            foreach (var t in tools)
                toolsArray.Add(t.ToJsonNode());
            body["tools"] = toolsArray;
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static (ChatResponse? response, string? error) TryParseResponse(string json)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc is null) return (null, "null response body");

            var message = doc["choices"]?[0]?["message"];
            if (message is null) return (null, "missing choices[0].message");

            var finishReason = doc["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "stop";

            // Check for tool_calls
            var toolCallsNode = message["tool_calls"];
            if (toolCallsNode is JsonArray toolCallsArr && toolCallsArr.Count > 0)
            {
                var toolCalls = new List<ToolCall>();
                foreach (var tcNode in toolCallsArr)
                {
                    var id = tcNode?["id"]?.GetValue<string>() ?? $"call_{toolCalls.Count}";
                    var fnName = tcNode?["function"]?["name"]?.GetValue<string>();
                    if (fnName is null) continue;

                    var argsRaw = tcNode?["function"]?["arguments"];
                    string argsJson;

                    if (argsRaw is JsonValue strVal &&
                        strVal.TryGetValue<string>(out var argsStr))
                    {
                        // Some models double-encode arguments as a string — unwrap.
                        argsJson = argsStr;
                    }
                    else
                    {
                        argsJson = argsRaw?.ToJsonString() ?? "{}";
                    }

                    // Validate the arguments are parseable JSON.
                    try { JsonNode.Parse(argsJson); }
                    catch (JsonException ex)
                    {
                        return (null, $"tool '{fnName}' arguments not valid JSON: {ex.Message}");
                    }

                    toolCalls.Add(new ToolCall(id, fnName, argsJson));
                }

                var textContent = message["content"]?.GetValue<string>();
                return (new ChatResponse(textContent, toolCalls, finishReason), null);
            }

            // Plain text response
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
