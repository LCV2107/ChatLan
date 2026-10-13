using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniChat.App.ViewModels;

namespace MiniChat.App.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is MainViewModel { OnboardingPinOnly: true }) PinBox.Focus();
                else { NameBox.Focus(); NameBox.SelectAll(); }
            });
        };
    }

    private async void OnContinue(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // El PIN no se enlaza a ninguna propiedad: se pasa directamente y se borra de las cajas.
        await vm.FinishOnboardingAsync(PinBox.Password, PinConfirmBox.Password);
        PinBox.Clear();
        PinConfirmBox.Clear();
    }

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnContinue(sender, e);
    }
}
