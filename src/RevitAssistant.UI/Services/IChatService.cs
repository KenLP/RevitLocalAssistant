namespace RevitAssistant.UI;

/// <summary>
/// Abstraction the chat panel talks to. Keeps <see cref="ChatViewModel"/> free of
/// Revit API and Ollama concrete types, so the panel is fully unit-testable.
///
/// Phase 3: <see cref="PlaceholderChatService"/> (offline, no model call).
/// Phase 4: the real orchestrator (Ollama → dry-run → preview → confirm → commit)
///          implements this same interface and is swapped in via the Addin.
/// </summary>
public interface IChatService
{
    /// <summary>Send the user's message and get back the assistant's reply.</summary>
    Task<ChatReply> SendAsync(string userInput, CancellationToken ct = default);
}

/// <summary>One assistant reply. <see cref="IsError"/> renders as an error bubble.</summary>
public readonly record struct ChatReply(string Text, bool IsError = false);
