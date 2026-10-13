// Sonda de integración con el escritorio de MiniChat LAN.
// Lanza el widget en un modo concreto y verifica en el escritorio REAL:
//  1) que una ventana normal lo cubre (no es TopMost),
//  2) que tras Win+D queda visible y utilizable,
//  3) que el área de iconos fuera del widget sigue siendo el escritorio,
//  4) que recibe foco, acepta escritura Unicode y permite seleccionar/copiar,
//  5) que una ventana normal activada vuelve a cubrirlo.
// Uso: MiniChat.DesktopProbe <ruta MiniChat.exe> <DesktopWidget|CompactWindow> [--keep]
// Usa el perfil "probe" (se crea en %LocalAppData%\MiniChatLAN\profiles\probe).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class Program
{
    private static readonly List<string> Results = new();

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2: coordenadas físicas coherentes
        string exe = args[0];
        string mode = args.Length > 1 ? args[1] : "DesktopWidget";
        var originalForeground = GetForegroundWindow();

        var proc = Process.Start(new ProcessStartInfo(exe, $"--desktop-mode {mode} --profile probe --tcp-port 47899 --discovery-port 47898") { UseShellExecute = false })!;
        var restoreList = new List<IntPtr>();
        System.Windows.Forms.Form? cover = null;
        Thread? coverThread = null;
        try
        {
            IntPtr widget = WaitForWindow(proc.Id, TimeSpan.FromSeconds(20));
            Check("widget visible al iniciar", widget != IntPtr.Zero && IsWindowVisible(widget));
            if (widget == IntPtr.Zero) return Report(mode);
            Thread.Sleep(1200);
            GetWindowRect(widget, out var wr);
            var center = new POINT { X = (wr.Left + wr.Right) / 2, Y = (wr.Top + wr.Bottom) / 2 };
            Console.WriteLine($"Widget rect: {wr.Left},{wr.Top},{wr.Right},{wr.Bottom}  exStyle=0x{GetWindowLongPtr(widget, -20):X}");

            // 1) Ventana normal encima
            var ready = new ManualResetEventSlim();
            coverThread = new Thread(() =>
            {
                cover = new System.Windows.Forms.Form
                {
                    Text = "Ventana de prueba (cubre el widget)",
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Left = wr.Left - 40, Top = wr.Top - 40, Width = wr.Width + 80, Height = wr.Height + 80,
                };
                cover.Shown += (_, _) => ready.Set();
                System.Windows.Forms.Application.Run(cover);
            });
            coverThread.SetApartmentState(ApartmentState.STA);
            coverThread.Start();
            ready.Wait(5000);
            Thread.Sleep(800);
            IntPtr coverHwnd = IntPtr.Zero;
            cover!.Invoke(() => { coverHwnd = cover.Handle; cover.Activate(); });
            Thread.Sleep(500);
            Check("una ventana normal cubre el widget", RootAt(center) == coverHwnd, Describe(RootAt(center)));

            // 2) Win+D
            var before = VisibleTopLevel();
            SendWinD();
            Thread.Sleep(1500);
            foreach (var h in before) if (IsIconic(h)) restoreList.Add(h);
            Console.WriteLine($"Win+D minimizó {restoreList.Count} ventanas");
            Check("tras Win+D el widget es visible", IsWindowVisible(widget) && !IsIconic(widget) && RootAt(center) == widget, Describe(RootAt(center)));

            // 3) Área de iconos fuera del widget
            var outside = new POINT { X = wr.Right + 60 < GetSystemMetrics(0) ? wr.Right + 60 : wr.Left - 60, Y = center.Y };
            var outsideRoot = RootAt(outside);
            string outsideClass = ClassOf(WindowFromPoint(outside));
            Check("fuera del widget se alcanza el escritorio/iconos", ClassOf(outsideRoot) is "Progman" or "WorkerW", $"{ClassOf(outsideRoot)} / {outsideClass}");

            // 4) Foco y escritura
            // Caja de texto real de la app: la del nombre (primera ejecución) o la de escribir mensajes.
            var root = AutomationElement.FromHandle(widget);
            var input = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NameBox"))
                     ?? root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "InputBox"));
            if (input is null) { Check("se encontró la caja de texto", false); return Report(mode); }
            var r = input.Current.BoundingRectangle;
            Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
            Thread.Sleep(600);
            bool fgOk = GetForegroundWindow() == widget;
            var inputId = input.Current.AutomationId;
            bool focusOk = Equals(AutomationElement.FocusedElement?.Current.AutomationId, inputId);
            Check("el clic da foco de teclado al widget", fgOk && focusOk, $"fg={Describe(GetForegroundWindow())} focus={AutomationElement.FocusedElement?.Current.AutomationId}");
            const string sample = "Hola ñandú — ü 😀 你好";
            if (fgOk && focusOk)
            {
                Chord(0x11, 0x41); Thread.Sleep(150);   // seleccionar lo que hubiera y sustituirlo
                TypeUnicode(sample);
                Thread.Sleep(600);
                var value = ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                Check("la escritura Unicode llega completa", value == sample, value);
                // Seleccionar todo y copiar
                Chord(0x11, 0x41); Thread.Sleep(200); Chord(0x11, 0x43); Thread.Sleep(400);
                string clip = System.Windows.Forms.Clipboard.GetText();
                Check("selección y copia funcionan", clip == sample, clip);
            }
            else Check("escritura (omitida: sin foco seguro)", false);

            // 5) Activar de nuevo la ventana normal
            cover.Invoke(() => { cover.WindowState = System.Windows.Forms.FormWindowState.Normal; cover.Activate(); });
            Thread.Sleep(700);
            Check("al activar otra ventana vuelve a cubrir el widget", RootAt(center) == coverHwnd, Describe(RootAt(center)));
        }
        finally
        {
            try { cover?.Invoke(() => cover.Close()); } catch { }
            coverThread?.Join(3000);
            if (!args.Contains("--keep")) { try { proc.Kill(); } catch { } }
            foreach (var h in restoreList) ShowWindow(h, 4 /* SW_SHOWNOACTIVATE */);
            if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
        }
        return Report(mode);
    }

    private static int Report(string mode)
    {
        Console.WriteLine($"=== Modo {mode} ===");
        foreach (var r in Results) Console.WriteLine(r);
        return Results.Any(r => r.StartsWith("FALLO")) ? 1 : 0;
    }

    private static void Check(string name, bool ok, string? detail = null)
        => Results.Add($"{(ok ? "OK   " : "FALLO")} {name}{(detail is null ? "" : "  [" + detail + "]")}");

    private static IntPtr WaitForWindow(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p == pid && IsWindowVisible(h) && GetWindowTextStr(h).StartsWith("MiniChat")) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }

    private static List<IntPtr> VisibleTopLevel()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h) && !IsIconic(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    private static IntPtr RootAt(POINT p) => GetAncestor(WindowFromPoint(p), 2);
    private static string Describe(IntPtr h) => h == IntPtr.Zero ? "(nada)" : $"{ClassOf(h)} '{GetWindowTextStr(h)}'";

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    private static string GetWindowTextStr(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    private static void SendWinD()
    {
        var inputs = new[] { Key(0x5B, false), Key(0x44, false), Key(0x44, true), Key(0x5B, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Chord(ushort mod, ushort key)
    {
        var inputs = new[] { Key(mod, false), Key(key, false), Key(key, true), Key(mod, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void TypeUnicode(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 | 0x0002 } } });
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) =>
        new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0u } } };

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[]
        {
            new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } },
            new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
