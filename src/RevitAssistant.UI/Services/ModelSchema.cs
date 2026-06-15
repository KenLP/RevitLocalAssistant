using System.Text;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Builds a compact, real grounding snippet from the active document:
/// the actual level names and the categories that really have elements (with
/// counts). Injected into the system prompt so the model uses EXACT names that
/// exist in THIS project instead of guessing (e.g. "L5" when the level is
/// actually "L1 - Block 35").
/// </summary>
public static class ModelSchema
{
    private const int MaxCategories = 30;

    public static string? Build(JsonObject? levelsEnv, JsonObject? categoriesEnv)
    {
        var sb = new StringBuilder();

        var levels = ExtractNames(levelsEnv, "levels");
        if (levels.Count > 0)
        {
            sb.AppendLine("## Tầng (Level) THẬT trong dự án — dùng CHÍNH XÁC các tên này:");
            foreach (var name in levels)
                sb.AppendLine($"  - {name}");
            sb.AppendLine();
        }

        var cats = ExtractCategories(categoriesEnv);
        if (cats.Count > 0)
        {
            sb.AppendLine("## Danh mục có trong model (BuiltInCategory — tên — số lượng):");
            foreach (var (bic, name, count) in cats.Take(MaxCategories))
                sb.AppendLine($"  - {bic} — {name} — {count}");
            sb.AppendLine();
        }

        var text = sb.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
    }

    private static List<string> ExtractNames(JsonObject? env, string arrayKey)
    {
        var result = new List<string>();
        if (env?["data"]?[arrayKey] is not JsonArray arr) return result;
        foreach (var item in arr)
        {
            var name = item?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
        }
        return result;
    }

    private static List<(string Bic, string Name, int Count)> ExtractCategories(JsonObject? env)
    {
        var result = new List<(string, string, int)>();
        if (env?["data"]?["categories"] is not JsonArray arr) return result;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var bic = o["builtInCategory"]?.GetValue<string>();
            var name = o["name"]?.GetValue<string>() ?? "";
            var count = TryInt(o["instanceCount"]);
            if (!string.IsNullOrWhiteSpace(bic))
                result.Add((bic!, name, count));
        }
        return result;
    }

    private static int TryInt(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<int>(); } catch { }
        try { return (int)n.GetValue<long>(); } catch { }
        return 0;
    }
}
