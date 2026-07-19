using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RevitAssistant.UI;

/// <summary>
/// Drives the chat panel: owns the message list, the input box, the send loop,
/// and the confirm/cancel/undo flow for pending model-writes.
/// All Revit/Ollama work is delegated to <see cref="IChatService"/>, so this class
/// has no Revit API dependency and is unit-testable on any thread.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly IChatService _chat;
    private readonly IFeedbackSink _feedback;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowImportHint))]
    private string _inputText = string.Empty;

    private static readonly string[] _importKeywords =
    {
        "upload", "tải file", "tải lên", "úp lên", "úp file",
        "nhập file", "import file", "nhập dữ liệu", "đính kèm", "attach",
        "file excel", "file csv", ".xlsx", ".csv",
    };

    public bool ShowImportHint =>
        _importKeywords.Any(k => InputText.Contains(k, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private ChangePreview? _pendingPreview;

    public bool HasPending => PendingPreview is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextWarning))]
    private int _contextUsagePercent;

    /// <summary>True at ≥85% — surfaced as a warning so the user can 🗑 Xóa.</summary>
    public bool ContextWarning => ContextUsagePercent >= 85;

    /// <summary>True immediately after a successful update_where commit.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _hasUndo;

    public ChatViewModel(IChatService chat, IFeedbackSink? feedback = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _feedback = feedback ?? new NullFeedbackSink();
    }

    /// <summary>Design-time / XAML-preview constructor — seeds sample bubbles.</summary>
    public ChatViewModel() : this(new PlaceholderChatService())
    {
        Messages.Add(ChatMessageVm.FromAssistant(
            "Xin chào! Tôi là trợ lý Revit chạy offline. Hỏi tôi bằng tiếng Việt hoặc English."));
        Messages.Add(ChatMessageVm.FromUser("Đổi 'Comments' = 'Đã duyệt' cho tất cả cửa thoát hiểm"));
        Messages.Add(ChatMessageVm.FromAssistant("Hiểu là: cập nhật tham số Comments cho các cửa…"));
    }

    // ── Send ─────────────────────────────────────────────────────────────────

    private bool CanSend() => !IsBusy && !HasPending && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;

        Messages.Add(ChatMessageVm.FromUser(text));
        InputText = string.Empty;
        IsBusy = true;
        HasUndo = false;
        try
        {
            var turn = await _chat.SendAsync(text).ConfigureAwait(true);
            ApplyTurn(turn);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessageVm.FromError($"Lỗi: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Confirm / Cancel pending write ───────────────────────────────────────

    private bool CanConfirm() => !IsBusy && HasPending;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        PendingPreview = null;
        IsBusy = true;
        try
        {
            var turn = await _chat.ConfirmAsync().ConfigureAwait(true);
            ApplyTurn(turn);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessageVm.FromError($"Lỗi khi ghi: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCancel() => !IsBusy && HasPending;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _chat.CancelPending();
        PendingPreview = null;
        Messages.Add(ChatMessageVm.FromSystem("Đã hủy thao tác."));
    }

    // ── Undo last edit ───────────────────────────────────────────────────────

    private bool CanUndo() => HasUndo && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        HasUndo = false;
        IsBusy = true;
        try
        {
            var turn = await _chat.UndoAsync().ConfigureAwait(true);
            ApplyTurn(turn);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessageVm.FromError($"Lỗi hoàn tác: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Import file ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void ImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn file để nhập vào Revit",
            Filter = "Excel / CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Tất cả tệp (*.*)|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var table = CsvXlsxReader.Read(dialog.FileName);
            var turn = _chat.IngestImport(table);
            ApplyTurn(turn);
        }
        catch (Exception ex)
        {
            Messages.Add(ChatMessageVm.FromError($"Không đọc được file: {ex.Message}"));
        }
    }

    // ── New chat ─────────────────────────────────────────────────────────────

    private bool CanReset() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        _chat.Reset();
        Messages.Clear();
        PendingPreview = null;
        InputText = string.Empty;
        ContextUsagePercent = 0;
        HasUndo = false;
    }

    /// <summary>
    /// Deletes the local diagnostics log. Thumbs-down feedback stores the assistant's
    /// reply and a conversation snapshot on disk; the user owns that data and must be
    /// able to remove it from inside the add-in.
    /// </summary>
    [RelayCommand]
    private void ClearDiagnostics()
    {
        _feedback.Clear();
        Messages.Add(ChatMessageVm.FromSystem(
            "Đã xoá nhật ký chẩn đoán (phản hồi 👎) khỏi máy."));
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private void ApplyTurn(ChatTurn turn)
    {
        foreach (var reply in turn.Replies)
        {
            if (reply.IsError)
            {
                Messages.Add(ChatMessageVm.FromError(reply.Text));
            }
            else
            {
                var m = ChatMessageVm.FromAssistant(reply.Text);
                m.RateHandler = HandleRate;
                m.SubmitReasonHandler = HandleSubmitReason;
                Messages.Add(m);
            }
        }

        if (turn.Tables is { Count: > 0 })
            foreach (var table in turn.Tables)
                Messages.Add(ChatMessageVm.FromTable(table));

        PendingPreview = turn.Pending;
        ContextUsagePercent = (int)System.Math.Round(turn.ContextUsage * 100);
        HasUndo = turn.CanUndo;
    }

    // ── Feedback ─────────────────────────────────────────────────────────────

    private void HandleRate(ChatMessageVm msg, bool liked)
    {
        if (liked) return;   // 👍 — nothing to log; just acknowledged in the UI
        // 👎 — capture immediately (context may change before the user types a reason).
        _feedback.Record(new FeedbackEntry(
            DateTime.Now, Liked: false, msg.Text, Reason: null, _chat.SnapshotContext()));
    }

    private void HandleSubmitReason(ChatMessageVm msg)
    {
        var reason = msg.ReasonText?.Trim();
        _feedback.Record(new FeedbackEntry(
            DateTime.Now, Liked: false, msg.Text,
            string.IsNullOrEmpty(reason) ? null : reason, _chat.SnapshotContext()));
        msg.ReasonText = string.Empty;
        Messages.Add(ChatMessageVm.FromSystem("Cảm ơn phản hồi — đã ghi nhận để cải thiện."));
    }
}
