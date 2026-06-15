using Autodesk.Revit.UI;

namespace RevitAssistant;

/// <summary>
/// Entry point loaded by Revit via RevitAssistant.addin.
///
/// Phase 0: minimal stub — Revit loads the addin, nothing crashes.
/// Phase 1: wire up Core (submodule) + ExternalEvent dispatcher.
/// Phase 2: start Ollama orchestrator.
/// Phase 3: register DockablePane + Ribbon button for the chat panel.
/// </summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }
}
