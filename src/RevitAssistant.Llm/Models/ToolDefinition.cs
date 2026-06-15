using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

/// <summary>
/// OpenAI / Ollama function-calling tool definition.
/// Passed in the "tools" array of /v1/chat/completions.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject Parameters)
{
    public JsonNode ToJsonNode() => new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = JsonNode.Parse(Parameters.ToJsonString()),
        },
    };
}
