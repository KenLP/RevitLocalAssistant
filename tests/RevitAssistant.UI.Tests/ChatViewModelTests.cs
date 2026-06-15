using Xunit;
using RevitAssistant.UI;
using FluentAssertions;

namespace RevitAssistant.UI.Tests;

public sealed class ChatViewModelTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeChatService : IChatService
    {
        private readonly ChatReply _reply;
        public string? LastInput { get; private set; }
        public FakeChatService(ChatReply reply) => _reply = reply;
        public Task<ChatReply> SendAsync(string userInput, CancellationToken ct = default)
        {
            LastInput = userInput;
            return Task.FromResult(_reply);
        }
    }

    private sealed class ThrowingChatService : IChatService
    {
        public Task<ChatReply> SendAsync(string userInput, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class GatedChatService : IChatService
    {
        public readonly TaskCompletionSource<ChatReply> Gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatReply> SendAsync(string userInput, CancellationToken ct = default)
            => Gate.Task;
    }

    // ── Send happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Send_AddsUserThenAssistantMessage()
    {
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("ok")))
        {
            InputText = "liệt kê phòng",
        };

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
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("ok")))
        {
            InputText = "hello",
        };

        await vm.SendCommand.ExecuteAsync(null);

        vm.InputText.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_TrimsWhitespaceFromUserMessage()
    {
        var fake = new FakeChatService(new ChatReply("ok"));
        var vm = new ChatViewModel(fake) { InputText = "  xin chào  " };

        await vm.SendCommand.ExecuteAsync(null);

        fake.LastInput.Should().Be("xin chào");
        vm.Messages[0].Text.Should().Be("xin chào");
    }

    [Fact]
    public async Task Send_ResetsBusyAfterCompletion()
    {
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("ok")))
        {
            InputText = "hi",
        };

        await vm.SendCommand.ExecuteAsync(null);

        vm.IsBusy.Should().BeFalse();
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_ErrorReply_RendersErrorBubble()
    {
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("không tìm thấy", IsError: true)))
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

    // ── CanExecute ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CanSend_EmptyOrWhitespaceInput_False(string input)
    {
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("ok"))) { InputText = input };
        vm.SendCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSend_WithInput_True()
    {
        var vm = new ChatViewModel(new FakeChatService(new ChatReply("ok"))) { InputText = "hi" };
        vm.SendCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CanSend_WhileBusy_False()
    {
        var gated = new GatedChatService();
        var vm = new ChatViewModel(gated) { InputText = "hi" };

        var sending = vm.SendCommand.ExecuteAsync(null);   // enters busy, awaits gate

        vm.IsBusy.Should().BeTrue();
        vm.SendCommand.CanExecute(null).Should().BeFalse();

        gated.Gate.SetResult(new ChatReply("done"));
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
    public void FromError_HasErrorKind()
    {
        ChatMessageVm.FromError("oops").Kind.Should().Be(ChatMessageKind.Error);
    }

    [Fact]
    public void FromSystem_HasSystemKind()
    {
        ChatMessageVm.FromSystem("note").Kind.Should().Be(ChatMessageKind.System);
    }
}

public sealed class PlaceholderChatServiceTests
{
    [Fact]
    public async Task SendAsync_EchoesUserInput()
    {
        var svc = new PlaceholderChatService();
        var reply = await svc.SendAsync("đổi tên tường");
        reply.IsError.Should().BeFalse();
        reply.Text.Should().Contain("đổi tên tường");
    }
}
