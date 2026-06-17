using System.Text.Json.Nodes;
using RevitAssistant.Llm;

namespace RevitAssistant.UI;

/// <summary>
/// Turns a write tool-call + its dry-run dispatcher result into a human-readable
/// <see cref="ChangePreview"/>. Pure / deterministic — no Revit or network access,
/// so it is fully unit-testable with synthetic JSON.
/// </summary>
public static class PreviewBuilder
{
    private const int MaxRows = 20;

    public static ChangePreview Build(ToolCall write, JsonObject dryRunEnvelope)
    {
        var args = SafeParse(write.ArgumentsJson);
        var data = dryRunEnvelope["data"] as JsonObject ?? new JsonObject();

        return write.FunctionName switch
        {
            "update_where"        => BuildUpdateWhere(args, data),
            "set_parameter_batch" => BuildBatch(args, data),
            "set_parameter"       => BuildSingle(args),
            "rename_element"      => BuildRename(args),
            _                     => BuildGeneric(write, data),
        };
    }

    // ── update_where (deterministic + read-back verify) ──────────────────────

    private static ChangePreview BuildUpdateWhere(JsonObject args, JsonObject data)
    {
        var setObj = args["set"] as JsonObject ?? new JsonObject();
        var paramName = Str(setObj, "parameter") ?? Str(setObj, "parameterName") ?? "(tham số)";
        var value = ValueText(setObj["value"]);

        var matched = IntOr(data, "matchedCount", 0);
        var applied = IntOr(data, "applied", 0);
        var failed = IntOr(data, "failed", 0);
        var affected = IntOr(data, "affectedInstances", matched);
        var scope = Str(data, "scope") ?? "instance";

        var rows = new List<PreviewRow>();
        if (data["results"] is JsonArray results)
        {
            foreach (var r in results)
            {
                if (rows.Count >= MaxRows) break;
                if (r is not JsonObject ro) continue;
                var id = LongOrNull(ro, "id");
                var ok = ro["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                var name = Str(ro, "name");
                var detail = ok
                    ? $"{paramName} → {value}"
                    : $"✗ {Str(ro, "reason") ?? "lỗi"}";
                rows.Add(new PreviewRow($"ID {id?.ToString() ?? "?"}{(name is null ? "" : $" · {name}")}",
                                        detail, IsFailure: !ok));
            }
        }

        var summary = $"Đặt '{paramName}' = '{value}' cho {matched} phần tử khớp."
                    + $" Dry-run: {applied} OK" + (failed > 0 ? $", {failed} lỗi." : ".");
        if (scope == "type" && affected > matched)
            summary += $" ⚠️ '{paramName}' là tham số LOẠI — sẽ ảnh hưởng {affected} phần tử "
                     + $"(thêm {affected - matched} ngoài {matched} khớp).";

        return new ChangePreview("Sửa tham số (theo điều kiện)", summary, rows, matched, failed);
    }

    // ── set_parameter_batch ──────────────────────────────────────────────────

    private static ChangePreview BuildBatch(JsonObject args, JsonObject data)
    {
        var paramName = Str(args, "parameterName") ?? "(tham số)";
        var value = ValueText(args["value"]);
        var ids = args["ids"] as JsonArray ?? new JsonArray();

        var total = IntOr(data, "total", ids.Count);
        var succeeded = IntOr(data, "succeeded", total);
        var failed = IntOr(data, "failed", 0);

        // Map failed ids → error message for highlighting.
        var failedIds = new Dictionary<long, string>();
        if (data["errors"] is JsonArray errs)
        {
            foreach (var e in errs)
            {
                if (e is not JsonObject eo) continue;
                var id = LongOrNull(eo, "id");
                if (id is null) continue;
                failedIds[id.Value] = Str(eo, "error") ?? Str(eo, "code") ?? "lỗi";
            }
        }

        var rows = new List<PreviewRow>();
        foreach (var idNode in ids)
        {
            if (rows.Count >= MaxRows) break;
            if (idNode is null) continue;
            var id = idNode.GetValue<long>();
            if (failedIds.TryGetValue(id, out var err))
                rows.Add(new PreviewRow($"ID {id}", $"✗ {err}", IsFailure: true));
            else
                rows.Add(new PreviewRow($"ID {id}", $"{paramName} → {value}"));
        }

        var summary = $"Đặt '{paramName}' = '{value}' cho {total} phần tử."
                    + $" Dry-run: {succeeded} OK"
                    + (failed > 0 ? $", {failed} lỗi." : ".");

        return new ChangePreview("Sửa tham số hàng loạt", summary, rows, total, failed);
    }

    // ── set_parameter ────────────────────────────────────────────────────────

    private static ChangePreview BuildSingle(JsonObject args)
    {
        var id = LongOrNull(args, "id");
        var paramName = Str(args, "parameterName") ?? "(tham số)";
        var value = ValueText(args["value"]);

        var rows = new List<PreviewRow>
        {
            new($"ID {id?.ToString() ?? "?"}", $"{paramName} → {value}"),
        };
        return new ChangePreview(
            "Sửa tham số",
            $"Đặt '{paramName}' = '{value}' cho 1 phần tử.",
            rows, 1);
    }

    // ── rename_element ───────────────────────────────────────────────────────

    private static ChangePreview BuildRename(JsonObject args)
    {
        var id = LongOrNull(args, "id");
        var newName = Str(args, "newName") ?? "(tên mới)";

        var rows = new List<PreviewRow>
        {
            new($"ID {id?.ToString() ?? "?"}", $"Tên → {newName}"),
        };
        return new ChangePreview(
            "Đổi tên",
            $"Đổi tên phần tử thành '{newName}'.",
            rows, 1);
    }

    // ── fallback ─────────────────────────────────────────────────────────────

    private static ChangePreview BuildGeneric(ToolCall write, JsonObject data)
    {
        var summary = Str(data, "changeSummary") ?? $"Thực hiện '{write.FunctionName}'.";
        return new ChangePreview(write.FunctionName, summary, [], 1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonObject SafeParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int IntOr(JsonObject o, string key, int fallback)
        => (int?)LongOrNull(o, key) ?? fallback;

    // Tolerates int- vs long- vs double-backed JsonValue (in-memory vs parsed JSON).
    private static long? LongOrNull(JsonObject o, string key)
    {
        var n = o[key];
        if (n is null) return null;
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        try { return (long)n.GetValue<double>(); } catch { }
        return null;
    }

    private static string ValueText(JsonNode? node)
    {
        if (node is null) return "(rỗng)";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "Yes" : "No";
            if (v.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return node.ToJsonString();
    }
}
