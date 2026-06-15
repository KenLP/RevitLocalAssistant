using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RevitAssistant.UI;

// ─── Phase 3 stub ────────────────────────────────────────────────────────────
// Full MVVM implementation comes in Phase 3.  For now the type must exist so
// the Addin project can reference it without compile errors.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class ChatViewModel : ObservableObject
{
    [ObservableProperty]
    private string _inputText = string.Empty;

    // Phase 3: bind to an ObservableCollection<ChatMessage>
    // Phase 4: PreviewItems (elements affected by pending edit)

    [RelayCommand]
    private Task SendAsync() => Task.CompletedTask; // Phase 4: full orchestrator call
}
