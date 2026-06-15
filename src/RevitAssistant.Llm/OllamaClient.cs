using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

// ─── Phase 2 stub ────────────────────────────────────────────────────────────
// Full implementation in Phase 2.
// Calls Ollama's OpenAI-compatible endpoint:
//   POST http://localhost:11434/v1/chat/completions
//   with "tools" array (function-calling) and model = qwen2.5:7b-instruct
// Returns parsed tool_calls or text content.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaClient(string baseUrl = "http://localhost:11434",
                        string model = "qwen2.5:7b-instruct")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _model = model;
    }

    // Phase 2: implement full chat-with-tools loop
    public Task<JsonObject> ChatAsync(
        IList<JsonObject> messages,
        IList<JsonObject>? tools = null,
        CancellationToken ct = default)
        => Task.FromResult(JsonResult.Stub());

    public void Dispose() => _http.Dispose();
}

// Temp helper until Core placeholder is referenced here
file static class JsonResult
{
    public static JsonObject Stub() => new() { ["stub"] = true };
}
