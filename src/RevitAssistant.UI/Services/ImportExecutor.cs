using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Deterministic executor for file-import operations.
/// Performs a two-phase flow (dry-run → commit) using Core commands so the
/// user sees what will happen before anything is written to Revit.
/// </summary>
public static class ImportExecutor
{
    // ── Dry-run ───────────────────────────────────────────────────────────────

    public static async Task<ImportDryRunResult> DryRunAsync(
        IRevitBridge revit, ImportedTable table, ImportSpec spec, CancellationToken ct)
    {
        return spec switch
        {
            UpdateParamsSpec up => await DryRunUpdateAsync(revit, table, up, ct).ConfigureAwait(false),
            CreateSheetsSpec cs => await DryRunSheetsAsync(revit, table, cs, ct).ConfigureAwait(false),
            _ => new ImportDryRunResult("Loại thao tác không được hỗ trợ.", spec),
        };
    }

    private static async Task<ImportDryRunResult> DryRunUpdateAsync(
        IRevitBridge revit, ImportedTable table, UpdateParamsSpec spec, CancellationToken ct)
    {
        // Request the match param + all set params so we can build the lookup in one call.
        var fields = new JsonArray();
        fields.Add(spec.Match.Param);
        foreach (var s in spec.Sets) if (!FieldInArray(fields, s.Param)) fields.Add(s.Param);

        var findArgs = new JsonObject
        {
            ["category"] = spec.Category,
            ["limit"] = 5000,
            ["fields"] = fields,
        };

        var env = await revit.CallAsync("find_elements", findArgs, false, ct).ConfigureAwait(false);
        if (!IsOk(env))
            return new ImportDryRunResult(ErrorOf(env), spec);

        // Build lookup: display-value of match param → elementId (case-insensitive)
        var lookup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (env["data"]?["elements"] is JsonArray elements)
        {
            foreach (var el in elements.OfType<JsonObject>())
            {
                var id = TryLong(el["id"]);
                if (id is null) continue;
                var fieldsObj = el["fields"] as JsonObject;
                // Prefer display string; fall back to raw value
                var matchVal = fieldsObj?[spec.Match.Param + "_display"]?.GetValue<string>()
                            ?? fieldsObj?[spec.Match.Param]?.ToString();
                if (!string.IsNullOrWhiteSpace(matchVal) && !lookup.ContainsKey(matchVal!))
                    lookup[matchVal!] = id.Value;
            }
        }

        // Match each import row against the lookup
        var matchColIdx = ColIndex(table, spec.Match.Column);
        var matched = new List<RowMatch>();
        var unmatched = new List<(int RowNum, string MatchVal)>();

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var matchVal = matchColIdx >= 0 && matchColIdx < row.Count ? row[matchColIdx] : "";
            if (lookup.TryGetValue(matchVal, out var eid))
                matched.Add(new RowMatch(i + 2, eid, matchVal, row, table.Columns));
            else
                unmatched.Add((i + 2, matchVal));
        }

        return new ImportDryRunResult(spec, lookup.Count, matched, unmatched);
    }

    private static async Task<ImportDryRunResult> DryRunSheetsAsync(
        IRevitBridge revit, ImportedTable table, CreateSheetsSpec spec, CancellationToken ct)
    {
        var sheetsEnv = await revit.CallAsync("list_sheets", new JsonObject(), false, ct)
            .ConfigureAwait(false);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sheetsEnv["data"]?["sheets"] is JsonArray arr)
            foreach (var s in arr.OfType<JsonObject>())
            {
                var num = s["sheetNumber"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(num)) existing.Add(num!);
            }

        var numIdx = ColIndex(table, spec.NumberColumn);
        var nameIdx = ColIndex(table, spec.NameColumn);

        var toCreate = new List<(int RowNum, string Number, string Name)>();
        var skipped = new List<(int RowNum, string Number)>();

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var num = numIdx >= 0 && numIdx < row.Count ? row[numIdx].Trim() : "";
            var name = nameIdx >= 0 && nameIdx < row.Count ? row[nameIdx].Trim() : "";
            if (string.IsNullOrEmpty(num)) continue;
            if (existing.Contains(num))
                skipped.Add((i + 2, num));
            else
                toCreate.Add((i + 2, num, name));
        }

        return new ImportDryRunResult(spec, toCreate, skipped);
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    public static async Task<ImportCommitResult> CommitAsync(
        IRevitBridge revit, ImportPending pending, CancellationToken ct)
    {
        return pending.Spec switch
        {
            UpdateParamsSpec up => await CommitUpdateAsync(revit, pending.Table, up, pending.DryRun, ct)
                .ConfigureAwait(false),
            CreateSheetsSpec cs => await CommitSheetsAsync(revit, pending.Table, cs, pending.DryRun, ct)
                .ConfigureAwait(false),
            _ => new ImportCommitResult(0, 0, "Loại thao tác không được hỗ trợ."),
        };
    }

    private static async Task<ImportCommitResult> CommitUpdateAsync(
        IRevitBridge revit, ImportedTable table, UpdateParamsSpec spec,
        ImportDryRunResult dryRun, CancellationToken ct)
    {
        // Build the items array from matched rows
        var items = new JsonArray();
        foreach (var match in dryRun.MatchedRows)
        {
            foreach (var set in spec.Sets)
            {
                var colIdx = ColIndex(table, set.Column);
                var value = colIdx >= 0 && colIdx < match.Row.Count ? match.Row[colIdx] : "";
                items.Add(new JsonObject
                {
                    ["elementId"] = match.ElementId,
                    ["parameterName"] = set.Param,
                    ["value"] = value,
                });
            }
        }

        if (items.Count == 0)
            return new ImportCommitResult(0, 0, null);

        var env = await revit.CallAsync("import_parameters", new JsonObject { ["items"] = items },
            false, ct).ConfigureAwait(false);

        if (!IsOk(env))
            return new ImportCommitResult(0, 0, ErrorOf(env));

        var data = env["data"] as JsonObject;
        var applied = data?["applied"]?.GetValue<int>() ?? 0;
        var failed = data?["failed"]?.GetValue<int>() ?? 0;
        return new ImportCommitResult(applied / Math.Max(1, spec.Sets.Count), failed, null);
    }

    private static async Task<ImportCommitResult> CommitSheetsAsync(
        IRevitBridge revit, ImportedTable table, CreateSheetsSpec spec,
        ImportDryRunResult dryRun, CancellationToken ct)
    {
        var created = 0;
        var failed = 0;

        foreach (var (_, number, name) in dryRun.SheetsToCreate)
        {
            var args = new JsonObject { ["sheetNumber"] = number, ["sheetName"] = name };
            var env = await revit.CallAsync("create_sheet", args, false, ct).ConfigureAwait(false);
            if (IsOk(env)) created++;
            else failed++;
        }

        return new ImportCommitResult(created, failed, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ColIndex(ImportedTable table, string colName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (string.Equals(table.Columns[i], colName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool FieldInArray(JsonArray arr, string field)
    {
        foreach (var n in arr)
            if (n?.GetValue<string>() == field) return true;
        return false;
    }

    private static bool IsOk(JsonObject env) =>
        env["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string ErrorOf(JsonObject env)
    {
        var err = env["error"] as JsonObject;
        return err?["message"]?.GetValue<string>() ?? "lỗi không xác định";
    }

    private static long? TryLong(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<long>(); } catch { }
        try { return (long)n.GetValue<int>(); } catch { }
        return null;
    }
}

// ── Result types ─────────────────────────────────────────────────────────────

public sealed class ImportDryRunResult
{
    // Update-params variant
    public ImportDryRunResult(
        ImportSpec spec, int totalRevitElements,
        IReadOnlyList<RowMatch> matched,
        IReadOnlyList<(int RowNum, string MatchVal)> unmatched)
    {
        Spec = spec;
        TotalRevitElements = totalRevitElements;
        MatchedRows = matched;
        UnmatchedRows = unmatched;
        SheetsToCreate = Array.Empty<(int, string, string)>();
        SheetsSkipped = Array.Empty<(int, string)>();
    }

    // Create-sheets variant
    public ImportDryRunResult(
        ImportSpec spec,
        IReadOnlyList<(int RowNum, string Number, string Name)> toCreate,
        IReadOnlyList<(int RowNum, string Number)> skipped)
    {
        Spec = spec;
        SheetsToCreate = toCreate;
        SheetsSkipped = skipped;
        MatchedRows = Array.Empty<RowMatch>();
        UnmatchedRows = Array.Empty<(int, string)>();
    }

    // Error variant
    public ImportDryRunResult(string error, ImportSpec spec)
    {
        Error = error;
        Spec = spec;
        MatchedRows = Array.Empty<RowMatch>();
        UnmatchedRows = Array.Empty<(int, string)>();
        SheetsToCreate = Array.Empty<(int, string, string)>();
        SheetsSkipped = Array.Empty<(int, string)>();
    }

    public ImportSpec Spec { get; }
    public string? Error { get; }
    public int TotalRevitElements { get; }
    public IReadOnlyList<RowMatch> MatchedRows { get; }
    public IReadOnlyList<(int RowNum, string MatchVal)> UnmatchedRows { get; }
    public IReadOnlyList<(int RowNum, string Number, string Name)> SheetsToCreate { get; }
    public IReadOnlyList<(int RowNum, string Number)> SheetsSkipped { get; }
    public bool HasError => Error is not null;
}

public sealed record RowMatch(
    int RowNum,
    long ElementId,
    string MatchValue,
    IReadOnlyList<string> Row,
    IReadOnlyList<string> Columns);

public sealed record ImportCommitResult(int Applied, int Failed, string? Error);

/// <summary>State kept between DryRun and Commit while waiting for user confirm.</summary>
public sealed record ImportPending(ImportedTable Table, ImportSpec Spec, ImportDryRunResult DryRun);
