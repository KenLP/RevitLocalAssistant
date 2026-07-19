using System.Text.Json.Nodes;
using RevitAssistant.Llm;
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
        // Deny-by-default, enforced again at the boundary. The orchestrator already
        // rejects unknown tool names, but this is the last gate before Core — which
        // registers ~90 commands including delete/create/move. Anything not in
        // ToolPolicy never reaches the dispatcher, whoever called us.
        if (!ToolPolicy.IsDispatchable(command))
        {
            return Task.FromResult(new JsonObject
            {
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "tool_not_allowed",
                    ["message"] = $"Lệnh '{command}' không nằm trong danh sách được phép.",
                },
            });
        }

        var dispatcher = App.Dispatcher;
        if (dispatcher is null) return Task.FromResult(NotReady());

        return dispatcher.EnqueueAsync(command, parameters, dryRun);
    }

    public Task<JsonObject> CallBatchAsync(
        IReadOnlyList<(string Command, JsonObject Parameters)> steps,
        bool stopOnError = true,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        // The allowlist applies per step — batching must not become a way around it.
        foreach (var (command, _) in steps)
        {
            if (!ToolPolicy.IsDispatchable(command))
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "tool_not_allowed",
                        ["message"] = $"Lệnh '{command}' không nằm trong danh sách được phép.",
                    },
                });
            }
        }

        var dispatcher = App.Dispatcher;
        if (dispatcher is null) return Task.FromResult(NotReady());

        var batchSteps = steps
            .Select(s => new RevitMCPAddin.BatchStep(s.Command, s.Parameters))
            .ToList();

        return dispatcher.EnqueueBatchAsync(batchSteps, stopOnError, dryRun);
    }

    private static JsonObject NotReady() => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = "not_ready",
            ["message"] = "Bộ điều phối Revit chưa sẵn sàng.",
        },
    };
}
