using Autodesk.Revit.UI;
using RevitMCPAddin;
using RevitMCPAddin.Commands;

namespace RevitAssistant;

/// <summary>
/// Entry point for the Revit Local Assistant add-in.
/// Loaded by Revit at startup via RevitAssistant.addin.
///
/// Threading contract:
///   All Revit API calls (element read + write) must run on the Revit main thread.
///   The ExternalEvent + RevitMCPExternalEventHandler (from RevitMCP.Core submodule)
///   enforce this via an async Task-based queue.
///   The Ollama client (Phase 2) and WPF panel (Phase 3) run on background / UI threads
///   and communicate with Revit only through EnqueueAsync / EnqueueBatchAsync.
///
/// Phases:
///   Phase 1 (this): wire up Core + ExternalEvent dispatcher — add-in loads cleanly.
///   Phase 2: inject dispatcher into OllamaOrchestrator; console-harness dry-run.
///   Phase 3: register DockablePane + Ribbon button for the chat panel.
/// </summary>
public sealed class App : IExternalApplication
{
    // Exposed as internal so Phase 2/3 components in this project can access them.
    // Phase 3: the WPF panel's ViewModel needs the dispatcher to call EnqueueAsync.
    internal static RevitMCPExternalEventHandler? Dispatcher { get; private set; }
    internal static ExternalEvent? DispatcherEvent { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var registry = new CommandRegistry();
            registry.RegisterDefaults();

            var handler = new RevitMCPExternalEventHandler(registry);
            var extEvent = ExternalEvent.Create(handler);
            handler.AttachExternalEvent(extEvent);

            Dispatcher = handler;
            DispatcherEvent = extEvent;

            Log($"[RevitAssistant] Ready — {registry.Names.Count()} commands registered.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit Local Assistant",
                "Failed to initialize:\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Dispatcher = null;
        DispatcherEvent = null;
        return Result.Succeeded;
    }

    private static void Log(string msg)
    {
        try { System.Diagnostics.Debug.WriteLine(msg); } catch { }
    }
}
