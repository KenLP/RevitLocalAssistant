using System.Text;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Builds a compact, real grounding snippet from the active document:
/// the actual level names, the categories that really have elements, the current
/// active view (including its ID for view_id filtering), and a sample of real
/// parameter names per top category so the model uses EXACT names that exist in
/// THIS project instead of guessing.
/// </summary>
public static class ModelSchema
{
    private const int MaxCategories = 30;
    private const int MaxParamsPerCategory = 25;

    public static string? Build(
        JsonObject? levelsEnv,
        JsonObject? categoriesEnv,
        JsonObject? activeViewEnv = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? paramsByCategory = null)
    {
        var sb = new StringBuilder();

        // Active view (inject early so model can reference view_id without a tool call)
        var view = activeViewEnv?["data"] as JsonObject;
        if (view != null)
        {
            var viewName = view["name"]?.GetValue<string>();
            var viewType = view["viewType"]?.GetValue<string>();
            var levelName = view["levelName"]?.GetValue<string>();
            long? viewId = null;
            if (view["id"] is JsonValue vid) { try { viewId = vid.GetValue<long>(); } catch { } }

            sb.AppendLine("## View đang mở — dùng view_id để lọc chỉ phần tử trong view này:");
            sb.Append($"  - Tên: {viewName}");
            if (!string.IsNullOrWhiteSpace(viewType)) sb.Append($"  |  Loại: {viewType}");
            if (!string.IsNullOrWhiteSpace(levelName)) sb.Append($"  |  Tầng: {levelName}");
            if (viewId.HasValue) sb.Append($"  |  view_id: {viewId.Value}");
            sb.AppendLine();
            sb.AppendLine("  Khi user nói 'trong view này' / 'đang hiển thị': thêm view_id vào query_where / count_elements / aggregate_elements.");
            sb.AppendLine();
        }

        // Exact level names
        var levels = ExtractNames(levelsEnv, "levels");
        if (levels.Count > 0)
        {
            sb.AppendLine("## Tầng (Level) THẬT trong dự án — dùng CHÍNH XÁC các tên này:");
            foreach (var name in levels)
                sb.AppendLine($"  - {name}");
            sb.AppendLine();
        }

        // Categories with counts
        var cats = ExtractCategories(categoriesEnv);
        if (cats.Count > 0)
        {
            sb.AppendLine("## Danh mục có trong model (BuiltInCategory — tên — số lượng):");
            foreach (var (bic, name, count) in cats.Take(MaxCategories))
                sb.AppendLine($"  - {bic} — {name} — {count}");
            sb.AppendLine();
        }

        // Per-category parameter names (sampled from real elements)
        if (paramsByCategory is { Count: > 0 })
        {
            sb.AppendLine("## Tham số THỰC TẾ trong model — dùng ĐÚNG tên (có dấu cách, đúng hoa/thường):");
            foreach (var (bic, names) in paramsByCategory)
            {
                var catLabel = cats.FirstOrDefault(c => c.Bic == bic);
                var label = !string.IsNullOrEmpty(catLabel.Name) ? $"{bic} ({catLabel.Name})" : bic;
                sb.AppendLine($"  {label}:");
                foreach (var n in names.Take(MaxParamsPerCategory))
                    sb.AppendLine($"    - {n}");
            }
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
