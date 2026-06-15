using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Deterministic counting and numeric aggregation over a find_elements result.
/// A 7B model is unreliable at counting/summing/min-maxing rows in its head
/// (it listed 7 rooms but said 10, and couldn't total floor volumes), so we do
/// it in C# and hand back a tiny, exact summary the model just reports.
/// Pure — unit-testable with synthetic envelopes.
/// </summary>
public static class Aggregator
{
    // Revit internal units → metric. Internal: length=ft, area=ft², volume=ft³.
    public const double FeetToMeters = 0.3048;
    public const double Ft2ToM2 = FeetToMeters * FeetToMeters;            // 0.09290304
    public const double Ft3ToM3 = FeetToMeters * FeetToMeters * FeetToMeters; // 0.0283168…

    // Keep returned arrays at/below ResultTrimmer's cap so it never injects a
    // heterogeneous "_note" object into our typed stat rows.
    private const int MaxItems = 40;

    private const string TruncatedNote =
        "⚠️ Danh mục có hơn 5000 phần tử; số liệu chỉ tính trên 5000 phần tử đầu — " +
        "CHƯA đầy đủ. Hãy nói rõ điều này với người dùng.";

    // ── Counting (count_elements) ────────────────────────────────────────────

    /// <summary>
    /// { total } or, when grouped, { total, groupBy, groups:[{value,count}] }.
    /// Errors are passed straight through.
    /// </summary>
    public static JsonObject Summarize(JsonObject findEnvelope, string? groupBy)
    {
        if (!IsOk(findEnvelope)) return findEnvelope;

        var data = findEnvelope["data"] as JsonObject ?? new JsonObject();
        var elements = data["elements"] as JsonArray ?? new JsonArray();
        var total = TryInt(data["count"]) ?? elements.Count;
        var truncated = IsTruncated(data);

        if (string.IsNullOrWhiteSpace(groupBy))
        {
            var d = new JsonObject { ["total"] = total, ["truncated"] = truncated };
            if (truncated) d["note"] = TruncatedNote;
            return Ok(d);
        }

        var counts = new Dictionary<string, int>();
        foreach (var el in elements)
        {
            var key = GroupKey(el, groupBy!);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var ordered = counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
        var groups = new JsonArray();
        foreach (var kv in ordered.Take(MaxItems))
            groups.Add(new JsonObject { ["value"] = kv.Key, ["count"] = kv.Value });

        var countResult = new JsonObject
        {
            ["total"] = total,
            ["groupBy"] = groupBy,
            ["truncated"] = truncated,
            ["groups"] = groups,
        };
        if (ordered.Count > MaxItems) countResult["groupsTruncated"] = true;
        if (truncated) countResult["note"] = TruncatedNote;
        return Ok(countResult);
    }

    // ── Numeric aggregation (aggregate_elements) ─────────────────────────────

    /// <summary>
    /// sum / min / max / avg of a numeric parameter, with values scaled by
    /// <paramref name="factor"/> (e.g. ft²→m²). Optionally returns the top-N
    /// elements by value and a per-group breakdown.
    /// </summary>
    public static JsonObject SummarizeNumeric(
        JsonObject findEnvelope,
        string parameter,
        double factor,
        string unitLabel,
        int top = 0,
        string? groupBy = null)
    {
        if (!IsOk(findEnvelope)) return findEnvelope;

        var data = findEnvelope["data"] as JsonObject ?? new JsonObject();
        var elements = data["elements"] as JsonArray ?? new JsonArray();
        var truncated = IsTruncated(data);

        var overall = new Stats();
        var items = new List<(double Val, long Id, string Name)>();
        var groups = new Dictionary<string, Stats>();

        foreach (var el in elements)
        {
            var raw = TryDouble(el?["fields"]?[parameter]);
            if (raw is null) continue;

            var val = raw.Value * factor;
            var id = TryLong(el?["id"]) ?? 0;
            var name = AsStr(el?["name"]) ?? "";

            overall.Add(val, id, name);
            items.Add((val, id, name));

            if (!string.IsNullOrWhiteSpace(groupBy))
            {
                var key = GroupKey(el, groupBy!);
                if (!groups.TryGetValue(key, out var gs)) groups[key] = gs = new Stats();
                gs.Add(val, id, name);
            }
        }

        var result = new JsonObject
        {
            ["parameter"] = parameter,
            ["unit"] = unitLabel,
            ["count"] = overall.Count,
            ["sum"] = R(overall.Sum),
            ["avg"] = overall.Count > 0 ? R(overall.Sum / overall.Count) : 0,
            ["min"] = overall.MinNode(),
            ["max"] = overall.MaxNode(),
            ["truncated"] = truncated,
        };

        if (truncated) result["note"] = TruncatedNote;

        if (top > 0 && items.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var it in items.OrderByDescending(i => i.Val).Take(Math.Min(top, MaxItems)))
                arr.Add(Elem(it.Val, it.Id, it.Name));
            result["top"] = arr;
        }

        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            var ordered = groups.OrderByDescending(k => k.Value.Sum).ToList();
            var garr = new JsonArray();
            foreach (var kv in ordered.Take(MaxItems))
                garr.Add(new JsonObject
                {
                    ["value"] = kv.Key,
                    ["count"] = kv.Value.Count,
                    ["sum"] = R(kv.Value.Sum),
                    ["avg"] = kv.Value.Count > 0 ? R(kv.Value.Sum / kv.Value.Count) : 0,
                    ["min"] = kv.Value.MinNode(),
                    ["max"] = kv.Value.MaxNode(),
                });
            result["groupBy"] = groupBy;
            result["groups"] = garr;
            if (ordered.Count > MaxItems) result["groupsTruncated"] = true;
        }

        return Ok(result);
    }

    private enum Dim { Area, Volume, Length, Unitless }

    private static Dim InferDim(string parameter)
    {
        var p = parameter.ToLowerInvariant();
        if (p.Contains("volume")) return Dim.Volume;
        if (p.Contains("area")) return Dim.Area;
        if (p.Contains("length") || p.Contains("height") || p.Contains("width") ||
            p.Contains("perimeter") || p.Contains("elevation") || p.Contains("offset") ||
            p.Contains("thickness") || p.Contains("depth") || p.Contains("radius"))
            return Dim.Length;
        return Dim.Unitless;
    }

    /// <summary>
    /// Pick (factor, label) for a unit. The parameter's true dimension (from its
    /// name) is authoritative: an explicit unit is honoured ONLY when it matches
    /// that dimension — so a model that swaps m2/m3 (exactly what this deterministic
    /// tool defends against) can't produce a confidently-mislabelled number. An
    /// unknown unit falls back to the dimension default rather than echoing a bad
    /// label over a raw value.
    /// </summary>
    public static (double Factor, string Label) ResolveUnit(string? unit, string parameter)
    {
        var dim = InferDim(parameter);
        var u = unit?.Trim().ToLowerInvariant();

        // Explicit "internal/feet/raw" → keep raw values, label by dimension so the
        // number is never unit-ambiguous.
        if (u is "internal" or "feet" or "raw")
            return dim switch
            {
                Dim.Area => (1.0, "ft²"),
                Dim.Volume => (1.0, "ft³"),
                Dim.Length => (1.0, "ft"),
                _ => (1.0, ""),
            };

        // Explicit metric unit → honour only if it matches the parameter dimension.
        if (!string.IsNullOrEmpty(u))
        {
            var (uf, ul, ud) = u switch
            {
                "m2" or "sqm" => (Ft2ToM2, "m²", Dim.Area),
                "m3" or "cbm" => (Ft3ToM3, "m³", Dim.Volume),
                "meters" or "m" => (FeetToMeters, "m", Dim.Length),
                _ => (double.NaN, "", Dim.Unitless),       // unknown
            };
            if (!double.IsNaN(uf) && ud == dim)
                return (uf, ul);
            // unknown or mismatched → fall through to the dimension default
        }

        // Inferred default: metric by dimension; unitless params stay raw.
        return dim switch
        {
            Dim.Area => (Ft2ToM2, "m²"),
            Dim.Volume => (Ft3ToM3, "m³"),
            Dim.Length => (FeetToMeters, "m"),
            _ => (1.0, ""),
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class Stats
    {
        public int Count;
        public double Sum;
        private bool _has;
        private double _min, _max;
        private long _minId, _maxId;
        private string _minName = "", _maxName = "";

        public void Add(double v, long id, string name)
        {
            Count++; Sum += v;
            if (!_has || v < _min) { _min = v; _minId = id; _minName = name; }
            if (!_has || v > _max) { _max = v; _maxId = id; _maxName = name; }
            _has = true;
        }

        public JsonNode? MinNode() => _has ? Elem(_min, _minId, _minName) : null;
        public JsonNode? MaxNode() => _has ? Elem(_max, _maxId, _maxName) : null;
    }

    private static JsonObject Elem(double val, long id, string name) =>
        new() { ["value"] = R(val), ["id"] = id, ["name"] = name };

    private static double R(double x) => Math.Round(x, 3, MidpointRounding.AwayFromZero);

    private static string GroupKey(JsonNode? el, string groupBy)
    {
        var fields = el?["fields"] as JsonObject;
        var key = AsStr(fields?[groupBy + "_display"])
                  ?? AsStr(fields?[groupBy])
                  ?? AsStr(el?[groupBy])
                  ?? "(trống)";
        return string.IsNullOrEmpty(key) ? "(trống)" : key;
    }

    private static bool IsOk(JsonObject env) =>
        env["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsTruncated(JsonObject data) =>
        data["truncated"] is JsonValue v && v.TryGetValue<bool>(out var t) && t;

    private static JsonObject Ok(JsonNode data) => new() { ["ok"] = true, ["data"] = data };

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

    private static long? TryLong(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        return null;
    }

    private static double? TryDouble(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        // string number fallback (some params project as text)
        if (n is JsonValue v && v.TryGetValue<string>(out var s) &&
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
