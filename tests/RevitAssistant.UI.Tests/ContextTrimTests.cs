using Xunit;
using RevitAssistant.UI;
using RevitAssistant.Llm;
using FluentAssertions;

namespace RevitAssistant.UI.Tests;

public sealed class ContextEstimatorTests
{
    [Fact]
    public void EstimateTokens_GrowsWithLength()
    {
        ContextEstimator.EstimateTokens("").Should().Be(0);
        ContextEstimator.EstimateTokens(new string('x', 35)).Should().BeGreaterThan(0);
        ContextEstimator.EstimateTokens(new string('x', 350))
            .Should().BeGreaterThan(ContextEstimator.EstimateTokens(new string('x', 35)));
    }

    [Fact]
    public void Estimate_SumsMessages()
    {
        var msgs = new[]
        {
            ChatMessage.System(new string('a', 350)),
            ChatMessage.User(new string('b', 350)),
        };
        ContextEstimator.Estimate(msgs).Should().BeGreaterThan(150);
    }
}

public sealed class ConversationTrimmerTests
{
    private static List<ChatMessage> Build(int turns, int charsPerMsg)
    {
        var list = new List<ChatMessage> { ChatMessage.System(new string('s', charsPerMsg)) };
        for (var i = 0; i < turns; i++)
        {
            list.Add(ChatMessage.User(new string('u', charsPerMsg)));
            list.Add(ChatMessage.System("noop")); // stand-in assistant text turn body
        }
        return list;
    }

    [Fact]
    public void TrimToFit_UnderCeiling_NoChange()
    {
        var conv = Build(2, 100);
        var removed = ConversationTrimmer.TrimToFit(conv, ceiling: 100000, target: 50000);
        removed.Should().Be(0);
    }

    [Fact]
    public void TrimToFit_OverCeiling_DropsOldestKeepsSystemAndRecent()
    {
        var conv = Build(10, 400);                       // big history
        var before = conv.Count;
        var removed = ConversationTrimmer.TrimToFit(conv, ceiling: 500, target: 300);

        removed.Should().BeGreaterThan(0);
        conv.Count.Should().BeLessThan(before);
        conv[0].Role.Should().Be(ChatRole.System);       // system prompt preserved
        // at least the most recent turns survive
        conv.Should().Contain(m => m.Role == ChatRole.User);
    }

    [Fact]
    public void TrimToFit_AlwaysKeepsRecentTurns()
    {
        var conv = Build(3, 100000);                      // every msg huge
        ConversationTrimmer.TrimToFit(conv, ceiling: 1, target: 1);
        // can't go below system + last 2 turns
        var users = conv.FindAll(m => m.Role == ChatRole.User).Count;
        users.Should().BeGreaterThanOrEqualTo(2);
        conv[0].Role.Should().Be(ChatRole.System);
    }
}

public sealed class ContextWarningVmTests
{
    private sealed class FakeChat : IChatService
    {
        private readonly double _usage;
        public FakeChat(double usage) => _usage = usage;
        public Task<ChatTurn> SendAsync(string i, CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(new[] { new ChatReply("ok") }, null, _usage));
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(System.Array.Empty<ChatReply>()));
        public void CancelPending() { }
        public void Reset() { }
    }

    [Fact]
    public async Task Usage_BelowThreshold_NoWarning()
    {
        var vm = new ChatViewModel(new FakeChat(0.50)) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.ContextUsagePercent.Should().Be(50);
        vm.ContextWarning.Should().BeFalse();
    }

    [Fact]
    public async Task Usage_AtOrAbove85_Warns()
    {
        var vm = new ChatViewModel(new FakeChat(0.87)) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.ContextUsagePercent.Should().Be(87);
        vm.ContextWarning.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_ClearsUsage()
    {
        var vm = new ChatViewModel(new FakeChat(0.90)) { InputText = "x" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.ContextWarning.Should().BeTrue();
        vm.ResetCommand.Execute(null);
        vm.ContextUsagePercent.Should().Be(0);
        vm.ContextWarning.Should().BeFalse();
    }
}
