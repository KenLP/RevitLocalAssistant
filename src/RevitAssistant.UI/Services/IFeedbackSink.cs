namespace RevitAssistant.UI;

/// <summary>One feedback record on an assistant reply.</summary>
public sealed record FeedbackEntry(
    DateTime Time,
    bool Liked,                 // false = thumbs-down (the case we act on)
    string MessageText,         // the assistant reply being rated
    string? Reason,             // optional user comment on what was wrong
    string ContextSnapshot);    // compact backend conversation snapshot for debugging

/// <summary>Sink for user feedback. Thumbs-down is logged for later improvement.</summary>
public interface IFeedbackSink
{
    void Record(FeedbackEntry entry);

    /// <summary>
    /// Discard everything recorded so far. The log holds the user's own conversations,
    /// so getting rid of it must not require finding a file under %APPDATA% by hand.
    /// </summary>
    void Clear();
}

/// <summary>No-op sink (design-time / tests).</summary>
public sealed class NullFeedbackSink : IFeedbackSink
{
    public void Record(FeedbackEntry entry) { }
    public void Clear() { }
}
