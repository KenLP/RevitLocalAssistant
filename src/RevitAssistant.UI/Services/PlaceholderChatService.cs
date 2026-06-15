namespace RevitAssistant.UI;

/// <summary>
/// Offline fallback / design-time service. Confirms the panel is wired without
/// requiring Ollama. Never produces a pending change.
/// Replaced at runtime by <see cref="OrchestratorChatService"/>.
/// </summary>
public sealed class PlaceholderChatService : IChatService
{
    public Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default)
    {
        var reply = new ChatReply(
            "✅ Bảng trợ lý hoạt động tốt.\n\n" +
            $"Bạn vừa nhập: “{userInput}”\n\n" +
            "Bộ điều phối AI offline sẽ trả lời khi Ollama đang chạy.");
        return Task.FromResult(new ChatTurn(new[] { reply }));
    }

    public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default) =>
        Task.FromResult(new ChatTurn(Array.Empty<ChatReply>()));

    public void CancelPending() { }

    public void Reset() { }
}
