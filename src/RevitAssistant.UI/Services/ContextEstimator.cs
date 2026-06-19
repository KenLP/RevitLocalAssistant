using RevitAssistant.Llm;

namespace RevitAssistant.UI;

/// <summary>
/// Rough token estimator for the conversation. We have no real tokenizer client-side,
/// so we approximate from characters (~3.5 chars/token covers VI text + JSON tool
/// payloads, slightly conservative). Used to drive auto-trim and the context gauge.
/// </summary>
public static class ContextEstimator
{
    private const double CharsPerToken = 3.5;
    private const int PerMessageOverhead = 4;   // role/formatting framing

    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)System.Math.Ceiling(text!.Length / CharsPerToken);

    public static int EstimateMessage(ChatMessage m)
    {
        var t = PerMessageOverhead + EstimateTokens(m.Content);
        if (m.ToolCalls is { Count: > 0 })
            foreach (var tc in m.ToolCalls)
                t += PerMessageOverhead + EstimateTokens(tc.FunctionName) + EstimateTokens(tc.ArgumentsJson);
        if (m.ToolCallId is not null) t += EstimateTokens(m.ToolCallId);
        return t;
    }

    public static int Estimate(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages) total += EstimateMessage(m);
        return total;
    }
}
