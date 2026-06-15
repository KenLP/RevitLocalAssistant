using System.Text.Json.Nodes;
using RevitAssistant.UI;

namespace RevitAssistant;

/// <summary>
/// Concrete <see cref="IRevitBridge"/> backed by the ExternalEvent dispatcher.
/// Marshals each command onto the Revit main thread via EnqueueAsync and returns
/// the JSON envelope. This is the only place the orchestrator touches Revit.
/// </summary>
internal sealed class RevitBridge : IRevitBridge
{
    public Task<JsonObject> CallAsync(
        string command,
        JsonObject parameters,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var dispatcher = App.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(new JsonObject
            {
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "not_ready",
                    ["message"] = "Bộ điều phối Revit chưa sẵn sàng.",
                },
            });
        }

        return dispatcher.EnqueueAsync(command, parameters, dryRun);
    }
}
