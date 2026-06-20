namespace RevitAssistant.UI;

/// <summary>
/// In-memory representation of a spreadsheet that the user imported.
/// Rows include all data rows (no header). Columns are the header names.
/// </summary>
public sealed record ImportedTable(
    string FileName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalRowCount);
