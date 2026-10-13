using System.Windows;
using System.Windows.Controls;
using MiniChat.App.ViewModels;

namespace MiniChat.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnChangePin(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        // Los PIN no se guardan en ninguna propiedad: se pasan y se borran de las cajas.
        await vm.ChangePinAsync(CurrentPinBox.Password, NewPinBox.Password, ConfirmPinBox.Password);
        CurrentPinBox.Clear();
        NewPinBox.Clear();
        ConfirmPinBox.Clear();
    }
}
