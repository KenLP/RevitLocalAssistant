namespace RevitAssistant.UI;

/// <summary>
/// Parsed representation of the <c>import_data</c> tool call that the LLM emits
/// after seeing the imported table and the user's intent.
/// </summary>
public abstract record ImportSpec;

/// <summary>
/// Match elements by one column→param pair, then write one or more column→param values.
/// Example: match Mark==DoorMark, set "Fire Rating" from column FireRating.
/// </summary>
public sealed record UpdateParamsSpec(
    string Category,
    ColParamPair Match,
    IReadOnlyList<ColParamPair> Sets) : ImportSpec;

/// <summary>Create ViewSheets: one sheet per row using two columns.</summary>
public sealed record CreateSheetsSpec(
    string NumberColumn,
    string NameColumn) : ImportSpec;

/// <param name="Column">Column name in the imported spreadsheet.</param>
/// <param name="Param">Exact Revit parameter name.</param>
public sealed record ColParamPair(string Column, string Param);
