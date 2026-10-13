using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniChat.App.ViewModels;

namespace MiniChat.App.Views;

public partial class LockView : UserControl
{
    public LockView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(() => PinBox.Focus()); };
    }

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var pin = PinBox.Password;
        PinBox.Clear();
        await vm.UnlockAsync(pin);
        if (vm.Locked) PinBox.Focus();
    }

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnUnlock(sender, e);
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Se borrarán de ESTE equipo todas las conversaciones y stickers guardados. No se puede deshacer.\n\n" +
            "Tus contactos y autorizaciones se conservan, y la copia que tienen tus compañeros no cambia.\n\n¿Continuar?",
            "Crear PIN nuevo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await vm.ResetForgottenPinAsync(NewPinBox.Password, NewPinConfirmBox.Password);
        NewPinBox.Clear();
        NewPinConfirmBox.Clear();
    }
}
