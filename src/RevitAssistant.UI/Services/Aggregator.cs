using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Deterministic counting / group-by over a find_elements result. This is the
/// "smart query" core: a 7B model is unreliable at counting or grouping rows in
/// its head, so we do it in C# and hand back a tiny, exact summary the model just
/// reports. Pure — unit-testable with synthetic envelopes.
/// </summary>
public static class Aggregator
{
    /// <summary>
    /// Turn a find_elements envelope into { total } or, when grouped,
    /// { total, groupBy, groups:[{value,count}] } (sorted by count desc).
    /// Errors are passed straight through.
    /// </summary>
    public static JsonObject Summarize(JsonObject findEnvelope, string? groupBy)
    {
        if (!(findEnvelope["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var okVal) && okVal))
            return findEnvelope;

        var data = findEnvelope["data"] as JsonObject ?? new JsonObject();
        var elements = data["elements"] as JsonArray ?? new JsonArray();
        var total = TryInt(data["count"]) ?? elements.Count;
        var truncated = data["truncated"] is JsonValue tv && tv.TryGetValue<bool>(out var t) && t;

        if (string.IsNullOrWhiteSpace(groupBy))
            return Ok(new JsonObject { ["total"] = total, ["truncated"] = truncated });

        var counts = new Dictionary<string, int>();
        foreach (var el in elements)
        {
            var fields = el?["fields"] as JsonObject;
            var key = Disp(fields, groupBy + "_display")
                      ?? Disp(fields, groupBy!)
                      ?? AsStr(el?[groupBy!])
                      ?? "(trống)";
            if (string.IsNullOrEmpty(key)) key = "(trống)";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var groups = new JsonArray();
        foreach (var kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
            groups.Add(new JsonObject { ["value"] = kv.Key, ["count"] = kv.Value });

        return Ok(new JsonObject
        {
            ["total"] = total,
            ["groupBy"] = groupBy,
            ["truncated"] = truncated,
            ["groups"] = groups,
        });
    }

    private static JsonObject Ok(JsonNode data) => new() { ["ok"] = true, ["data"] = data };

    private static string? Disp(JsonObject? fields, string key) => AsStr(fields?[key]);

    private static string? AsStr(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n.ToString();
    }

    private static int? TryInt(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<int>(); } catch { }
        try { return (int)n.GetValue<long>(); } catch { }
        return null;
    }
}
