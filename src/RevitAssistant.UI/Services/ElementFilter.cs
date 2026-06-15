using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAssistant.UI;

/// <summary>
/// Rich, deterministic, client-side element filtering. The Revit-side
/// find_elements only supports eq/neq/contains/gt/lt and compares against the
/// FORMATTED display string (so numeric gt/lt are unreliable and there is no
/// ends_with / regex / is_empty). We instead fetch the elements with the needed
/// fields projected, then filter here in C# over the RAW values — covering the
/// "complex query" cases (Mark ends with X, Fire Rating matches a format,
/// doors without a value, etc.). Pure → unit-testable.
/// </summary>
public static class ElementFilter
{
    public sealed record Cond(string Param, string Op, JsonNode? Value);

    /// <summary>Parse a filters JSON array of {parameterName, operator, value}.</summary>
    public static IReadOnlyList<Cond> Parse(JsonArray? filters)
    {
        var list = new List<Cond>();
        if (filters is null) return list;
        foreach (var f in filters)
        {
            if (f is not JsonObject o) continue;
            var param = AsStr(o["parameterName"]) ?? AsStr(o["param"]);
            if (string.IsNullOrWhiteSpace(param)) continue;
            var op = (AsStr(o["operator"]) ?? "eq").Trim().ToLowerInvariant();
            list.Add(new Cond(param!, NormalizeOp(op), o["value"]));
        }
        return list;
    }

    /// <summary>The distinct parameter names referenced — to project as fields.</summary>
    public static IEnumerable<string> Params(IReadOnlyList<Cond> conds) =>
        conds.Select(c => c.Param).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Return the elements (from a find_elements data.elements array) that match ALL conds.</summary>
    public static List<JsonNode> Apply(JsonArray elements, IReadOnlyList<Cond> conds)
    {
        var result = new List<JsonNode>();
        foreach (var el in elements)
        {
            if (el is null) continue;
            if (conds.All(c => Match(el, c))) result.Add(el);
        }
        return result;
    }

    // ── matching ─────────────────────────────────────────────────────────────

    private static bool Match(JsonNode el, Cond c)
    {
        // Text matching uses the DISPLAY value ("60 MIN", "300 mm") — Core projects
        // numeric params as a raw number plus a formatted *_display string.
        var text = GetDisplayText(el, c.Param);   // null = parameter absent / no value
        var valStr = AsStr(c.Value) ?? "";

        // Operators with defined behaviour on a MISSING value are handled first.
        switch (c.Op)
        {
            case "is_empty": return string.IsNullOrWhiteSpace(text);
            case "not_empty": return !string.IsNullOrWhiteSpace(text);
            case "regex": return text is not null && SafeRegex(text, valStr);
            // "không đúng định dạng" — a missing value also fails the format.
            case "not_regex": return text is null || !SafeRegex(text, valStr);
        }

        // A missing value matches nothing else (use is_empty/not_empty for presence);
        // neq is symmetric with eq → also false when absent.
        if (text is null)
            return false;

        switch (c.Op)
        {
            case "eq":
            {
                var (a, b) = (GetNum(el, c.Param), TryDouble(c.Value));
                if (a is not null && b is not null) return Near(a.Value, b.Value);
                return string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
            }
            case "neq":
            {
                var (a, b) = (GetNum(el, c.Param), TryDouble(c.Value));
                if (a is not null && b is not null) return !Near(a.Value, b.Value);
                return !string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
            }
            case "contains":
                return text.Contains(valStr, StringComparison.OrdinalIgnoreCase);
            case "starts_with":
                return text.StartsWith(valStr, StringComparison.OrdinalIgnoreCase);
            case "ends_with":
                return text.EndsWith(valStr, StringComparison.OrdinalIgnoreCase);
            case "gt": case "lt": case "gte": case "lte":
            {
                var a = GetNum(el, c.Param);
                var b = TryDouble(c.Value);
                if (a is null || b is null) return false;
                return c.Op switch
                {
                    "gt" => a > b,
                    "lt" => a < b,
                    "gte" => a >= b,
                    "lte" => a <= b,
                    _ => false,
                };
            }
            default:
                // Unknown op → be permissive on contains-like, else exact.
                return string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeOp(string op) => op switch
    {
        "equals" => "eq",
        "not_equals" or "ne" => "neq",
        "greater" => "gt",
        "less" => "lt",
        "greater_equal" or "greater_than_or_equal" => "gte",
        "less_equal" or "less_than_or_equal" => "lte",
        "startswith" or "starts" => "starts_with",
        "endswith" or "ends" => "ends_with",
        "matches" or "regexp" => "regex",
        "not_match" or "notmatch" or "not_matches" or "notregex" => "not_regex",
        "empty" or "isempty" or "is_null" => "is_empty",
        "notempty" or "isnotempty" or "exists" or "has_value" => "not_empty",
        _ => op,
    };

    private static bool SafeRegex(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch { return false; }   // invalid pattern / timeout → no match
    }

    // ── value access ─────────────────────────────────────────────────────────

    /// <summary>
    /// Human/display value of a parameter, or null if absent. Prefers the
    /// formatted *_display string (e.g. "60 MIN", "300 mm") that Core projects
    /// alongside the raw scalar — so text operators match what the user sees.
    /// </summary>
    private static string? GetDisplayText(JsonNode el, string param)
    {
        var fields = el["fields"] as JsonObject;
        if (fields is not null)
        {
            var disp = fields[param + "_display"];
            if (disp is JsonValue dv) return dv.TryGetValue<string>(out var ds) ? ds : dv.ToString();
            var raw = fields[param];
            if (raw is JsonValue rv) return rv.TryGetValue<string>(out var s) ? s : rv.ToString();
        }
        // Built-ins surfaced at the top level by SummarizeElement.
        if (param.Equals("name", StringComparison.OrdinalIgnoreCase)) return AsStr(el["name"]);
        if (param.Equals("id", StringComparison.OrdinalIgnoreCase)) return AsStr(el["id"]);
        if (param.Equals("category", StringComparison.OrdinalIgnoreCase)) return AsStr(el["category"]);
        return null;
    }

    /// <summary>
    /// Numeric value for gt/lt/eq compares. Reads the RAW scalar node first
    /// (a numerically-stored param), then falls back to the leading number in the
    /// display text so a string-stored "60 MIN" → 60, "300 mm" → 300.
    /// </summary>
    private static double? GetNum(JsonNode el, string param)
    {
        var fields = el["fields"] as JsonObject;
        var n = TryDouble(fields?[param]);
        if (n is not null) return n;

        var text = GetDisplayText(el, param);
        if (text is not null)
        {
            var m = Regex.Match(text, @"[-+]?\d+(?:[.,]\d+)?");
            if (m.Success && double.TryParse(m.Value.Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static string? AsStr(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n.ToString();
    }

    private static double? TryDouble(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        if (n is JsonValue v && v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
