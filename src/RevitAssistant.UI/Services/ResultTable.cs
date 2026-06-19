namespace RevitAssistant.UI;

/// <summary>
/// A tabular result rendered as a grid in the chat. Built deterministically by
/// <see cref="TableExtractor"/> from a tool result, so the displayed rows are
/// always complete (the model can't drop any).
/// </summary>
public sealed record ResultTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalCount,
    bool Truncated);
