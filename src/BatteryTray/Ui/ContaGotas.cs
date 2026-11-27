using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryTray.Ui;

/// <summary>
/// Fork pedrocirinoo: conta-gotas. Uma camada quase transparente cobre todas as telas;
/// o clique esconde a camada e le a cor do pixel sob o cursor. Esc cancela.
/// </summary>
public static class ContaGotas
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    public static void Escolher(Action<string> escolhida)
    {
        var camada = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Cursor = Cursors.Cross,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
        };
        camada.KeyDown += (_, e) => { if (e.Key == Key.Escape) camada.Close(); };
        camada.MouseLeftButtonDown += (_, _) =>
        {
            GetCursorPos(out var p);
            camada.Hide();
            // Espera a camada sumir da tela antes de ler o pixel.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                camada.Close();
                IntPtr dc = GetDC(IntPtr.Zero);
                uint cor = GetPixel(dc, p.X, p.Y);
                ReleaseDC(IntPtr.Zero, dc);
                if (cor == 0xFFFFFFFF) return; // fora da tela
                escolhida($"#{cor & 0xFF:X2}{(cor >> 8) & 0xFF:X2}{(cor >> 16) & 0xFF:X2}");
            };
            t.Start();
        };
        camada.Show();
        camada.Activate();
    }
}
