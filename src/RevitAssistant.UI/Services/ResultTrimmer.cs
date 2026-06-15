using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Caps large arrays in a dispatcher result before it is fed back to the LLM.
/// A full list_rooms / find_elements dump can be hundreds of items and blow past
/// the model's context window, truncating the system prompt. We keep the head of
/// each array (the model rarely needs every row to answer) and annotate how many
/// were dropped so it can still reason about totals.
/// </summary>
public static class ResultTrimmer
{
    private const int MaxArrayItems = 40;

    /// <summary>Returns a trimmed deep copy; the original is left untouched.</summary>
    public static JsonObject Trim(JsonObject envelope, int maxItems = MaxArrayItems)
    {
        return (JsonObject)TrimNode(envelope.DeepClone(), maxItems);
    }

    private static JsonNode TrimNode(JsonNode node, int maxItems)
    {
        switch (node)
        {
            case JsonArray arr when arr.Count > maxItems:
            {
                var kept = new JsonArray();
                for (var i = 0; i < maxItems; i++)
                {
                    var item = arr[i];
                    arr[i] = null;                 // detach before re-parenting
                    kept.Add(item is null ? null : TrimNode(item, maxItems));
                }
                kept.Add(new JsonObject
                {
                    ["_note"] = $"(đã rút gọn: hiển thị {maxItems}/{arr.Count} mục)",
                });
                return kept;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (item is null) continue;
                    arr[i] = null;
                    arr[i] = TrimNode(item, maxItems);
                }
                return arr;
            }
            case JsonObject obj:
            {
                foreach (var key in new List<string>(obj.Select(kv => kv.Key)))
                {
                    var child = obj[key];
                    if (child is null) continue;
                    obj[key] = null;
                    obj[key] = TrimNode(child, maxItems);
                }
                return obj;
            }
            default:
                return node;
        }
    }
}
