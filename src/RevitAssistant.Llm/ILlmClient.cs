namespace RevitAssistant.Llm;

/// <summary>
/// Abstraction over the chat-completions backend so the orchestrator can be
/// unit-tested without a running Ollama instance.
/// <see cref="OllamaClient"/> is the production implementation.
/// </summary>
public interface ILlmClient
{
    Task<ChatResponse> ChatAsync(
        IList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default);
}
