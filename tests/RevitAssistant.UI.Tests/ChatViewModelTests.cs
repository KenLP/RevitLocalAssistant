using Xunit;
using RevitAssistant.UI;
using FluentAssertions;

namespace RevitAssistant.UI.Tests;

public sealed class ChatViewModelTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeChatService : IChatService
    {
        private readonly ChatTurn _turn;
        public string? LastInput { get; private set; }
        public int ConfirmCount { get; private set; }
        public int CancelCount { get; private set; }
        public FakeChatService(ChatTurn turn) => _turn = turn;

        public Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default)
        {
            LastInput = userInput;
            return Task.FromResult(_turn);
        }
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
        {
            ConfirmCount++;
            return Task.FromResult(new ChatTurn(new[] { new ChatReply("đã ghi") }));
        }
        public void CancelPending() => CancelCount++;
    }

    private sealed class ThrowingChatService : IChatService
    {
        public Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public void CancelPending() { }
    }

    private sealed class GatedChatService : IChatService
    {
        public readonly TaskCompletionSource<ChatTurn> Gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatTurn> SendAsync(string userInput, CancellationToken ct = default) => Gate.Task;
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default) => Gate.Task;
        public void CancelPending() { }
    }

    private static ChatTurn Reply(string text, bool isError = false) =>
        new(new[] { new ChatReply(text, isError) });

    // ── Send happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Send_AddsUserThenAssistantMessage()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok"))) { InputText = "liệt kê phòng" };

        await vm.SendCommand.ExecuteAsync(null);

        vm.Messages.Should().HaveCount(2);
        vm.Messages[0].Kind.Should().Be(ChatMessageKind.User);
        vm.Messages[0].Text.Should().Be("liệt kê phòng");
        vm.Messages[1].Kind.Should().Be(ChatMessageKind.Assistant);
        vm.Messages[1].Text.Should().Be("ok");
    }

    [Fact]
    public async Task Send_ClearsInputText()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok"))) { InputText = "hello" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_TrimsWhitespaceFromUserMessage()
    {
        var fake = new FakeChatService(Reply("ok"));
        var vm = new ChatViewModel(fake) { InputText = "  xin chào  " };

        await vm.SendCommand.ExecuteAsync(null);

        fake.LastInput.Should().Be("xin chào");
        vm.Messages[0].Text.Should().Be("xin chào");
    }

    [Fact]
    public async Task Send_ResetsBusyAfterCompletion()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok"))) { InputText = "hi" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.IsBusy.Should().BeFalse();
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_ErrorReply_RendersErrorBubble()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("không tìm thấy", isError: true)))
        {
            InputText = "x",
        };
        await vm.SendCommand.ExecuteAsync(null);

        vm.Messages[1].Kind.Should().Be(ChatMessageKind.Error);
        vm.Messages[1].Text.Should().Be("không tìm thấy");
    }

    [Fact]
    public async Task Send_ServiceThrows_RendersErrorBubbleAndClearsBusy()
    {
        var vm = new ChatViewModel(new ThrowingChatService()) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);

        vm.Messages.Should().HaveCount(2);
        vm.Messages[1].Kind.Should().Be(ChatMessageKind.Error);
        vm.Messages[1].Text.Should().Contain("boom");
        vm.IsBusy.Should().BeFalse();
    }

    // ── Pending / confirm / cancel ───────────────────────────────────────────

    [Fact]
    public async Task Send_WithPending_SetsPendingAndDisablesSend()
    {
        var preview = new ChangePreview("Sửa", "tóm tắt", Array.Empty<PreviewRow>(), 3);
        var turn = new ChatTurn(new[] { new ChatReply("hiểu là…") }, preview);
        var vm = new ChatViewModel(new FakeChatService(turn)) { InputText = "đổi tham số" };

        await vm.SendCommand.ExecuteAsync(null);

        vm.HasPending.Should().BeTrue();
        vm.PendingPreview.Should().Be(preview);
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
        vm.CancelCommand.CanExecute(null).Should().BeTrue();

        vm.InputText = "thêm yêu cầu";
        vm.SendCommand.CanExecute(null).Should().BeFalse("must resolve the pending change first");
    }

    [Fact]
    public async Task Confirm_CommitsAndClearsPending()
    {
        var preview = new ChangePreview("Sửa", "tóm tắt", Array.Empty<PreviewRow>(), 1);
        var fake = new FakeChatService(new ChatTurn(Array.Empty<ChatReply>(), preview));
        var vm = new ChatViewModel(fake) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        fake.ConfirmCount.Should().Be(1);
        vm.HasPending.Should().BeFalse();
        vm.Messages.Should().Contain(m => m.Text == "đã ghi");
    }

    [Fact]
    public async Task Cancel_DiscardsPendingAndAddsSystemBubble()
    {
        var preview = new ChangePreview("Sửa", "tóm tắt", Array.Empty<PreviewRow>(), 1);
        var fake = new FakeChatService(new ChatTurn(Array.Empty<ChatReply>(), preview));
        var vm = new ChatViewModel(fake) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);

        vm.CancelCommand.Execute(null);

        fake.CancelCount.Should().Be(1);
        vm.HasPending.Should().BeFalse();
        vm.Messages.Last().Kind.Should().Be(ChatMessageKind.System);
    }

    [Fact]
    public void ConfirmCancel_NoPending_CannotExecute()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok")));
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
        vm.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    // ── CanExecute ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CanSend_EmptyOrWhitespaceInput_False(string input)
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok"))) { InputText = input };
        vm.SendCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSend_WithInput_True()
    {
        var vm = new ChatViewModel(new FakeChatService(Reply("ok"))) { InputText = "hi" };
        vm.SendCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CanSend_WhileBusy_False()
    {
        var gated = new GatedChatService();
        var vm = new ChatViewModel(gated) { InputText = "hi" };

        var sending = vm.SendCommand.ExecuteAsync(null);

        vm.IsBusy.Should().BeTrue();
        vm.SendCommand.CanExecute(null).Should().BeFalse();

        gated.Gate.SetResult(Reply("done"));
        await sending;

        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullService_Throws()
    {
        var act = () => new ChatViewModel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DesignTimeConstructor_SeedsSampleMessages()
    {
        var vm = new ChatViewModel();
        vm.Messages.Should().NotBeEmpty();
    }
}

public sealed class ChatMessageVmTests
{
    [Fact]
    public void FromUser_SetsUserKindAndIsUser()
    {
        var m = ChatMessageVm.FromUser("hi");
        m.Kind.Should().Be(ChatMessageKind.User);
        m.IsUser.Should().BeTrue();
        m.Sender.Should().Be("Bạn");
        m.Text.Should().Be("hi");
    }

    [Fact]
    public void FromAssistant_NotUser()
    {
        var m = ChatMessageVm.FromAssistant("xin chào");
        m.Kind.Should().Be(ChatMessageKind.Assistant);
        m.IsUser.Should().BeFalse();
    }

    [Fact]
    public void FromError_HasErrorKind() =>
        ChatMessageVm.FromError("oops").Kind.Should().Be(ChatMessageKind.Error);

    [Fact]
    public void FromSystem_HasSystemKind() =>
        ChatMessageVm.FromSystem("note").Kind.Should().Be(ChatMessageKind.System);
}

public sealed class PlaceholderChatServiceTests
{
    [Fact]
    public async Task SendAsync_EchoesUserInput_NoPending()
    {
        var svc = new PlaceholderChatService();
        var turn = await svc.SendAsync("đổi tên tường");
        turn.Pending.Should().BeNull();
        turn.Replies.Should().ContainSingle();
        turn.Replies[0].Text.Should().Contain("đổi tên tường");
    }
}
