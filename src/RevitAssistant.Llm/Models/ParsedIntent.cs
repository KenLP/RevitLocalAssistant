namespace RevitAssistant.Llm;

/// <summary>
/// Result of one IntentParser.ParseAsync round.
///
/// The IntentParser does ONE LLM turn and returns here.
/// Phase 4 Orchestrator uses ConversationHistory to continue the multi-turn
/// loop (execute Revit tools → feed results back → next assistant message).
/// </summary>
public sealed record ParsedIntent
{
    /// <summary>"Hiểu là: ..." — Vietnamese echo-back for the user to verify.</summary>
    public string? EchoVi { get; init; }

    /// <summary>"Understood: ..." — Professional English for debug/log.</summary>
    public string? EchoEn { get; init; }

    /// <summary>
    /// Revit tool calls the LLM wants to execute (find_elements, set_parameter_batch, etc.).
    /// Empty when the LLM needs clarification first.
    /// </summary>
    public IReadOnlyList<ToolCall> Actions { get; init; } = [];

    /// <summary>True when the LLM called the 'clarify' tool instead of acting.</summary>
    public bool NeedsClarification { get; init; }

    /// <summary>The question to ask the user, in Vietnamese.</summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>
    /// Full conversation so far — used by Phase 4 Orchestrator to continue
    /// after executing the tool calls and feeding results back.
    /// </summary>
    public IReadOnlyList<ChatMessage> ConversationHistory { get; init; } = [];
}
