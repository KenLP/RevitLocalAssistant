namespace RevitMCPAddin.Commands;

/// <summary>
/// The one place that defines which commands RevitAssistant can dispatch:
/// upstream Core's RegisterDefaults() plus the vendored commands upstream main
/// does not ship (see the VENDORED banner in Commands/*.cs).
///
/// Both the add-in (App.OnStartup) and the tool-surface contract tests build
/// the registry through this factory, so "what the tests check" and "what the
/// app dispatches against" cannot drift apart.
/// </summary>
public static class AssistantCommands
{
    public static CommandRegistry CreateRegistry()
    {
        var registry = new CommandRegistry();
        registry.RegisterDefaults();

        // Vendored — not in upstream main (fork drift, kept local deliberately).
        registry.Register(new QueryWhereCommand());
        registry.Register(new UpdateWhereCommand());
        registry.Register(new ImportParametersCommand());

        return registry;
    }
}
