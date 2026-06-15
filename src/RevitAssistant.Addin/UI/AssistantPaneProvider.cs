using System.Windows;
using Autodesk.Revit.UI;

namespace RevitAssistant;

/// <summary>
/// Supplies the chat UserControl to Revit's dockable-pane host.
/// The content (ChatView) is created once in <see cref="App.OnStartup"/> on the
/// Revit UI thread and handed in here.
/// </summary>
internal sealed class AssistantPaneProvider : IDockablePaneProvider
{
    private readonly FrameworkElement _content;

    public AssistantPaneProvider(FrameworkElement content) => _content = content;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _content;
        data.VisibleByDefault = false;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
        };
    }
}
