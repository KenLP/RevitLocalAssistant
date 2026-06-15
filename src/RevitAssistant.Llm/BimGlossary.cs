namespace RevitAssistant.Llm;

/// <summary>
/// Vietnamese ↔ Revit BIM terminology lookup.
/// Compiled from docs/VI_BIM_GLOSSARY.md.
///
/// Used by IntentParser to inject a context-relevant glossary excerpt into the
/// system prompt, so the LLM maps VI terms to exact Revit names rather than guessing.
/// </summary>
public static class BimGlossary
{
    // ── Category mappings ────────────────────────────────────────────────────

    public static readonly IReadOnlyDictionary<string, string> CategoryByVi =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Structural / architectural elements
            ["tường"]              = "OST_Walls",
            ["tuong"]              = "OST_Walls",
            ["tường cứu hỏa"]      = "OST_Walls",
            ["tường ngăn cháy"]    = "OST_Walls",
            ["sàn"]                = "OST_Floors",
            ["san"]                = "OST_Floors",
            ["trần"]               = "OST_Ceilings",
            ["tran"]               = "OST_Ceilings",
            ["mái"]                = "OST_Roofs",
            ["mai"]                = "OST_Roofs",
            ["cột"]                = "OST_Columns",
            ["cot"]                = "OST_Columns",
            ["cột kết cấu"]        = "OST_StructuralColumns",
            ["cot ket cau"]        = "OST_StructuralColumns",
            ["dầm"]                = "OST_StructuralFraming",
            ["dam"]                = "OST_StructuralFraming",
            ["xà"]                 = "OST_StructuralFraming",
            ["xa"]                 = "OST_StructuralFraming",
            ["dầm sàn"]            = "OST_StructuralFraming",
            // Openings
            ["cửa"]                = "OST_Doors",
            ["cua"]                = "OST_Doors",
            ["cửa đi"]             = "OST_Doors",
            ["cua di"]             = "OST_Doors",
            ["cửa thoát hiểm"]     = "OST_Doors",
            ["cửa cứu hỏa"]        = "OST_Doors",
            ["cửa sổ"]             = "OST_Windows",
            ["cua so"]             = "OST_Windows",
            // Circulation
            ["thang bộ"]           = "OST_Stairs",
            ["cầu thang"]          = "OST_Stairs",
            ["thang"]              = "OST_Stairs",
            ["lan can"]            = "OST_Railings",
            // Spaces
            ["phòng"]              = "OST_Rooms",
            ["phong"]              = "OST_Rooms",
            ["không gian"]         = "OST_MEPSpaces",
            // Reference
            ["tầng"]               = "OST_Levels",
            ["tang"]               = "OST_Levels",
            ["cao độ"]             = "OST_Levels",
            ["lưới trục"]          = "OST_Grids",
            ["trục"]               = "OST_Grids",
            // MEP
            ["ống nước"]           = "OST_PipeCurves",
            ["ong nuoc"]           = "OST_PipeCurves",
            ["ống gió"]            = "OST_DuctCurves",
            ["ong gio"]            = "OST_DuctCurves",
            ["thiết bị cơ"]        = "OST_MechanicalEquipment",
            ["thiết bị điện"]      = "OST_ElectricalEquipment",
            ["đèn"]                = "OST_LightingFixtures",
            // Documentation
            ["vật liệu"]           = "OST_Materials",
            ["sheet"]              = "OST_Sheets",
            ["bản vẽ"]             = "OST_Sheets",
        };

    // ── Parameter name mappings ──────────────────────────────────────────────

    public static readonly IReadOnlyDictionary<string, string> ParameterByVi =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mã hiệu"]            = "Mark",
            ["ma hieu"]            = "Mark",
            ["tên"]                = "Name",
            ["ten"]                = "Name",
            ["chú thích"]          = "Comments",
            ["ghi chú"]            = "Comments",
            ["bình luận"]          = "Comments",
            ["cấp chống cháy"]     = "Fire Rating",
            ["cap chong chay"]     = "Fire Rating",
            ["chống cháy"]         = "Fire Rating",
            ["bề dày"]             = "Width",
            ["be day"]             = "Width",
            ["độ dày"]             = "Width",
            ["chiều cao"]          = "Height",
            ["chieu cao"]          = "Height",
            ["chiều cao thông thủy"] = "Clear Height",
            ["chiều dài"]          = "Length",
            ["chieu dai"]          = "Length",
            ["diện tích"]          = "Area",
            ["dien tich"]          = "Area",
            ["thể tích"]           = "Volume",
            ["the tich"]           = "Volume",
            ["chu vi"]             = "Perimeter",
            ["cao độ đáy"]         = "Base Offset",
            ["cao độ đỉnh"]        = "Top Offset",
            ["tầng"]               = "Level",
            ["vật liệu kết cấu"]   = "Structural Material",
            ["phân khu"]           = "Department",
            ["phan khu"]           = "Department",
            ["bộ phận"]            = "Department",
            ["số phòng"]           = "Number",
            ["so phong"]           = "Number",
            ["mô tả"]              = "Description",
            ["nhà sản xuất"]       = "Manufacturer",
            ["model"]              = "Model",
            ["giai đoạn thi công"] = "Phase Created",
            ["giai đoạn phá dỡ"]   = "Phase Demolished",
        };

    // ── Public lookup helpers ────────────────────────────────────────────────

    /// <summary>
    /// Try to resolve a Vietnamese term to a BuiltInCategory string.
    /// Returns null if not found.
    /// </summary>
    public static string? TryGetCategory(string viTerm) =>
        CategoryByVi.TryGetValue(viTerm.Trim(), out var cat) ? cat : null;

    /// <summary>
    /// Try to resolve a Vietnamese term to a Revit parameter name.
    /// Returns null if not found.
    /// </summary>
    public static string? TryGetParameter(string viTerm) =>
        ParameterByVi.TryGetValue(viTerm.Trim(), out var param) ? param : null;

    /// <summary>
    /// Build a compact glossary snippet for injection into the LLM system prompt.
    /// Keeps it under ~500 tokens.
    /// </summary>
    public static string BuildPromptSnippet()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## VI → Revit mapping (use EXACT names)");
        sb.AppendLine("**Categories:**");

        // Deduplicate to unique targets
        var catGroups = CategoryByVi
            .GroupBy(kv => kv.Value)
            .Select(g => (Target: g.Key, Terms: string.Join(", ", g.Select(kv => kv.Key).Take(3))));

        foreach (var (target, terms) in catGroups)
            sb.AppendLine($"  {terms} → {target}");

        sb.AppendLine("**Parameters:**");
        var paramGroups = ParameterByVi
            .GroupBy(kv => kv.Value)
            .Select(g => (Target: g.Key, Terms: string.Join(", ", g.Select(kv => kv.Key).Take(2))));

        foreach (var (target, terms) in paramGroups)
            sb.AppendLine($"  {terms} → \"{target}\"");

        return sb.ToString();
    }
}
