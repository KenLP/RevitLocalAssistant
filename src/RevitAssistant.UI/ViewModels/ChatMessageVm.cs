using CommunityToolkit.Mvvm.ComponentModel;

namespace RevitAssistant.UI;

public enum ChatMessageKind { User, Assistant, System, Error }

/// <summary>
/// One chat bubble. <see cref="Text"/> is observable so Phase 4 streaming can
/// append tokens to an existing assistant bubble in place.
/// </summary>
public sealed partial class ChatMessageVm : ObservableObject
{
    public ChatMessageKind Kind { get; init; }
    public string Sender { get; init; } = "";

    [ObservableProperty]
    private string _text = string.Empty;

    public bool IsUser => Kind == ChatMessageKind.User;

    public static ChatMessageVm FromUser(string text) =>
        new() { Kind = ChatMessageKind.User, Sender = "Bạn", Text = text };

    public static ChatMessageVm FromAssistant(string text) =>
        new() { Kind = ChatMessageKind.Assistant, Sender = "Trợ lý", Text = text };

    public static ChatMessageVm FromSystem(string text) =>
        new() { Kind = ChatMessageKind.System, Sender = "Hệ thống", Text = text };

    public static ChatMessageVm FromError(string text) =>
        new() { Kind = ChatMessageKind.Error, Sender = "Lỗi", Text = text };
}
