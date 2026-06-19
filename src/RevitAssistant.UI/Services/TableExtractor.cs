using System.Globalization;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Turns a tool result into a <see cref="ResultTable"/> when its data contains a
/// list of records (query_where rows, count/aggregate groups, list_* arrays).
/// Generalised: picks the first suitable array-of-objects and unions the keys.
/// Returns null when there's nothing tabular to show.
/// </summary>
public static class TableExtractor
{
    private const int MaxRows = 200;

    // Preferred array properties, in order.
    private static readonly string[] Preferred =
        { "rows", "groups", "elements", "levels", "rooms", "sheets", "materials", "phases", "families" };

    private static readonly Dictionary<string, string> Header = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "ID", ["name"] = "Tên", ["value"] = "Hạng mục", ["count"] = "Số lượng",
        ["sum"] = "Tổng", ["avg"] = "Trung bình", ["min"] = "Nhỏ nhất", ["max"] = "Lớn nhất",
        ["number"] = "Số", ["level"] = "Tầng", ["category"] = "Danh mục", ["type"] = "Loại",
    };

    public static ResultTable? TryExtract(JsonObject envelope)
    {
        if (envelope["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b) return null;
        if (envelope["data"] is not JsonObject data) return null;

        var arr = PickArray(data);
        if (arr is null || arr.Count == 0) return null;

        // Column order: id, name first, then first-seen keys; skip noise.
        var cols = new List<string>();
        void Add(string k)
        {
            if (k.EndsWith("_scope", StringComparison.OrdinalIgnoreCase)) return;
            if (k.Equals("_note", StringComparison.OrdinalIgnoreCase)) return;
            if (!cols.Contains(k, StringComparer.OrdinalIgnoreCase)) cols.Add(k);
        }
        foreach (var pref in new[] { "id", "name" })
            if (arr[0] is JsonObject f0 && f0.ContainsKey(pref)) Add(pref);
        foreach (var item in arr)
            if (item is JsonObject o)
                foreach (var kv in o) Add(kv.Key);

        if (cols.Count == 0) return null;

        var rows = new List<IReadOnlyList<string>>();
        foreach (var item in arr)
        {
            if (rows.Count >= MaxRows) break;
            if (item is not JsonObject o) continue;
            var row = new List<string>(cols.Count);
            foreach (var c in cols) row.Add(Cell(o[c]));
            rows.Add(row);
        }

        var total = (data["count"] as JsonValue)?.TryGetValue<int>(out var cnt) == true ? cnt : arr.Count;
        var truncated = arr.Count > rows.Count
            || ((data["truncated"] as JsonValue)?.TryGetValue<bool>(out var t) == true && t)
            || ((data["listTruncated"] as JsonValue)?.TryGetValue<bool>(out var lt) == true && lt);

        var headers = cols.Select(c => Header.TryGetValue(c, out var h) ? h : c).ToList();
        return new ResultTable(headers, rows, total, truncated);
    }

    private static JsonArray? PickArray(JsonObject data)
    {
        foreach (var key in Preferred)
            if (data[key] is JsonArray a && HasObjects(a)) return a;
        // fallback: first array-of-objects
        foreach (var kv in data)
            if (kv.Value is JsonArray a && HasObjects(a)) return a;
        return null;
    }

    private static bool HasObjects(JsonArray a)
    {
        foreach (var x in a) if (x is JsonObject) return true;
        return false;
    }

    private static string Cell(JsonNode? n)
    {
        switch (n)
        {
            case null: return "";
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<bool>(out var bo)) return bo ? "✓" : "✗";
                if (v.TryGetValue<double>(out var d))
                    return d == Math.Floor(d) && Math.Abs(d) < 1e15
                        ? ((long)d).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("0.###", CultureInfo.InvariantCulture);
                return v.ToString();
            case JsonObject o:
                // e.g. min/max {value,id,name} → show the value (+name).
                var val = o["value"];
                var name = (o["name"] as JsonValue)?.TryGetValue<string>(out var nm) == true ? nm : null;
                if (val is not null) return name is null ? Cell(val) : $"{Cell(val)} ({name})";
                return o.ToJsonString();
            default: return n.ToString();
        }
    }
}
