using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RevitAssistant.UI;

/// <summary>
/// Drives the chat panel: owns the message list, the input box, the send loop,
/// and the confirm/cancel flow for pending model-writes.
/// All Revit/Ollama work is delegated to <see cref="IChatService"/>, so this class
/// has no Revit API dependency and is unit-testable on any thread.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly IChatService _chat;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
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

    public ChatViewModel(IChatService chat)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
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
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private void ApplyTurn(ChatTurn turn)
    {
        foreach (var reply in turn.Replies)
            Messages.Add(reply.IsError
                ? ChatMessageVm.FromError(reply.Text)
                : ChatMessageVm.FromAssistant(reply.Text));

        PendingPreview = turn.Pending;
        ContextUsagePercent = (int)System.Math.Round(turn.ContextUsage * 100);
    }
}
