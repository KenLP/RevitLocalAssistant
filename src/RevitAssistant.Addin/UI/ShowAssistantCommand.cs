using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAssistant;

/// <summary>
/// Ribbon button handler — toggles the chat dockable pane on/off.
/// Registered by class name in <see cref="App.CreateRibbon"/>'s PushButtonData,
/// so it must live in the loaded add-in assembly (RevitAssistant.dll).
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowAssistantCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var pane = commandData.Application.GetDockablePane(App.PaneId);
            if (pane.IsShown())
                pane.Hide();
            else
                pane.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
