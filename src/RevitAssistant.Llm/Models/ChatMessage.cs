using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>One turn in the conversation with Ollama.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string? Content { get; init; }

    /// <summary>Set by the assistant when it wants to call tools.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Set on a Tool-role message that carries a tool result back to the model.
    /// Must match the ToolCall.Id from the preceding Assistant message.
    /// </summary>
    public string? ToolCallId { get; init; }

    public static ChatMessage System(string content) =>
        new() { Role = ChatRole.System, Content = content };

    public static ChatMessage User(string content) =>
        new() { Role = ChatRole.User, Content = content };

    public static ChatMessage ToolResult(string toolCallId, string resultJson) =>
        new() { Role = ChatRole.Tool, Content = resultJson, ToolCallId = toolCallId };

    /// <summary>Serialize to the OpenAI-compatible wire format Ollama expects.</summary>
    public JsonObject ToJsonNode()
    {
        var obj = new JsonObject
        {
            ["role"] = Role switch
            {
                ChatRole.System    => "system",
                ChatRole.User      => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool      => "tool",
                _ => "user",
            },
        };

        if (Content is not null)
            obj["content"] = Content;

        if (ToolCalls is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var tc in ToolCalls) arr.Add(tc.ToJsonNode());
            obj["tool_calls"] = arr;
        }

        if (ToolCallId is not null)
            obj["tool_call_id"] = ToolCallId;

        return obj;
    }
}

/// <summary>A single tool (function) call requested by the assistant.</summary>
public sealed record ToolCall(string Id, string FunctionName, string ArgumentsJson)
{
    public JsonNode ToJsonNode() => new JsonObject
    {
        ["id"] = Id,
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = FunctionName,
            ["arguments"] = ArgumentsJson,
        },
    };

    /// <summary>Parse arguments as a JsonObject (throws if malformed).</summary>
    public JsonObject ParseArguments() =>
        JsonNode.Parse(ArgumentsJson)?.AsObject()
        ?? throw new InvalidOperationException(
            $"Tool '{FunctionName}' returned non-object arguments: {ArgumentsJson}");
}
