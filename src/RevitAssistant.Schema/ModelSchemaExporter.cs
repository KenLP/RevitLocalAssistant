using Autodesk.Revit.DB;

namespace RevitAssistant.Schema;

// ─── Phase 5 stub ────────────────────────────────────────────────────────────
// Reads the live Revit model and produces a compact JSON context that is
// injected into the LLM prompt so the model only sees parameter names that
// actually exist in this specific project.
//
// Output shape (Phase 5):
// {
//   "categories": [
//     {
//       "name": "Walls",
//       "builtInCategory": "OST_Walls",
//       "count": 142,
//       "instanceParams": [
//         { "name": "Comments", "storageType": "String", "readOnly": false },
//         { "name": "Unconnected Height", "storageType": "Double", "units": "meters" }
//       ],
//       "typeParams": [...]
//     }
//   ]
// }
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ModelSchemaExporter
{
    private readonly Document _doc;

    public ModelSchemaExporter(Document doc)
    {
        _doc = doc;
    }

    // Phase 5: returns a compact schema JSON string (<4 KB) for LLM context
    public string Export() => "{}";
}
