namespace RevitAssistant.Llm;

/// <summary>
/// Parses a user's VI/EN command into a structured ParsedIntent.
///
/// One turn: User message → LLM response (tool calls) → ParsedIntent.
/// The LLM is expected to:
///   1. Call echo_interpretation first (confirms VI/EN interpretation)
///   2. Call one or more Revit tools (find_elements, set_parameter_batch, …)
///      OR call clarify if the request is ambiguous
///
/// Phase 4 Orchestrator uses ParsedIntent.ConversationHistory to continue
/// the multi-turn loop after Revit tool calls are executed.
///
/// Phase 5: inject live model schema via modelSchemaJson.
/// </summary>
public sealed class IntentParser
{
    private readonly ILlmClient _ollama;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public IntentParser(ILlmClient ollama)
    {
        _ollama = ollama;
        _tools = ToolSpecAdapter.BuildToolSurface();
    }

    public async Task<ParsedIntent> ParseAsync(
        string userInput,
        string? modelSchemaJson = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(AssistantPrompt.Build(modelSchemaJson)),
            ChatMessage.User(userInput),
        };

        var response = await _ollama.ChatAsync(messages, _tools, ct).ConfigureAwait(false);

        return BuildIntent(response, messages);
    }

    // ── Response → ParsedIntent ──────────────────────────────────────────────

    private static ParsedIntent BuildIntent(ChatResponse response, List<ChatMessage> history)
    {
        // Record the assistant's message in history for Phase 4 continuation.
        var assistantMsg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = response.TextContent,
            ToolCalls = response.HasToolCalls ? response.ToolCalls : null,
        };
        var fullHistory = new List<ChatMessage>(history) { assistantMsg };

        string? echoVi = null;
        string? echoEn = null;
        var actions = new List<ToolCall>();
        string? clarificationQuestion = null;

        foreach (var tc in response.ToolCalls)
        {
            switch (tc.FunctionName)
            {
                case "echo_interpretation":
                    try
                    {
                        var args = tc.ParseArguments();
                        echoVi = args["vi"]?.GetValue<string>();
                        echoEn = args["en"]?.GetValue<string>();
                    }
                    catch { /* malformed echo — ignore, still proceed */ }
                    break;

                case "clarify":
                    try
                    {
                        var args = tc.ParseArguments();
                        clarificationQuestion = args["question"]?.GetValue<string>();
                    }
                    catch { clarificationQuestion = "Bạn muốn làm gì?"; }
                    break;

                default:
                    // Revit tool call (find_elements, set_parameter_batch, etc.)
                    actions.Add(tc);
                    break;
            }
        }

        return new ParsedIntent
        {
            EchoVi = echoVi,
            EchoEn = echoEn,
            Actions = actions,
            NeedsClarification = clarificationQuestion is not null,
            ClarificationQuestion = clarificationQuestion,
            ConversationHistory = fullHistory,
        };
    }
}
