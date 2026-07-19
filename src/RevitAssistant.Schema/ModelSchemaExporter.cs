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

    /// <summary>
    /// NOT IMPLEMENTED YET (Phase 5) — returns a compact schema JSON string (&lt;4 KB) for
    /// LLM context. Throws instead of returning "{}": an empty schema silently strips the
    /// prompt of the project's real categories and parameter names, so the model falls back
    /// to guessing them and the degradation is invisible. The orchestrator currently builds
    /// its own schema sample instead; this exporter has no callers yet.
    /// </summary>
    public string Export() => throw new NotImplementedException(
        "ModelSchemaExporter is not implemented yet (Phase 5). Returning an empty schema " +
        "would silently degrade prompt grounding.");
}
