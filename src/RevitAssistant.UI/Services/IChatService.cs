namespace RevitAssistant.UI;

/// <summary>
/// Abstraction the chat panel talks to. Keeps <see cref="ChatViewModel"/> free of
/// Revit API and Ollama concrete types, so the panel is fully unit-testable.
///
/// A turn may end with a <see cref="ChangePreview"/> when a model-write is awaiting
/// the user's confirmation. The panel then shows Confirm/Cancel and calls
/// <see cref="ConfirmAsync"/> or <see cref="CancelPending"/>.
///
/// Phase 3: <see cref="PlaceholderChatService"/> (offline, no model call).
/// Phase 4: <see cref="OrchestratorChatService"/> (Ollama → dry-run → confirm → commit).
/// </summary>
public interface IChatService
{
    /// <summary>Send the user's message; run the agent loop until it needs the user.</summary>
    Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default);

    /// <summary>Commit the write that is currently awaiting confirmation, then continue.</summary>
    Task<ChatTurn> ConfirmAsync(CancellationToken ct = default);

    /// <summary>Discard the write that is awaiting confirmation.</summary>
    void CancelPending();

    /// <summary>Start a fresh conversation — clears history and any pending write.</summary>
    void Reset();

    /// <summary>Compact snapshot of recent backend conversation — logged on thumbs-down.</summary>
    string SnapshotContext();

    /// <summary>
    /// Restore the parameter values that were overwritten by the last confirmed
    /// update_where. Only valid when the previous <see cref="ChatTurn.CanUndo"/> was true.
    /// </summary>
    Task<ChatTurn> UndoAsync(CancellationToken ct = default);

    /// <summary>
    /// Register an imported spreadsheet so the next user message can reference it.
    /// Returns a preview turn (table bubble + prompt hint) immediately — no Revit call.
    /// </summary>
    ChatTurn IngestImport(ImportedTable table);
}

/// <summary>
/// The result of one turn: bubbles to append, an optional pending change that
/// requires confirmation, and the estimated context fill (0..1) after the turn.
/// <see cref="CanUndo"/> is true immediately after a successful update_where commit —
/// the UI shows "Hoàn tác" while this is true.
/// </summary>
public sealed record ChatTurn(
    IReadOnlyList<ChatReply> Replies,
    ChangePreview? Pending = null,
    double ContextUsage = 0,
    IReadOnlyList<ResultTable>? Tables = null,
    bool CanUndo = false);

/// <summary>One assistant bubble. <see cref="IsError"/> renders as an error bubble.</summary>
public readonly record struct ChatReply(string Text, bool IsError = false);
