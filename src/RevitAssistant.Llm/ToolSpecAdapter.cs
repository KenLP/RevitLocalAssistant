using System.Text.Json.Nodes;

namespace RevitAssistant.Llm;

/// <summary>
/// Builds the curated v1 tool surface exposed to the local LLM.
///
/// Scope: read + parameter-edit only. No geometry create/delete, no view ops,
/// no group/ungroup, no delete elements — keeps the tool surface small so
/// Qwen2.5-7B stays accurate.
///
/// Tool names match the Revit command names in RevitMCP.Core (CommandRegistry)
/// so the Orchestrator can dispatch them directly via EnqueueAsync(toolName, args).
/// </summary>
public static class ToolSpecAdapter
{
    /// <summary>Returns the full curated tool list for the LLM context.</summary>
    public static IReadOnlyList<ToolDefinition> BuildToolSurface()
    {
        return
        [
            // ── Document info ───────────────────────────────────────────────
            new ToolDefinition(
                "get_document_info",
                "Get project info: title, file path, worksharing status, active view, project metadata. " +
                "Dùng để trả lời câu hỏi về dự án hiện tại.",
                Schema("""{"type":"object","properties":{}}""")),

            // ── Listing / discovery ─────────────────────────────────────────
            new ToolDefinition(
                "list_levels",
                "List all Levels in the model with elevation. " +
                "Dùng khi người dùng hỏi về danh sách tầng / cao độ.",
                Schema("""{"type":"object","properties":{}}""")),

            new ToolDefinition(
                "list_rooms",
                "List all Rooms: name, number, area, level, department. " +
                "Dùng để liệt kê phòng hoặc trả lời câu hỏi về phòng.",
                Schema("""{"type":"object","properties":{}}""")),

            new ToolDefinition(
                "list_categories",
                "List all Revit categories that have elements in this model (with element count). " +
                "Dùng để khám phá model hoặc khi không chắc tên category.",
                Schema("""{"type":"object","properties":{}}""")),

            new ToolDefinition(
                "list_families",
                "List loaded families, optionally filtered by category. " +
                "Dùng khi hỏi về họ cấu kiện (families).",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory to filter by, e.g. 'OST_Doors'."
                    }
                  }
                }
                """)),

            new ToolDefinition(
                "list_family_types",
                "List all types (type names) of a given family. " +
                "Dùng khi người dùng hỏi về loại cụ thể của một họ.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "familyName": {
                      "type": "string",
                      "description": "Exact family name."
                    }
                  },
                  "required": ["familyName"]
                }
                """)),

            new ToolDefinition(
                "list_materials",
                "List all Materials in the project. " +
                "Dùng khi hỏi về vật liệu.",
                Schema("""{"type":"object","properties":{}}""")),

            new ToolDefinition(
                "list_phases",
                "List all project phases. " +
                "Dùng khi hỏi về giai đoạn thi công / phá dỡ.",
                Schema("""{"type":"object","properties":{}}""")),

            new ToolDefinition(
                "list_sheets",
                "List all Sheets: sheet number, name, and placed views. " +
                "Dùng khi hỏi về bản vẽ / tờ in.",
                Schema("""{"type":"object","properties":{}}""")),

            // ── Element query / edit (deterministic, scope-aware) ───────────
            new ToolDefinition(
                "query_where",
                "PRIMARY tool to find / list / COUNT elements by conditions. Revit does the " +
                "matching (you never hand-build id lists or count by eye), resolving instance " +
                "vs TYPE scope automatically — so it can read TYPE params like 'Fire Rating' " +
                "that instance tools miss. Returns { count, rows:[{id,name,…select}] }. " +
                "Dùng cho 'bao nhiêu / how many', 'liệt kê / list', 'kiểm tra [phần tử] có [điều kiện]'.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": { "type": "string", "description": "BuiltInCategory (required), e.g. OST_Doors, OST_Walls, OST_Rooms, OST_Floors." },
                    "where": {
                      "type": "array",
                      "description": "AND conditions.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameter": { "type": "string", "description": "Exact Revit param name; may contain spaces, e.g. 'Fire Rating' (NOT 'FireRating')." },
                          "operator": { "type": "string", "enum": ["eq","neq","contains","starts_with","ends_with","regex","not_regex","gt","lt","gte","lte","is_empty","not_empty"] },
                          "value": { "description": "Value to compare (omit for is_empty/not_empty; .NET regex for regex/not_regex)." },
                          "scope": { "type": "string", "enum": ["auto","instance","type"], "description": "Where the param lives. Default auto. 'Fire Rating' is a TYPE param." }
                        },
                        "required": ["parameter"]
                      }
                    },
                    "select": { "type": "array", "items": { "type": "string" }, "description": "Param names to return per row." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 1000, "description": "Max rows returned; 'count' is always the true total." },
                    "view_id": { "type": "integer", "description": "Optional: restrict to elements visible in this view. Get the id from the schema section 'View đang mở' or call get_active_view." }
                  },
                  "required": ["category"]
                }
                """)),

            new ToolDefinition(
                "update_where",
                "PRIMARY tool to EDIT a parameter on every element matching conditions. " +
                "Deterministic with read-back VERIFY (re-reads each write; rolls back all if any " +
                "fails when atomic). Resolves instance vs TYPE scope and WARNS if a TYPE-param " +
                "edit hits extra instances. The user confirms before commit. NEVER hand-build id lists. " +
                "Dùng cho mọi chỉnh sửa: 'đặt/set [tham số]=[giá trị] cho [cấu kiện] có [điều kiện]'.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": { "type": "string", "description": "BuiltInCategory (required)." },
                    "where": {
                      "type": "array",
                      "description": "AND conditions selecting which elements to edit.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameter": { "type": "string" },
                          "operator": { "type": "string", "enum": ["eq","neq","contains","starts_with","ends_with","regex","not_regex","gt","lt","gte","lte","is_empty","not_empty"] },
                          "value": { },
                          "scope": { "type": "string", "enum": ["auto","instance","type"] }
                        },
                        "required": ["parameter"]
                      }
                    },
                    "set": {
                      "type": "object",
                      "properties": {
                        "parameter": { "type": "string", "description": "Param to write, e.g. 'Comments'." },
                        "value": { "description": "New value (string / number / true|false)." },
                        "units": { "type": "string", "enum": ["meters","feet","internal"], "description": "For numeric length/area/volume. Default internal." },
                        "scope": { "type": "string", "enum": ["auto","instance","type"] }
                      },
                      "required": ["parameter", "value"]
                    },
                    "atomic": { "type": "boolean", "description": "Roll back ALL if any element fails verify. Default true." }
                  },
                  "required": ["category", "set"]
                }
                """)),

            new ToolDefinition(
                "count_elements",
                "Count elements EXACTLY (Revit does the counting — you never miscount). " +
                "USE THIS for any 'bao nhiêu / how many' question. " +
                "Optionally group by a parameter to get a per-group breakdown: " +
                "'bao nhiêu phòng mỗi tầng' → category=OST_Rooms, groupBy='Level'. " +
                "Optionally filter: 'bao nhiêu cửa có Fire Rating < 60' → filters. " +
                "Returns { total, groups:[{value,count}] }. Report these numbers verbatim.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory name (required), e.g. OST_Rooms, OST_Doors, OST_Walls."
                    },
                    "groupBy": {
                      "type": "string",
                      "description": "Optional parameter name to break the count down by, e.g. 'Level', 'Fire Rating', 'Department'."
                    },
                    "order": {
                      "type": "string",
                      "enum": ["asc", "desc"],
                      "description": "When groupBy='Level': asc = low→high by elevation (default), desc = high→low."
                    },
                    "filters": {
                      "type": "array",
                      "description": "Optional filters (AND), instance-scope. For TYPE params (e.g. Fire Rating) use query_where instead.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameterName": { "type": "string" },
                          "operator": { "type": "string", "enum": ["eq","neq","contains","starts_with","ends_with","regex","not_regex","gt","lt","gte","lte","is_empty","not_empty"] },
                          "value": { }
                        },
                        "required": ["parameterName"]
                      }
                    },
                    "view_id": { "type": "integer", "description": "Optional: restrict to elements visible in this view." }
                  },
                  "required": ["category"]
                }
                """)),

            new ToolDefinition(
                "aggregate_elements",
                "Compute EXACT numeric stats of a parameter over a category (sum, min, max, avg) — " +
                "Revit does the math, you never add up rows by hand. " +
                "USE THIS for 'tổng/total', 'lớn nhất/largest', 'nhỏ nhất/smallest', 'trung bình/average'. " +
                "Examples: tổng m³ sàn → category=OST_Floors, parameter='Volume', unit='m3'; " +
                "sàn diện tích lớn nhất/nhỏ nhất → parameter='Area', unit='m2' (min & max are returned). " +
                "Floors and walls have a computed 'Volume' and 'Area' — totalling volume needs NO thickness. " +
                "Returns { count, sum, avg, min:{value,id,name}, max:{value,id,name} }. Report verbatim.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory (required), e.g. OST_Floors, OST_Walls, OST_Rooms."
                    },
                    "parameter": {
                      "type": "string",
                      "description": "Numeric parameter to aggregate (required), e.g. 'Area', 'Volume', 'Length'."
                    },
                    "unit": {
                      "type": "string",
                      "enum": ["m2", "m3", "meters", "internal"],
                      "description": "Output unit. Use m2 for Area, m3 for Volume, meters for lengths. If omitted it is inferred from the parameter name."
                    },
                    "groupBy": {
                      "type": "string",
                      "description": "Optional parameter to break stats down by, e.g. 'Level'."
                    },
                    "order": {
                      "type": "string",
                      "enum": ["asc", "desc"],
                      "description": "When groupBy='Level': asc = low→high by elevation (default), desc = high→low."
                    },
                    "top": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": 40,
                      "description": "Optional: also return the top-N (max 40) elements by value, largest first."
                    },
                    "filters": {
                      "type": "array",
                      "description": "Optional filters (AND), instance-scope. For TYPE params use query_where.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameterName": { "type": "string" },
                          "operator": { "type": "string", "enum": ["eq","neq","contains","starts_with","ends_with","regex","not_regex","gt","lt","gte","lte","is_empty","not_empty"] },
                          "value": { }
                        },
                        "required": ["parameterName"]
                      }
                    },
                    "view_id": { "type": "integer", "description": "Optional: restrict to elements visible in this view." }
                  },
                  "required": ["category", "parameter"]
                }
                """)),

            new ToolDefinition(
                "get_element_info",
                "Get ALL parameters of a single element by id. " +
                "Dùng khi cần xem chi tiết đầy đủ của một cấu kiện.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": "integer",
                      "description": "Revit ElementId (integer)."
                    }
                  },
                  "required": ["id"]
                }
                """)),

            new ToolDefinition(
                "get_parameter",
                "Get one specific parameter value from an element. " +
                "Dùng khi chỉ cần đọc một tham số của một phần tử.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "integer", "description": "ElementId." },
                    "parameterName": { "type": "string", "description": "Exact Revit parameter name." }
                  },
                  "required": ["id", "parameterName"]
                }
                """)),

            new ToolDefinition(
                "get_selected_elements",
                "Get currently selected elements in Revit (those the user has clicked on). " +
                "Dùng khi người dùng nói 'cái này', 'phần tử đang chọn', 'những cái đang bôi đen'.",
                Schema("""{"type":"object","properties":{}}""")),

            // ── Assistant-level tools ───────────────────────────────────────
            new ToolDefinition(
                "echo_interpretation",
                "CALL THIS FIRST before any Revit tool. " +
                "Confirms your understanding of the user's request in both languages. " +
                "The user sees this as a preview before any action is taken.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "vi": {
                      "type": "string",
                      "description": "Interpretation in Vietnamese: 'Hiểu là: [mô tả hành động]'"
                    },
                    "en": {
                      "type": "string",
                      "description": "Professional English: 'Understood: [action in Revit/BIM terms]'"
                    }
                  },
                  "required": ["vi", "en"]
                }
                """)),

            new ToolDefinition(
                "clarify",
                "Ask the user for clarification when the request is ambiguous. " +
                "Dùng khi không chắc người dùng muốn gì — hỏi lại bằng tiếng Việt. " +
                "Do NOT guess. Call clarify instead.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "question": {
                      "type": "string",
                      "description": "Câu hỏi làm rõ bằng tiếng Việt. Be specific about what is ambiguous."
                    }
                  },
                  "required": ["question"]
                }
                """)),

            // ── Spreadsheet import ──────────────────────────────────────────
            new ToolDefinition(
                "import_data",
                "Declare how to map an imported spreadsheet to Revit data. " +
                "Call this AFTER the user describes what they want to do with the imported file. " +
                "Returns a dry-run preview; the user then confirms before any writes happen. " +
                "Hai loại thao tác: update_parameters (cập nhật tham số theo cột) và create_sheets (tạo bản vẽ).",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "operation": {
                      "type": "string",
                      "enum": ["update_parameters", "create_sheets"],
                      "description": "update_parameters: tìm phần tử theo cột khớp, rồi ghi các cột khác vào tham số. create_sheets: tạo ViewSheet cho mỗi dòng."
                    },
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory (for update_parameters), e.g. OST_Doors, OST_Walls, OST_Rooms."
                    },
                    "match": {
                      "type": "object",
                      "description": "For update_parameters: which spreadsheet column identifies the Revit element.",
                      "properties": {
                        "column": { "type": "string", "description": "Column name in the spreadsheet (exact header)." },
                        "param":  { "type": "string", "description": "Exact Revit parameter name to match against, e.g. 'Mark'." }
                      },
                      "required": ["column", "param"]
                    },
                    "set": {
                      "type": "array",
                      "description": "For update_parameters: columns to write into Revit parameters.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "column": { "type": "string", "description": "Column name in the spreadsheet." },
                          "param":  { "type": "string", "description": "Exact Revit parameter name to write." }
                        },
                        "required": ["column", "param"]
                      }
                    },
                    "numberColumn": {
                      "type": "string",
                      "description": "For create_sheets: column with the sheet number (e.g. 'A-001')."
                    },
                    "nameColumn": {
                      "type": "string",
                      "description": "For create_sheets: column with the sheet name / title."
                    }
                  },
                  "required": ["operation"]
                }
                """)),
        ];
    }

    private static JsonObject Schema(string json) =>
        JsonNode.Parse(json.Trim())!.AsObject();
}
