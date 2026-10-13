using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniChat.App.ViewModels;

namespace MiniChat.App.Views;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _atBottom = true;
    private bool _loadingOlder;
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ChatViewModel);
        MessageList.Loaded += (_, _) =>
        {
            _scroll ??= FindChild<ScrollViewer>(MessageList);
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(() => InputBox.Focus()); };
        // El selector de stickers es otra ventana: hereda la protección de capturas si está activada.
        StickerPopup.Opened += (_, _) => Desktop.CaptureProtection.ApplyToPopup(StickerPopup);
    }

    private void Attach(ChatViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.MessageAppended -= OnMessageAppended;
            _vm.ConversationLoaded -= OnConversationLoaded;
        }
        _vm = vm;
        if (_vm is null) return;
        _vm.MessageAppended += OnMessageAppended;
        _vm.ConversationLoaded += OnConversationLoaded;
    }

    /// <summary>Enter envía; Mayús+Enter inserta un salto de línea.</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
        }
    }

    private void OnConversationLoaded(object? sender, EventArgs e)
    {
        NewMessagesButton.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() =>
        {
            ScrollToEnd();
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>No se desplaza si el usuario está leyendo mensajes antiguos: se muestra un indicador.</summary>
    private void OnMessageAppended(object? sender, MessageItem item)
    {
        if (item.IsOutgoing || _atBottom)
        {
            Dispatcher.BeginInvoke(ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            NewMessagesButton.Visibility = Visibility.Visible;
        }
    }

    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        _atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 8;
        if (_atBottom) NewMessagesButton.Visibility = Visibility.Collapsed;

        // Carga por bloques al llegar arriba, conservando la posición de lectura.
        if (_scroll.VerticalOffset <= 0 && e.VerticalChange < 0 && !_loadingOlder && _vm is { HasOlder: true })
        {
            _loadingOlder = true;
            try
            {
                double before = _scroll.ExtentHeight;
                int added = await _vm.LoadOlderAsync();
                if (added > 0)
                {
                    MessageList.UpdateLayout();
                    _scroll.ScrollToVerticalOffset(_scroll.ExtentHeight - before);
                }
            }
            finally
            {
                _loadingOlder = false;
            }
        }
    }

    private void OnJumpToEnd(object sender, RoutedEventArgs e)
    {
        ScrollToEnd();
        NewMessagesButton.Visibility = Visibility.Collapsed;
    }

    private void ScrollToEnd()
    {
        if (MessageList.Items.Count == 0) return;
        MessageList.ScrollIntoView(MessageList.Items[^1]);
        _scroll?.ScrollToEnd();
        _atBottom = true;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
