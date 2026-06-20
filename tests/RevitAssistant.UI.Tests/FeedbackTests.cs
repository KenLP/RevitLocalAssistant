using Xunit;
using RevitAssistant.UI;
using FluentAssertions;

namespace RevitAssistant.UI.Tests;

public sealed class FeedbackTests
{
    private sealed class FakeChat : IChatService
    {
        public Task<ChatTurn> SendAsync(string i, CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(new[] { new ChatReply("đáp án") }));
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(System.Array.Empty<ChatReply>()));
        public Task<ChatTurn> UndoAsync(CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(System.Array.Empty<ChatReply>()));
        public ChatTurn IngestImport(ImportedTable table)
            => new(System.Array.Empty<ChatReply>());
        public void CancelPending() { }
        public void Reset() { }
        public string SnapshotContext() => "BACKEND-CTX";
    }

    private sealed class RecordingSink : IFeedbackSink
    {
        public List<FeedbackEntry> Entries { get; } = new();
        public void Record(FeedbackEntry e) => Entries.Add(e);
    }

    private static async Task<(ChatViewModel vm, RecordingSink sink, ChatMessageVm reply)> Setup()
    {
        var sink = new RecordingSink();
        var vm = new ChatViewModel(new FakeChat(), sink) { InputText = "hỏi" };
        await vm.SendCommand.ExecuteAsync(null);
        var reply = vm.Messages.Single(m => m.Kind == ChatMessageKind.Assistant);
        return (vm, sink, reply);
    }

    [Fact]
    public async Task AssistantMessage_CanBeRated()
    {
        var (_, _, reply) = await Setup();
        reply.CanRate.Should().BeTrue();
    }

    [Fact]
    public async Task UserMessage_CannotBeRated()
    {
        var (vm, _, _) = await Setup();
        vm.Messages.First(m => m.Kind == ChatMessageKind.User).CanRate.Should().BeFalse();
    }

    [Fact]
    public async Task ThumbUp_DoesNotLog()
    {
        var (_, sink, reply) = await Setup();
        reply.ThumbUpCommand.Execute(null);
        reply.Feedback.Should().Be(FeedbackKind.Up);
        sink.Entries.Should().BeEmpty("a like needs no backend record");
    }

    [Fact]
    public async Task ThumbDown_LogsWithContext_AndShowsReasonBox()
    {
        var (_, sink, reply) = await Setup();
        reply.ThumbDownCommand.Execute(null);

        reply.Feedback.Should().Be(FeedbackKind.Down);
        reply.ShowReasonBox.Should().BeTrue();
        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Liked.Should().BeFalse();
        sink.Entries[0].MessageText.Should().Be("đáp án");
        sink.Entries[0].ContextSnapshot.Should().Be("BACKEND-CTX");
        sink.Entries[0].Reason.Should().BeNull();
    }

    [Fact]
    public async Task SubmitReason_LogsReason_AndThanks()
    {
        var (vm, sink, reply) = await Setup();
        reply.ThumbDownCommand.Execute(null);     // entry #1 (no reason)
        reply.ReasonText = "đếm sai số phòng";
        reply.SubmitReasonCommand.Execute(null);  // entry #2 (with reason)

        reply.ShowReasonBox.Should().BeFalse();
        sink.Entries.Should().HaveCount(2);
        sink.Entries[1].Reason.Should().Be("đếm sai số phòng");
        vm.Messages.Should().Contain(m => m.Kind == ChatMessageKind.System && m.Text.Contains("Cảm ơn"));
    }
}

public sealed class FileFeedbackSinkTests
{
    [Fact]
    public void Record_AppendsJsonLine()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ra_feedback_{System.Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = new FileFeedbackSink(path);
            sink.Record(new FeedbackEntry(System.DateTime.Now, false, "msg", "wrong count", "ctx"));
            var text = System.IO.File.ReadAllText(path);
            text.Should().Contain("down").And.Contain("wrong count").And.Contain("msg");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Record_BadPath_DoesNotThrow()
    {
        var sink = new FileFeedbackSink("\0:/invalid<>path/feedback.jsonl");
        var act = () => sink.Record(new FeedbackEntry(System.DateTime.Now, false, "m", null, "c"));
        act.Should().NotThrow();
    }
}
