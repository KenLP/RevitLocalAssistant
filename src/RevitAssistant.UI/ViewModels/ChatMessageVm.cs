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

    /// <summary>Save the table to a UTF-8 CSV file via SaveFileDialog.</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (Table is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Xuất bảng ra CSV",
            Filter = "CSV (*.csv)|*.csv|Tất cả tệp (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "revit_export",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join(',', Table.Columns.Select(CsvCell)));
            foreach (var row in Table.Rows)
                sb.AppendLine(string.Join(',', row.Select(CsvCell)));
            System.IO.File.WriteAllText(
                dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch { /* silent — user can see the file was not created */ }
    }

    /// <summary>
    /// Quotes a CSV cell and defuses spreadsheet formula injection.
    ///
    /// Values here come from the Revit model — a parameter such as Comments can legitimately
    /// contain any text a user typed. Excel and Sheets treat a leading =, +, - or @ as a
    /// formula, so exporting a cell like <c>=HYPERLINK(...)</c> or <c>=cmd|...</c> turns the
    /// export into something that executes when opened. Prefixing a tab keeps the value
    /// visible and text-only without changing what it reads as.
    /// </summary>
    internal static string CsvCell(string s)
    {
        var value = s.Length > 0 && (s[0] is '=' or '+' or '-' or '@'
                                     || s[0] == '\t' || s[0] == '\r')
            ? "\t" + s
            : s;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                 || value.Contains('\r') || value.Contains('\t')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
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
