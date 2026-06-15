// ─────────────────────────────────────────────────────────────────────────────
// PLACEHOLDER — will be removed in Phase 1 when the git submodule
// RevitMCP.Core is added.  See CORE_EXTRACTION.md for what the
// RevitMCPServer session must produce before Phase 1 can start.
//
// The namespace (RevitMCPAddin.Commands) intentionally matches the upstream
// MCP repo so consuming code needs NO using-changes after the swap.
// ─────────────────────────────────────────────────────────────────────────────
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Commands;

public enum ExecutionKind { ReadOnly, ModelWrite, UiAction }

public interface IRevitCommand
{
    string Name { get; }
    bool IsReadOnly { get; }
    string RiskLevel => IsReadOnly ? "read" : "low";
    ExecutionKind Execution => IsReadOnly ? ExecutionKind.ReadOnly : ExecutionKind.ModelWrite;
    JsonNode? Execute(CommandContext ctx);
}

public sealed class CommandContext
{
    public required UIApplication App { get; init; }
    public Document? Doc { get; init; }
    public JsonObject Parameters { get; init; } = [];
    public bool DryRun { get; init; }
    public Document RequireDoc() =>
        Doc ?? throw new InvalidOperationException("No active Revit document.");
}

public sealed class RevitCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class JsonResult
{
    public static JsonObject Success(JsonNode? data = null) =>
        new() { ["ok"] = true, ["data"] = data };

    public static JsonObject Error(string code, string message, string? type = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (type is not null) err["type"] = type;
        return new JsonObject { ["ok"] = false, ["error"] = err };
    }
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, IRevitCommand> _commands = [];

    public void Register(IRevitCommand cmd) => _commands[cmd.Name] = cmd;
    public bool TryGet(string name, out IRevitCommand? cmd) =>
        _commands.TryGetValue(name, out cmd);
    public IEnumerable<string> Names => _commands.Keys;

    public IEnumerable<(string Name, bool IsReadOnly, string RiskLevel, string ExecutionKind)> Describe()
    {
        foreach (var kv in _commands)
            yield return (kv.Key, kv.Value.IsReadOnly, kv.Value.RiskLevel,
                          kv.Value.Execution.ToString());
    }

    // Phase 1: this will delegate to the real RevitMCP.Core submodule implementation
    // which registers all ~70 commands from RegisterDefaults().
    public void RegisterDefaults() { }
}

public sealed record BatchStep(string CommandName, JsonObject Parameters);
