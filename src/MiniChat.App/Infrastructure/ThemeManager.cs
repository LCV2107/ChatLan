using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace MiniChat.App.Infrastructure;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Cambia entre la paleta clara y la oscura reemplazando el diccionario de paleta. Los brushes de Theme.xaml
/// toman su color con DynamicResource, así que el cambio se aplica en vivo sin recargar las vistas.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static AppTheme Parse(string? s) => s switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    public static string Id(AppTheme t) => t switch { AppTheme.Light => "light", AppTheme.Dark => "dark", _ => "system" };

    /// <summary>¿La paleta efectiva es oscura ahora mismo?</summary>
    public static bool IsDarkActive => Current switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDark(),
    };

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        if (Application.Current is null) return;
        bool dark = theme switch { AppTheme.Dark => true, AppTheme.Light => false, _ => IsSystemDark() };
        var uri = new Uri($"pack://application:,,,/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var palette = new ResourceDictionary { Source = uri };
        var dicts = Application.Current.Resources.MergedDictionaries;
        // Quitar la paleta anterior e insertar la nueva al principio: remove+insert invalida de forma fiable
        // todas las referencias DynamicResource (el reemplazo por índice no siempre repinta la ventana entera).
        // Quitar la paleta anterior e insertar la nueva al principio invalida de forma fiable las referencias
        // DynamicResource. El cuerpo de la ventana se actualiza al instante; la barra de título (zona de
        // WindowChrome, que cachea su composición) termina de ajustarse al reabrir la ventana.
        foreach (var old in dicts.Where(d => d.Contains("BgColor")).ToList()) dicts.Remove(old);
        dicts.Insert(0, palette);
    }

    /// <summary>Vuelve a aplicar la paleta si el tema es «Sistema» (p. ej. cuando Windows cambia de claro a oscuro).</summary>
    public static void ReapplyIfSystem()
    {
        if (Current == AppTheme.System) Apply(AppTheme.System);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch
        {
            return true;   // ante la duda, oscuro (como hasta ahora)
        }
    }
}
