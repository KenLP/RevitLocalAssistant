namespace RevitAssistant.Llm;

/// <summary>Parsed response from one Ollama /v1/chat/completions call.</summary>
public sealed record ChatResponse(
    string? TextContent,
    IReadOnlyList<ToolCall> ToolCalls,
    string FinishReason)
{
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>True when the model wants to call tools AND has no plain text.</summary>
    public bool IsToolUse => HasToolCalls && TextContent is null or "";
}
