using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WinDiag.App.Views;

/// <summary>Uma série do gráfico: nome, pincel do tema e valores (null = sem leitura).</summary>
public sealed class ChartSeries
{
    /// <summary>Nome exibido na legenda.</summary>
    public string Name { get; set; } = "";
    /// <summary>Pincel da linha (use chaves do tema, ex.: Ui.Res("AccentBrush")).</summary>
    public Brush Color { get; set; } = Brushes.Gray;
    /// <summary>Valores em ordem cronológica.</summary>
    public List<double?> Values { get; } = new();
}

/// <summary>Gráfico de linhas simples (sem biblioteca): grade leve, últimos N pontos, eixo Y fixo.</summary>
public sealed class LineChart : FrameworkElement
{
    /// <summary>Séries desenhadas.</summary>
    public List<ChartSeries> Series { get; } = new();
    /// <summary>Valor mínimo do eixo Y.</summary>
    public double YMin { get; set; }
    /// <summary>Valor máximo do eixo Y.</summary>
    public double YMax { get; set; } = 100;
    /// <summary>Quantidade de pontos mantidos (janela deslizante).</summary>
    public int MaxPoints { get; set; } = 120;
    /// <summary>Sufixo das marcas do eixo Y.</summary>
    public string YUnit { get; set; } = "";
    /// <summary>Formato numérico das marcas do eixo Y.</summary>
    public string YFormat { get; set; } = "0";

    /// <summary>Cria o gráfico e o redesenha quando o tema muda (a grade e o texto usam chaves do tema).</summary>
    public LineChart()
    {
        MinHeight = 140;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => Themes.ThemeManager.Changed += OnTheme;
        Unloaded += (_, _) => Themes.ThemeManager.Changed -= OnTheme;
    }

    private void OnTheme() => InvalidateVisual();

    /// <summary>Adiciona uma série e a devolve.</summary>
    public ChartSeries AddSeries(string name, Brush color)
    {
        var s = new ChartSeries { Name = name, Color = color };
        Series.Add(s);
        return s;
    }

    /// <summary>Remove todos os pontos.</summary>
    public void Clear()
    {
        foreach (var s in Series) s.Values.Clear();
        InvalidateVisual();
    }

    /// <summary>Acrescenta um valor por série (na ordem em que foram adicionadas).</summary>
    public void Push(params double?[] values)
    {
        for (int i = 0; i < Series.Count && i < values.Length; i++)
        {
            Series[i].Values.Add(values[i]);
            while (Series[i].Values.Count > MaxPoints) Series[i].Values.RemoveAt(0);
        }
        InvalidateVisual();
    }

    private static Brush Res(string key, Brush fallback)
    {
        try { return (Brush)Application.Current.FindResource(key); } catch { return fallback; }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 160 : Math.Max(availableSize.Height, MinHeight));

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        var line = Res("ChartGridBrush", Brushes.Gray);
        var muted = Res("MutedBrush", Brushes.Gray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI Variable Text, Segoe UI");

        const double left = 38, top = 6, bottomLegend = 22;
        double pw = w - left - 6, ph = h - top - bottomLegend - 4;
        if (pw < 10 || ph < 10) return;
        double range = YMax - YMin; if (range <= 0) range = 1;

        // Sem Freeze: o pincel do tema não pode ser congelado (mudaria de cor ao vivo).
        var gridPen = new Pen(line, 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph * i / 4;
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + pw, y));
            double val = YMax - range * i / 4;
            var ft = new FormattedText(val.ToString(YFormat, WinDiag.Core.Loc.Culture) + YUnit, WinDiag.Core.Loc.Culture,
                FlowDirection.LeftToRight, typeface, 10, muted, dpi);
            dc.DrawText(ft, new Point(left - ft.Width - 4, y - ft.Height / 2));
        }

        foreach (var s in Series)
        {
            var pen = new Pen(s.Color, 1.6) { LineJoin = PenLineJoin.Round };
            int n = s.Values.Count;
            if (n == 0) continue;
            double step = pw / Math.Max(MaxPoints - 1, 1);
            // alinha à direita: o ponto mais recente fica na borda direita
            double x0 = left + pw - (n - 1) * step;
            StreamGeometry? geo = null; StreamGeometryContext? ctx = null; bool open = false;
            for (int i = 0; i < n; i++)
            {
                var v = s.Values[i];
                if (v is null) { open = false; continue; }
                double c = Math.Max(YMin, Math.Min(YMax, v.Value));
                var p = new Point(x0 + i * step, top + ph - (c - YMin) / range * ph);
                if (geo is null) { geo = new StreamGeometry(); ctx = geo.Open(); }
                if (!open) { ctx!.BeginFigure(p, false, false); open = true; }
                else ctx!.LineTo(p, true, true);
            }
            if (geo is not null) { ctx!.Close(); geo.Freeze(); dc.DrawGeometry(null, pen, geo); }
        }

        double lx = left;
        foreach (var s in Series)
        {
            var label = s.Name;
            var ft = new FormattedText(label, WinDiag.Core.Loc.Culture, FlowDirection.LeftToRight, typeface, 11, muted, dpi);
            double ly = h - bottomLegend + 8;
            dc.DrawRectangle(s.Color, null, new Rect(lx, ly + 2, 10, 4));
            dc.DrawText(ft, new Point(lx + 14, ly - 3));
            lx += 14 + ft.Width + 16;
        }
    }
}
