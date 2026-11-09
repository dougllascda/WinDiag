using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDiag.Core;
using WinDiag.Core.Live;

namespace WinDiag.App.Views;

/// <summary>Aba "Captura": amostragem 1/s com gráficos ao vivo e resumo de percentis.</summary>
public sealed class CaptureView : UserControl, IDisposable
{
    // Cores: somente chaves do tema (Ui.Res); nenhum valor fixo.
    /// <summary>Seletor de duração: botões de opção estilizados nos dois temas.</summary>
    private readonly StackPanel _dur = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
    /// <summary>Inicia a captura (estilo de destaque).</summary>
    private readonly Button _start = new() { Content = Loc.T("Iniciar", "Start"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("AccentButton") };
    /// <summary>Interrompe a captura em andamento.</summary>
    private readonly Button _stop = new() { Content = Loc.T("Parar", "Stop"), Padding = new Thickness(16, 6, 16, 6), IsEnabled = false };
    /// <summary>Contagem regressiva exibida ao lado dos botões.</summary>
    private readonly TextBlock _countdown = new() { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Jogo detectado durante a captura (processo com maior uso de GPU 3D).</summary>
    private readonly TextBlock _game = Ui.Muted(Loc.T("Jogo detectado: n/d", "Game detected: n/a"), 13);
    /// <summary>Gráfico de CPU, GPU 3D, DPC e disco (0–100 %).</summary>
    private readonly LineChart _chart = new() { MaxPoints = 300, YUnit = "%" };
    /// <summary>Gráfico de RAM disponível em GB; a escala sobe se a leitura passar do total estimado.</summary>
    private readonly LineChart _ramChart = new() { MaxPoints = 300, YUnit = " GB", YFormat = "0.#", MinHeight = 110 };
    /// <summary>Cabeçalhos da tabela de percentis.</summary>
    private static readonly string[] SummaryColumns = { Loc.T("Métrica", "Metric"), "P50", "P95", "P99", Loc.T("Máx.", "Max.") };
    /// <summary>Área onde o resumo final é inserido.</summary>
    private readonly StackPanel _result = new();
    /// <summary>Cancelamento da captura em curso; nulo quando parada.</summary>
    private CancellationTokenSource? _cts;
    /// <summary>Duração escolhida (s), usada no cálculo do tempo restante.</summary>
    private int _total;
    /// <summary>true após <see cref="Dispose"/>: impede mexer na interface de uma janela já fechada.</summary>
    private bool _disposed;
    /// <summary>Fornece a VRAM total para calcular a razão de uso de memória de vídeo.</summary>
    private readonly Func<double?>? _getVram;

    /// <summary>Monta a aba de captura.</summary>
    /// <param name="getVramMiB">Fornece a VRAM total (MiB) da última varredura; null/n.d. quando indisponível.</param>
    public CaptureView(Func<double?>? getVramMiB = null)
    {
        _getVram = getVramMiB;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("Captura de stutter", "Stutter capture"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });

        foreach (var s in Capture.DurationOptions)
        {
            _dur.Children.Add(new RadioButton
            {
                Content = s >= 60 ? $"{s / 60} min" + (s % 60 != 0 ? $" {s % 60} s" : "") : $"{s} s",
                Tag = s,
                GroupName = "capdur",
                IsChecked = s == 60,
                Foreground = Ui.Res("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
            });
        }
        _countdown.Foreground = Ui.Res("TextBrush");

        var body = new StackPanel();
        body.Children.Add(Ui.CardTitle(Loc.T("Medição durante o jogo (somente leitura)", "Measurement while gaming (read-only)")));
        body.Children.Add(Ui.Muted(Loc.T("Mede, a cada segundo, CPU, DPC/interrupções, disco, paginação, RAM e memória de vídeo. Inicie, volte para o jogo e jogue normalmente até o tempo acabar.", "Measures CPU, DPC/interrupts, disk, paging, RAM and video memory every second. Start, go back to the game and play normally until the time runs out.")));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        bar.Children.Add(new TextBlock { Text = Loc.T("Duração:", "Duration:"), Foreground = Ui.Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(_dur); bar.Children.Add(_start); bar.Children.Add(_stop); bar.Children.Add(_countdown);
        body.Children.Add(bar);
        _game.Foreground = Ui.Res("TextBrush");
        body.Children.Add(_game);
        root.Children.Add(Ui.Card(body));

        _chart.AddSeries("CPU %", Ui.Res("AccentBrush"));
        _chart.AddSeries("GPU 3D %", Ui.Res("OkBrush"));
        _chart.AddSeries("DPC %", Ui.Res("WarnBrush"));
        _chart.AddSeries(Loc.T("Disco %", "Disk %"), Ui.Res("BadBrush"));
        _chart.Height = 190;
        var c1 = new StackPanel();
        c1.Children.Add(Ui.CardTitle(Loc.T("Uso ao vivo (0–100 %)", "Live usage (0–100 %)")));
        c1.Children.Add(_chart);
        root.Children.Add(Ui.Card(c1));

        _ramChart.AddSeries(Loc.T("RAM disponível (GB)", "Available RAM (GB)"), Ui.Res("InfoBrush"));
        _ramChart.YMax = RamTotalGB();
        _ramChart.Height = 130;
        var c2 = new StackPanel();
        c2.Children.Add(Ui.CardTitle(Loc.T("RAM disponível", "Available RAM")));
        c2.Children.Add(_ramChart);
        root.Children.Add(Ui.Card(c2));

        root.Children.Add(_result);
        Content = root;

        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop();
        // Não cancela em Unloaded: trocar de aba não deve interromper a captura.
        // O descarte acontece no fechamento da janela (MainWindow.Closing).
    }

    /// <summary>RAM física total arredondada para cima (escala do gráfico de RAM).</summary>
    private static double RamTotalGB()
    {
        try { return Math.Ceiling(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824d); }
        catch { return 32; }
    }

    /// <summary>Executa a captura (amostragem em tarefa de fundo); o <see cref="Progress{T}"/> devolve cada amostra à UI thread, onde os gráficos são atualizados.</summary>
    private async Task StartAsync()
    {
        if (_cts is not null) return;
        _total = _dur.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is int t ? t : 60;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        _start.IsEnabled = false; _dur.IsEnabled = false; _stop.IsEnabled = true;
        _result.Children.Clear();
        _chart.Clear(); _ramChart.Clear();
        _ramChart.YMax = RamTotalGB();
        _game.Text = Loc.T("Jogo detectado: n/d", "Game detected: n/a");
        _countdown.Text = Loc.T($"Restam {_total} s", $"{_total} s left");
        double? vram = null;
        try { vram = _getVram?.Invoke(); } catch { /* sem VRAM: a captura segue sem a razão de uso */ }
        var progress = new Progress<CaptureSample>(OnSample);
        CaptureSummary? sum = null;
        string? failure = null;
        try { sum = await Capture.RunAsync(_total, progress, cts.Token, vram); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex.Message; }
        _cts = null;
        cts.Dispose();
        if (_disposed) return;
        _start.IsEnabled = true; _dur.IsEnabled = true; _stop.IsEnabled = false;
        _countdown.Text = failure is not null ? Loc.T("Falha na captura: ", "Capture failed: ") + failure
            : sum?.Cancelled == true ? Loc.T("Captura interrompida.", "Capture stopped.") : Loc.T("Captura concluída.", "Capture finished.");
        if (sum is not null) ShowSummary(sum);
    }

    /// <summary>Recebe uma amostra por segundo (UI thread) e atualiza gráficos, contagem e jogo detectado.</summary>
    private void OnSample(CaptureSample s)
    {
        _chart.Push(s.CpuPct, s.Gpu3DPct, s.DpcPct, s.DiskBusyPct);
        if (s.RamAvailGB.HasValue && s.RamAvailGB.Value > _ramChart.YMax) _ramChart.YMax = Math.Ceiling(s.RamAvailGB.Value / 8) * 8;
        _ramChart.Push(s.RamAvailGB);
        _countdown.Text = Loc.T($"Restam {Math.Max(0, _total - s.Second)} s", $"{Math.Max(0, _total - s.Second)} s left");
        _game.Text = s.GamePid.HasValue
            ? Loc.T("Jogo detectado: ", "Game detected: ") + $"{s.GameName} ({s.GamePid})"
              + (s.GameWorkingSetMB.HasValue ? Loc.T(" | memória ", " | memory ") + $"{Fmt.Num(s.GameWorkingSetMB.Value, 0)} MB" : "")
              + (s.GameCpuPct.HasValue ? $" | CPU {Fmt.Num(s.GameCpuPct.Value, 1)} %" : "")
              + (s.GamePageFaultsPerSec.HasValue ? Loc.T(" | falhas de página ", " | page faults ") + $"{Fmt.Num(s.GamePageFaultsPerSec.Value, 0)}/s" : "")
            : Loc.T("Jogo detectado: n/d (nenhum processo com uso de GPU 3D)", "Game detected: n/a (no process using the 3D GPU)");
    }

    /// <summary>Monta o cartão de percentis (P50/P95/P99/máx.) e as interpretações; sem afirmar causa, só correlação.</summary>
    private void ShowSummary(CaptureSummary sum)
    {
        var body = new StackPanel();
        body.Children.Add(Ui.CardTitle(Loc.T("Resumo da captura", "Capture summary")));
        body.Children.Add(Ui.Muted($"{sum.SampleCount}" + Loc.T(" amostra(s)", " sample(s)") + (sum.GameName is not null ? Loc.T(" | jogo: ", " | game: ") + $"{sum.GameName} ({sum.GamePid})" : "") + $" | {Fmt.DateTimeSec(DateTime.Now)}", 12));
        if (sum.Rows.Count > 0 && sum.SampleCount > 0)
        {
            var t = new Core.Model.InfoTable();
            t.Columns.AddRange(SummaryColumns);
            string F(double? v, string u) => v.HasValue ? Fmt.Num(v.Value, 1) + (u.Length > 0 ? " " + u : "") : Fmt.NA;
            foreach (var r in sum.Rows) t.Rows.Add(new List<string> { r.Metric, F(r.P50, r.Unit), F(r.P95, r.Unit), F(r.P99, r.Unit), F(r.Max, r.Unit) });
            var grid = Ui.Table(t);
            grid.Margin = new Thickness(0, 8, 0, 8);
            grid.IsReadOnly = true;
            body.Children.Add(grid);
        }
        var h = new TextBlock { Text = Loc.T("Interpretação", "Interpretation"), Foreground = Ui.Res("AccentBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 3) };
        body.Children.Add(h);
        foreach (var line in sum.Interpretation)
            body.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = Ui.Res("TextBrush") });
        if (sum.PresentMonHint is not null) body.Children.Add(Ui.Muted(sum.PresentMonHint));
        body.Children.Add(Ui.Muted(Loc.T("Medições indicam correlação, não causa comprovada.", "Measurements indicate correlation, not a proven cause.")));
        _result.Children.Clear();
        _result.Children.Add(Ui.Card(body));
    }

    /// <summary>Pede o cancelamento; ignora se a fonte já foi descartada.</summary>
    private void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Cancela uma captura em andamento e impede atualizações de interface depois do fechamento da janela.</summary>
    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
