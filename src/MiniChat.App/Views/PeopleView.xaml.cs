using System.Windows;
using System.Windows.Controls;

namespace MiniChat.App.Views;

public partial class PeopleView : UserControl
{
    public PeopleView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.DataContext = b.DataContext;
            menu.IsOpen = true;
        }
    }
}
