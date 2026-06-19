using RevitAssistant.Llm;

namespace RevitAssistant.UI;

/// <summary>
/// Sliding-window trim for the conversation so it never overflows the model's
/// context. Keeps the system prompt (index 0) and the most recent turns, dropping
/// the OLDEST whole turns first. Dropping a whole turn (a User message and every
/// Assistant/Tool message up to the next User message) preserves tool_call↔tool
/// pairing — never leaves a dangling tool call.
/// </summary>
public static class ConversationTrimmer
{
    private const int KeepLastTurns = 2;   // always retain at least this many recent turns

    /// <summary>
    /// Trim <paramref name="conv"/> in place when it exceeds <paramref name="ceiling"/>
    /// tokens, removing oldest turns until at/under <paramref name="target"/>.
    /// Returns the number of messages removed.
    /// </summary>
    public static int TrimToFit(List<ChatMessage> conv, int ceiling, int target)
    {
        if (ContextEstimator.Estimate(conv) <= ceiling) return 0;

        var hasSystem = conv.Count > 0 && conv[0].Role == ChatRole.System;
        var firstRemovable = hasSystem ? 1 : 0;
        var removed = 0;

        while (ContextEstimator.Estimate(conv) > target)
        {
            // Turn boundaries = indices of User messages within the removable region.
            var userIdx = new List<int>();
            for (var i = firstRemovable; i < conv.Count; i++)
                if (conv[i].Role == ChatRole.User) userIdx.Add(i);

            if (userIdx.Count <= KeepLastTurns) break;   // keep the most recent turns

            // Remove the oldest turn: [firstUser .. secondUser-1].
            var start = userIdx[0];
            var end = userIdx[1];                        // exclusive
            conv.RemoveRange(start, end - start);
            removed += end - start;
        }

        return removed;
    }
}
