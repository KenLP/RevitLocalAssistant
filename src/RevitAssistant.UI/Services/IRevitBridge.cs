using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Pure-JSON bridge to the Revit command dispatcher. Defined with only
/// System.Text.Json types so the orchestrator (and its tests) carry no Revit
/// API dependency. The Addin supplies the concrete implementation backed by
/// the ExternalEvent queue.
/// </summary>
public interface IRevitBridge
{
    /// <summary>
    /// Run one registered Revit command. When <paramref name="dryRun"/> is true,
    /// model-write commands execute inside a transaction that is rolled back, so
    /// the caller can preview the effect without committing.
    /// Returns the dispatcher envelope: { ok, data?, error?, dryRun?, committed? }.
    /// </summary>
    Task<JsonObject> CallAsync(
        string command,
        JsonObject parameters,
        bool dryRun = false,
        CancellationToken ct = default);
}
