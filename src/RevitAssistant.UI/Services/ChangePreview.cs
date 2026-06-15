namespace RevitAssistant.UI;

/// <summary>
/// A preview of a pending model-write, shown to the user with Confirm/Cancel
/// before anything is committed. Produced by <see cref="PreviewBuilder"/> from a
/// dry-run dispatcher result.
/// </summary>
public sealed record ChangePreview(
    string Title,
    string Summary,
    IReadOnlyList<PreviewRow> Rows,
    int TotalCount,
    int FailedCount = 0);

/// <summary>One line in a <see cref="ChangePreview"/> table.</summary>
public sealed record PreviewRow(string Element, string Detail, bool IsFailure = false);
