using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RevitAssistant.UI;

public enum ChatMessageKind { User, Assistant, System, Error }

public enum FeedbackKind { None, Up, Down }

/// <summary>
/// One chat bubble. <see cref="Text"/> is observable so Phase 4 streaming can
/// append tokens to an existing assistant bubble in place. Assistant bubbles also
/// carry 👍/👎 feedback (wired by <see cref="ChatViewModel"/>).
/// </summary>
public sealed partial class ChatMessageVm : ObservableObject
{
    public ChatMessageKind Kind { get; init; }
    public string Sender { get; init; } = "";

    [ObservableProperty]
    private string _text = string.Empty;

    public bool IsUser => Kind == ChatMessageKind.User;

    /// <summary>Non-null when this bubble is a result table instead of text.</summary>
    public ResultTable? Table { get; init; }
    public bool IsTable => Table is not null;
    public bool IsText => Table is null;

    /// <summary>Only assistant text bubbles can be rated.</summary>
    public bool CanRate { get; init; }

    [ObservableProperty]
    private FeedbackKind _feedback = FeedbackKind.None;

    [ObservableProperty]
    private bool _showReasonBox;

    [ObservableProperty]
    private string _reasonText = string.Empty;

    /// <summary>Set by ChatViewModel: (message, liked).</summary>
    public Action<ChatMessageVm, bool>? RateHandler { get; set; }

    /// <summary>Set by ChatViewModel: submit the typed reason for a thumbs-down.</summary>
    public Action<ChatMessageVm>? SubmitReasonHandler { get; set; }

    [RelayCommand]
    private void ThumbUp()
    {
        Feedback = FeedbackKind.Up;
        ShowReasonBox = false;
        RateHandler?.Invoke(this, true);
    }

    [RelayCommand]
    private void ThumbDown()
    {
        Feedback = FeedbackKind.Down;
        ShowReasonBox = true;           // ask what was wrong
        RateHandler?.Invoke(this, false);
    }

    [RelayCommand]
    private void SubmitReason()
    {
        SubmitReasonHandler?.Invoke(this);
        ShowReasonBox = false;
    }

    /// <summary>Copy the table to the clipboard as TSV (paste-friendly into Excel).</summary>
    [RelayCommand]
    private void CopyTable()
    {
        if (Table is null) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join('\t', Table.Columns));
        foreach (var row in Table.Rows) sb.AppendLine(string.Join('\t', row));
        try { System.Windows.Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy */ }
    }

    public static ChatMessageVm FromUser(string text) =>
        new() { Kind = ChatMessageKind.User, Sender = "Bạn", Text = text };

    public static ChatMessageVm FromAssistant(string text) =>
        new() { Kind = ChatMessageKind.Assistant, Sender = "Trợ lý", Text = text, CanRate = true };

    public static ChatMessageVm FromSystem(string text) =>
        new() { Kind = ChatMessageKind.System, Sender = "Hệ thống", Text = text };

    public static ChatMessageVm FromError(string text) =>
        new() { Kind = ChatMessageKind.Error, Sender = "Lỗi", Text = text };

    public static ChatMessageVm FromTable(ResultTable table) =>
        new()
        {
            Kind = ChatMessageKind.Assistant,
            Sender = "Kết quả",
            Table = table,
            Text = table.Truncated ? $"Hiển thị {table.Rows.Count}/{table.TotalCount} dòng" : "",
        };
}
