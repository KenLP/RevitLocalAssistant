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
    private readonly OllamaClient _ollama;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public IntentParser(OllamaClient ollama)
    {
        _ollama = ollama;
        _tools = ToolSpecAdapter.BuildToolSurface();
    }

    public async Task<ParsedIntent> ParseAsync(
        string userInput,
        string? modelSchemaJson = null,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(modelSchemaJson);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userInput),
        };

        var response = await _ollama.ChatAsync(messages, _tools, ct).ConfigureAwait(false);

        return BuildIntent(response, messages);
    }

    // ── System prompt ────────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string? modelSchemaJson)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("""
            You are an AI assistant embedded inside Autodesk Revit.
            You understand commands in Vietnamese (VI) AND English (EN).
            Always respond to Vietnamese users in Vietnamese when asking clarification.

            ## YOUR WORKFLOW — follow this ORDER strictly:
            1. Call `echo_interpretation` FIRST with your understanding in both VI and EN.
            2. If ambiguous, call `clarify` instead and STOP (do not call Revit tools).
            3. Otherwise, call the Revit tool(s) needed to fulfil the request.
               - For queries: call find_elements or list_* tools.
               - For bulk edits: call find_elements FIRST to discover element IDs,
                 then call set_parameter_batch with those IDs.
               - NEVER invent element IDs — always discover them via find_elements.

            ## RULES
            - Use EXACT BuiltInCategory names and EXACT parameter names from the glossary below.
            - Numbers for length/area are in Revit internal units (feet) unless you pass units="meters".
            - Prefer set_parameter_batch over multiple set_parameter calls.
            - If the user says "những cái đang chọn" / "selected elements", call get_selected_elements.
            - If the user says "kiểm tra" / "compliance check", call find_elements with the relevant filters.

            """);

        sb.AppendLine(BimGlossary.BuildPromptSnippet());

        if (modelSchemaJson is not null)
        {
            sb.AppendLine("## Live model schema (categories + parameters in this project)");
            sb.AppendLine(modelSchemaJson);
        }
        else
        {
            sb.AppendLine("""
                ## Common categories in a typical project
                OST_Walls, OST_Floors, OST_Ceilings, OST_Roofs, OST_Doors, OST_Windows,
                OST_Columns, OST_StructuralColumns, OST_StructuralFraming, OST_Stairs,
                OST_Rooms, OST_Levels, OST_Grids, OST_Sheets, OST_PipeCurves, OST_DuctCurves.

                ## Common parameters
                Mark, Name, Comments, "Fire Rating", Width, Height, Length, Area, Volume,
                Department, Number, Level, Material, Description, Manufacturer.
                """);
        }

        return sb.ToString();
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
