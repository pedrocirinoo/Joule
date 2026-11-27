using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BatteryTray.Ui;

/// <summary>
/// Fork pedrocirinoo: seletor de cor com espectro. Quadrado de saturacao/brilho, barra de
/// matiz, previa, campo hex e conta-gotas. OK aplica; Esc fecha sem mudar.
/// </summary>
public sealed class SeletorCor : Window
{
    private const double L = 240, A = 160, BarraA = 14;
    private double _h, _s, _v;
    private readonly Rectangle _matiz = new() { Width = L, Height = A };
    private readonly Ellipse _marca = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle _marcaBarra = new() { Width = 4, Height = BarraA + 4, Stroke = Brushes.White, StrokeThickness = 1.5, Fill = Brushes.Transparent, IsHitTestVisible = false, RadiusX = 2, RadiusY = 2 };
    private readonly Border _previa = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(15) };
    private readonly TextBox _hex = new() { Width = 86, MaxLength = 7, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Action<string> _ok;
    private bool _pegandoDaTela;

    public SeletorCor(string corAtual, Action<string> ok)
    {
        _ok = ok;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var fundo = Recurso("Card.Fill", Color.FromRgb(0x26, 0x26, 0x26));
        var texto = Recurso("Text.Primary", Colors.White);
        _hex.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        _hex.Foreground = texto;
        _hex.CaretBrush = texto;
        _hex.BorderThickness = new Thickness(1);
        _hex.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));

        // Quadrado: matiz pura + branco da esquerda + preto de baixo.
        var quadrado = new Grid { Width = L, Height = A, ClipToBounds = true, Cursor = Cursors.Cross };
        quadrado.Children.Add(_matiz);
        quadrado.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        quadrado.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var camadaMarca = new Canvas { IsHitTestVisible = false };
        camadaMarca.Children.Add(_marca);
        quadrado.Children.Add(camadaMarca);
        Arrastar(quadrado, p => { _s = Math.Clamp(p.X / L, 0, 1); _v = 1 - Math.Clamp(p.Y / A, 0, 1); Atualizar(); });

        // Barra de matiz (arco-iris).
        var arco = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) arco.GradientStops.Add(new GradientStop(Hsv(i * 60 % 360 == 0 && i == 6 ? 359.9 : i * 60, 1, 1), i / 6.0));
        var barra = new Grid { Width = L, Height = BarraA + 4, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand };
        barra.Children.Add(new Border { Height = BarraA, CornerRadius = new CornerRadius(BarraA / 2), Background = arco, VerticalAlignment = VerticalAlignment.Center });
        var camadaBarra = new Canvas { IsHitTestVisible = false };
        camadaBarra.Children.Add(_marcaBarra);
        barra.Children.Add(camadaBarra);
        Arrastar(barra, p => { _h = Math.Clamp(p.X / L, 0, 0.9999) * 360; Atualizar(); });

        // Linha de baixo: previa, hex, conta-gotas, OK.
        var contaGotas = Botao(new Viewbox { Width = 15, Height = 15, Child = new System.Windows.Shapes.Path { Data = Icons.Dropper, Stroke = texto, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } }, 30);
        contaGotas.ToolTip = BatteryTray.Core.L.T("Pegar uma cor da tela", "Pick a color from the screen");
        contaGotas.Click += (_, _) =>
        {
            _pegandoDaTela = true;
            Hide();
            ContaGotas.Escolher(c => { Dispatcher.Invoke(() => { DeHex(c); Atualizar(); Show(); Activate(); _pegandoDaTela = false; }); });
        };
        var botaoOk = Botao(new TextBlock { Text = "OK", FontWeight = FontWeights.SemiBold, Foreground = texto, Margin = new Thickness(12, 2, 12, 2) }, double.NaN);
        botaoOk.Click += (_, _) => Confirmar();

        var linha = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        linha.Children.Add(_previa);
        _hex.Margin = new Thickness(10, 0, 0, 0);
        linha.Children.Add(_hex);
        DockPanel.SetDock(botaoOk, Dock.Right);
        DockPanel.SetDock(contaGotas, Dock.Right);
        linha.Children.Add(botaoOk);
        contaGotas.Margin = new Thickness(0, 0, 6, 0);
        linha.Children.Add(contaGotas);

        var pilha = new StackPanel();
        pilha.Children.Add(quadrado);
        pilha.Children.Add(barra);
        pilha.Children.Add(linha);
        Content = new Border
        {
            Background = fundo is SolidColorBrush sb && sb.Color.A < 0xF0 ? new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)) : fundo,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = pilha,
        };

        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { DeHex(_hex.Text); Atualizar(); e.Handled = true; } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Enter && !_hex.IsKeyboardFocused) Confirmar(); };
        Deactivated += (_, _) => { if (!_pegandoDaTela && IsVisible) Close(); };
        Loaded += (_, _) =>
        {
            // Abre perto do cursor, dentro da tela.
            var m = Mouse.GetPosition(this);
            var p = PointToScreen(m);
            var src = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var dip = src.Transform(p);
            Left = Math.Min(dip.X - ActualWidth / 2, SystemParameters.WorkArea.Right - ActualWidth - 8);
            Top = Math.Min(dip.Y - ActualHeight - 12, SystemParameters.WorkArea.Bottom - ActualHeight - 8);
            if (Top < SystemParameters.WorkArea.Top) Top = SystemParameters.WorkArea.Top + 8;
            if (Left < SystemParameters.WorkArea.Left) Left = SystemParameters.WorkArea.Left + 8;
        };

        DeHex(corAtual);
        Atualizar();
    }

    private void Confirmar()
    {
        _ok(ParaHex(Hsv(_h, _s, _v)));
        Close();
    }

    private void Atualizar()
    {
        _matiz.Fill = new SolidColorBrush(Hsv(_h, 1, 1));
        var cor = Hsv(_h, _s, _v);
        _previa.Background = new SolidColorBrush(cor);
        if (!_hex.IsKeyboardFocused) _hex.Text = ParaHex(cor);
        Canvas.SetLeft(_marca, _s * L - 6);
        Canvas.SetTop(_marca, (1 - _v) * A - 6);
        _marca.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_marcaBarra, _h / 360 * L - 2);
        Canvas.SetTop(_marcaBarra, 0);
    }

    private void DeHex(string texto)
    {
        texto = texto.Trim();
        if (!texto.StartsWith('#')) texto = "#" + texto;
        if (!BatteryTray.Core.Settings.IsHex(texto)) return;
        int v = Convert.ToInt32(texto[1..], 16);
        double r = ((v >> 16) & 0xFF) / 255.0, g = ((v >> 8) & 0xFF) / 255.0, b = (v & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        _v = max;
        _s = max == 0 ? 0 : d / max;
        if (d == 0) return; // cinza: mantem a matiz de antes
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        _h = (h * 60 + 360) % 360;
    }

    private static Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static string ParaHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void Arrastar(FrameworkElement alvo, Action<Point> mover)
    {
        alvo.MouseLeftButtonDown += (_, e) => { alvo.CaptureMouse(); mover(e.GetPosition(alvo)); e.Handled = true; };
        alvo.MouseMove += (_, e) => { if (alvo.IsMouseCaptured) mover(e.GetPosition(alvo)); };
        alvo.MouseLeftButtonUp += (_, _) => alvo.ReleaseMouseCapture();
    }

    private static Button Botao(object conteudo, double lado)
    {
        var b = new Button
        {
            Content = conteudo,
            Height = 30,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
        };
        if (!double.IsNaN(lado)) b.Width = lado;
        return b;
    }

    private static Brush Recurso(string chave, Color reserva) =>
        System.Windows.Application.Current?.Resources[chave] as Brush ?? new SolidColorBrush(reserva);
}
