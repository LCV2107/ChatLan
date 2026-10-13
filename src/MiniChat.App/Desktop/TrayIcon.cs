using System.Drawing;
using WinForms = System.Windows.Forms;

namespace MiniChat.App.Desktop;

/// <summary>
/// Icono de bandeja (NotifyIcon de Windows Forms, incluido en el runtime de escritorio de .NET).
/// Distingue ocultar (la app sigue recibiendo mensajes) de salir (cierra la app y la red).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _normal;
    private readonly Icon _unread;
    private readonly WinForms.ToolStripMenuItem _toggle;
    private readonly WinForms.ToolStripMenuItem _mute;
    private bool _hasUnread;

    public TrayIcon(string tooltip)
    {
        _normal = LoadIcon("minichat.ico");
        _unread = LoadIcon("minichat-unread.ico");
        _toggle = new WinForms.ToolStripMenuItem("Mostrar", null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
        _mute = new WinForms.ToolStripMenuItem("Silenciar notificaciones", null, (_, _) => MuteToggled?.Invoke(this, EventArgs.Empty)) { CheckOnClick = false };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Personas", null, (_, _) => PeopleRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Ajustes", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(_mute);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Salir de MiniChat", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        menu.Opening += (_, _) => MenuOpening?.Invoke(this, EventArgs.Empty);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _normal,
            Text = Trim(tooltip),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ToggleRequested?.Invoke(this, EventArgs.Empty); };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ToggleRequested;
    public event EventHandler? PeopleRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? MuteToggled;
    public event EventHandler? MenuOpening;
    public event EventHandler? BalloonClicked;

    public void SetMenuState(bool windowVisible, bool muted)
    {
        _toggle.Text = windowVisible ? "Ocultar" : "Mostrar";
        _mute.Checked = muted;
    }

    public void SetUnread(bool unread, string tooltip)
    {
        if (_hasUnread != unread)
        {
            _hasUnread = unread;
            _icon.Icon = unread ? _unread : _normal;
        }
        _icon.Text = Trim(tooltip);
    }

    /// <summary>Aviso discreto del sistema: no roba el foco ni abre ninguna conversación.</summary>
    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, Trim(title, 60), string.IsNullOrEmpty(text) ? " " : Trim(text, 200), WinForms.ToolTipIcon.None);

    /// <summary>
    /// Explorer recrea el área de notificación al reiniciarse; WinForms vuelve a registrar el icono
    /// al recibir "TaskbarCreated", pero se fuerza por si acaso.
    /// </summary>
    public void Reinstall()
    {
        _icon.Visible = false;
        _icon.Visible = true;
    }

    private static string Trim(string s, int max = 63) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Icon LoadIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normal.Dispose();
        _unread.Dispose();
    }
}
