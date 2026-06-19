using System.Reflection;
using Autodesk.Revit.UI;
using RevitMCPAddin;
using RevitMCPAddin.Commands;
using RevitAssistant.Llm;
using RevitAssistant.UI;

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
///   Phase 1: wire up Core + ExternalEvent dispatcher — add-in loads cleanly.
///   Phase 2: LLM layer (OllamaClient, IntentParser) — standalone.
///   Phase 3 (this): DockablePane chat panel + Ribbon button. Placeholder chat service.
///   Phase 4: real orchestrator (Ollama → dry-run → preview → confirm → commit).
/// </summary>
public sealed class App : IExternalApplication
{
    /// <summary>Stable id for the chat dockable pane (referenced by the ribbon command).</summary>
    public static readonly DockablePaneId PaneId =
        new(new Guid("C8E2A1F4-3D7B-4A9E-8C16-2F5B7D9E4A03"));

    // Exposed as internal so Phase 4's orchestrator (in this project) can dispatch
    // Revit commands through EnqueueAsync / EnqueueBatchAsync.
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

            RegisterChatPane(application);
            CreateRibbon(application);

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

    /// <summary>Create the chat panel and register it as a dockable pane.</summary>
    private static void RegisterChatPane(UIControlledApplication application)
    {
        // Phase 4: real orchestrator — local Ollama + Revit dispatcher bridge.
        // Overridable without rebuild (also used by the Phase 7 installer):
        //   REVIT_ASSISTANT_OLLAMA_URL  (default http://localhost:11434)
        //   REVIT_ASSISTANT_MODEL       (default qwen2.5:7b-instruct)
        var baseUrl = Environment.GetEnvironmentVariable("REVIT_ASSISTANT_OLLAMA_URL")
                      ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("REVIT_ASSISTANT_MODEL")
                    ?? "qwen2.5:7b-instruct";
        var numCtx = int.TryParse(
            Environment.GetEnvironmentVariable("REVIT_ASSISTANT_NUM_CTX"), out var n) ? n : 8192;

        var llm = new OllamaClient(baseUrl, model, numCtx);
        var bridge = new RevitBridge();
        var chat = new OrchestratorChatService(llm, bridge, modelSchemaJson: null, contextTokens: numCtx);

        var view = new ChatView { DataContext = new ChatViewModel(chat) };
        application.RegisterDockablePane(PaneId, "Trợ lý Revit", new AssistantPaneProvider(view));
    }

    /// <summary>Add the "AI Assistant" ribbon tab with a toggle button.</summary>
    private static void CreateRibbon(UIControlledApplication application)
    {
        const string tab = "AI Assistant";
        try { application.CreateRibbonTab(tab); } catch { /* already exists */ }

        var panel = application.CreateRibbonPanel(tab, "Trợ lý");
        var asmPath = Assembly.GetExecutingAssembly().Location;

        var button = new PushButtonData(
            "ShowRevitAssistant",
            "Trợ lý\nAI",
            asmPath,
            "RevitAssistant.ShowAssistantCommand")
        {
            ToolTip = "Mở/đóng bảng Trợ lý AI (offline, tiếng Việt + English).",
            LongDescription =
                "Trợ lý Revit chạy hoàn toàn offline qua Ollama. Truy vấn model, "
                + "kiểm tra tuân thủ, và sửa tham số hàng loạt với xem trước an toàn.",
        };

        panel.AddItem(button);
    }

    private static void Log(string msg)
    {
        try { System.Diagnostics.Debug.WriteLine(msg); } catch { }
    }
}
