using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RevitAssistant.UI;

/// <summary>
/// Chat panel UserControl. Hosted inside a Revit DockablePane by the Addin.
/// Code-behind only handles view concerns: Enter-to-send and auto-scroll.
/// All logic lives in <see cref="ChatViewModel"/>.
/// </summary>
public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnMessagesChanged;

        if (DataContext is ChatViewModel vm)
        {
            _observed = vm.Messages;
            _observed.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Defer until layout settles so the new bubble is included in the extent.
        Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()),
                               DispatcherPriority.Background);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a newline.
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }
}
