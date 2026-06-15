namespace RevitAssistant.UI;

/// <summary>
/// Phase 3 placeholder. Confirms the panel is wired end-to-end without requiring
/// Ollama to be running. Replaced by the real orchestrator in Phase 4.
/// </summary>
public sealed class PlaceholderChatService : IChatService
{
    public Task<ChatReply> SendAsync(string userInput, CancellationToken ct = default)
    {
        var reply =
            "✅ Bảng trợ lý hoạt động tốt.\n\n" +
            $"Bạn vừa nhập: “{userInput}”\n\n" +
            "Bộ điều phối AI (Ollama → xem trước dry-run → xác nhận → ghi vào model) " +
            "sẽ được kết nối ở Phase 4.";
        return Task.FromResult(new ChatReply(reply));
    }
}
