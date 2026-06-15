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

            // ── Element query ───────────────────────────────────────────────
            new ToolDefinition(
                "list_elements",
                "List elements in a category (name, id, type). Use for a quick overview. " +
                "Dùng để liệt kê nhanh cấu kiện theo loại danh mục.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory, e.g. 'OST_Walls'. If omitted, lists all elements (slow)."
                    },
                    "limit": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": 500,
                      "description": "Max results. Default 100."
                    }
                  }
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
                    "filters": {
                      "type": "array",
                      "description": "Optional filters (AND). Same shape as find_elements.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameterName": { "type": "string" },
                          "operator": { "type": "string", "enum": ["eq","neq","contains","gt","lt","gte","lte"] },
                          "value": { }
                        },
                        "required": ["parameterName", "value"]
                      }
                    }
                  },
                  "required": ["category"]
                }
                """)),

            new ToolDefinition(
                "find_elements",
                "List elements by category + parameter filters. Returns id, name, type, and requested fields. " +
                "Dùng khi cần DANH SÁCH/ID cấu kiện theo điều kiện: " +
                "'liệt kê / tìm tất cả [cấu kiện] có [tham số] [toán tử] [giá trị]'. " +
                "For 'how many' use count_elements instead. " +
                "IMPORTANT: call this BEFORE set_parameter_batch to discover element IDs.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "category": {
                      "type": "string",
                      "description": "BuiltInCategory name (required). Examples: OST_Walls, OST_Doors, OST_Rooms, OST_Floors, OST_Columns, OST_StructuralFraming, OST_Windows."
                    },
                    "filters": {
                      "type": "array",
                      "description": "Parameter filters (all must match — AND logic).",
                      "items": {
                        "type": "object",
                        "properties": {
                          "parameterName": {
                            "type": "string",
                            "description": "Exact Revit parameter name, e.g. 'Comments', 'Fire Rating', 'Mark', 'Name'."
                          },
                          "operator": {
                            "type": "string",
                            "enum": ["eq","neq","contains","gt","lt","gte","lte"],
                            "description": "eq=equals, neq=not_equals, contains=string contains, gt/lt/gte/lte=numeric."
                          },
                          "value": {
                            "description": "The value to compare against (string or number)."
                          }
                        },
                        "required": ["parameterName", "value"]
                      }
                    },
                    "fields": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Extra parameter names to include in each result."
                    },
                    "limit": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": 500,
                      "description": "Max results. Default 200."
                    }
                  },
                  "required": ["category"]
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

            // ── Parameter edit ──────────────────────────────────────────────
            new ToolDefinition(
                "set_parameter",
                "Set ONE parameter on ONE element. " +
                "Dùng khi chỉ cần sửa một cấu kiện duy nhất. " +
                "For bulk edits prefer set_parameter_batch.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "integer", "description": "ElementId of the element to edit." },
                    "parameterName": {
                      "type": "string",
                      "description": "Exact Revit parameter name, e.g. 'Comments', 'Fire Rating', 'Mark'."
                    },
                    "value": {
                      "description": "New value. String for text params; number for numeric; true/false for Yes/No."
                    },
                    "units": {
                      "type": "string",
                      "enum": ["meters", "feet", "internal"],
                      "description": "Unit for numeric length/area/volume params. Default 'internal' (Revit feet). Use 'meters' when the user gives metric values."
                    }
                  },
                  "required": ["id", "parameterName", "value"]
                }
                """)),

            new ToolDefinition(
                "set_parameter_batch",
                "Set the SAME parameter to the SAME value on MULTIPLE elements in one transaction. " +
                "Dùng cho bulk edit: 'đặt [tham số] = [giá trị] cho tất cả [cấu kiện]'. " +
                "ALWAYS call find_elements first to get the ids array.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "ids": {
                      "type": "array",
                      "items": { "type": "integer" },
                      "description": "Array of ElementId integers to edit. Get these from find_elements first.",
                      "minItems": 1
                    },
                    "parameterName": {
                      "type": "string",
                      "description": "Exact Revit parameter name."
                    },
                    "value": {
                      "description": "New value. Same type rules as set_parameter."
                    },
                    "units": {
                      "type": "string",
                      "enum": ["meters", "feet", "internal"],
                      "description": "Unit for numeric dimensional params. Default 'internal'."
                    },
                    "atomic": {
                      "type": "boolean",
                      "description": "If true, rollback ALL on any failure. Default false (best-effort)."
                    }
                  },
                  "required": ["ids", "parameterName", "value"]
                }
                """)),

            new ToolDefinition(
                "rename_element",
                "Rename an element (sets its Name parameter). " +
                "Dùng khi người dùng muốn đổi tên một phần tử.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "integer", "description": "ElementId." },
                    "newName": { "type": "string", "description": "The new name." }
                  },
                  "required": ["id", "newName"]
                }
                """)),

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
        ];
    }

    private static JsonObject Schema(string json) =>
        JsonNode.Parse(json.Trim())!.AsObject();
}
