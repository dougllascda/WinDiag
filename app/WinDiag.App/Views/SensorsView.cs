using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinDiag.Core;
using WinDiag.Core.Sensors;

namespace WinDiag.App.Views;

/// <summary>
/// Aba "Sensores": leituras de temperatura, ventoinhas, tensões, potência, clocks e uso, uma vez por segundo
/// enquanto a aba está visível e a janela não está minimizada. Mín/máx/média são calculados aqui por sensor.
/// </summary>
public sealed class SensorsView : UserControl, IDisposable
{
    /// <summary>Cultura do idioma atual para formatar números e horas.</summary>
    private static CultureInfo Pt => WinDiag.Core.Loc.Culture;

    /// <summary>Limites de referência de temperatura (°C): a partir de 75 vira "morno" (aviso) e de 85 "quente"; o limite real depende do modelo.</summary>
    private const double WarnTemp = 75, BadTemp = 85;
    /// <summary>Larguras (px) das colunas numéricas e da coluna de origem; as colunas Média/Origem recolhem a 0 no modo Simples.</summary>
    private const double WideCol = 80, SrcCol = 150;

    /// <summary>Ordem e título dos grupos de sensores dentro de cada cartão.</summary>
    private static readonly string[] GroupTitles = { Loc.T("Temperaturas", "Temperatures"), Loc.T("Ventoinhas", "Fans"), Loc.T("Voltagens", "Voltages"), Loc.T("Potência", "Power"), "Clocks", Loc.T("Uso", "Usage"), Loc.T("Outros", "Other") };
    /// <summary>Títulos das colunas, na ordem da grade de cada grupo.</summary>
    private static readonly string[] ColTitles = { "Sensor", Loc.T("Atual", "Current"), Loc.T("Mín", "Min"), Loc.T("Máx", "Max"), Loc.T("Média", "Avg"), Loc.T("Origem", "Source") };

    /// <summary>Leitura dos sensores; roda fora da UI thread (quem chama decide o mecanismo) e devolve null sem dados.</summary>
    private readonly Func<Task<SensorSnapshot?>> _read;
    /// <summary>Timer de 1 s na UI thread; só roda com a aba visível e a janela não minimizada, para não gastar CPU à toa.</summary>
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>Visão detalhada (cartões por peça).</summary>
    private readonly StackPanel _detailed = new();
    /// <summary>Contêiner dos cartões por peça, ordenados por <see cref="HwCardUi.Rank"/>.</summary>
    private readonly StackPanel _hwHost = new();
    /// <summary>Cartão do modo Simples (resumo de temperaturas e ventoinhas).</summary>
    private readonly Border _simpleCard;
    /// <summary>Linhas de resumo do modo Simples: CPU, GPU e ventoinhas.</summary>
    private readonly TextBlock _simpleCpu = new(), _simpleGpu = new(), _simpleFan = new();
    /// <summary>Cartão de orientação exibido quando não há temperatura de CPU legível.</summary>
    private readonly Border _emptyCard;
    /// <summary>Texto "Aguardando a primeira leitura" até chegar o primeiro snapshot.</summary>
    private readonly TextBlock _waiting;
    /// <summary>Hora da última atualização.</summary>
    private readonly TextBlock _updated;
    /// <summary>Botão que zera mín/máx/média acumulados.</summary>
    private readonly Button _reset;
    /// <summary>Selos das fontes (HWiNFO, LibreHardwareMonitor, nvidia-smi) com destaque quando ativas.</summary>
    private readonly Border _pillHw, _pillLhm, _pillNv;
    /// <summary>Estatística de cada sensor pela chave hardware|nome|tipo; as linhas são criadas uma vez e só têm o texto atualizado (evita reconstruir a grade a cada segundo).</summary>
    private readonly Dictionary<string, SensorRow> _rows = new();
    /// <summary>Cartões por peça, pela chave tipo|nome.</summary>
    private readonly Dictionary<string, HwCardUi> _cards = new();
    /// <summary>Todos os grupos criados, para aplicar o modo Simples/Detalhado a todos.</summary>
    private readonly List<GroupUi> _groups = new();
    /// <summary>Último estado de cada selo, para só mexer no recurso quando muda.</summary>
    private readonly Dictionary<Border, bool> _pillState = new();
    /// <summary>Chave do pincel atual de cada texto, para não reatribuir o recurso sem necessidade.</summary>
    private readonly Dictionary<TextBlock, string> _brushOf = new();
    /// <summary>Janela dona da aba, para observar minimizar/restaurar.</summary>
    private Window? _win;
    /// <summary>_busy evita leituras sobrepostas; _disposed impede trabalho após o descarte; _hasSnapshot indica que já chegou a primeira leitura.</summary>
    private bool _busy, _disposed, _hasSnapshot;

    /// <summary>Estatística acumulada de um sensor e os TextBlocks reaproveitados da sua linha.</summary>
    private sealed class SensorRow
    {
        /// <summary>Tipo do sensor, que define a formatação e o grupo.</summary>
        public SensorKind Kind;
        /// <summary>Unidade informada pela fonte.</summary>
        public string Unit = "";
        /// <summary>Último valor, mínimo, máximo e soma (para a média).</summary>
        public double Last, Min, Max, Sum;
        /// <summary>Quantidade de amostras acumuladas (divisor da média).</summary>
        public long Count;
        /// <summary>Células reaproveitadas da linha: nome, atual, mín, máx, média e origem.</summary>
        public TextBlock Name = null!, Cur = null!, MinT = null!, MaxT = null!, Avg = null!, Src = null!;
        /// <summary>Chave do pincel do valor atual (cor por faixa de temperatura).</summary>
        public string CurBrush = "";

        /// <summary>Reinicia a estatística com o último valor como base.</summary>
        public void Reset()
        {
            Min = Max = Sum = Last;
            Count = 1;
        }

        /// <summary>Acrescenta uma amostra e atualiza mín, máx e soma.</summary>
        public void Add(double v)
        {
            Last = v;
            if (Count == 0) Min = Max = v;
            else
            {
                if (v < Min) Min = v;
                if (v > Max) Max = v;
            }
            Sum += v;
            Count++;
        }
    }

    /// <summary>Um grupo (ex.: Temperaturas) dentro de um cartão: grade de colunas e próxima linha livre.</summary>
    private sealed class GroupUi
    {
        /// <summary>Posição do grupo no cartão (índice em <see cref="GroupTitles"/>).</summary>
        public int Order;
        /// <summary>Grade com cabeçalho e uma linha por sensor.</summary>
        public Grid Grid = null!;
        /// <summary>Próxima linha livre da grade (a 0 é o cabeçalho).</summary>
        public int NextRow = 1;
        /// <summary>Cabeçalhos das colunas Média e Origem, ocultos no modo Simples.</summary>
        public TextBlock HeadAvg = null!, HeadSrc = null!;
    }

    /// <summary>Um cartão por peça (HardwareKind + Hardware).</summary>
    private sealed class HwCardUi
    {
        /// <summary>Ordem do cartão (processador, vídeo, placa-mãe...).</summary>
        public int Rank;
        /// <summary>Cartão visual da peça.</summary>
        public Border Card = null!;
        /// <summary>Corpo do cartão, onde os grupos são inseridos.</summary>
        public StackPanel Body = null!;
        /// <summary>Grupos do cartão, em ordem de tipo.</summary>
        public readonly List<GroupUi> Groups = new();
    }

    /// <summary>Cria a aba; <paramref name="read"/> lê os sensores fora da UI e devolve null quando não há leitura.</summary>
    public SensorsView(Func<Task<SensorSnapshot?>> read)
    {
        _read = read;
        var root = new StackPanel();

        // Faixa de fontes e botão de zerar.
        var strip = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        pills.Children.Add(UiExtra.Text(Loc.T("Fontes ativas:", "Active sources:"), "MutedBrush", 12.5, wrap: false));
        ((TextBlock)pills.Children[0]).Margin = new Thickness(0, 0, 8, 0);
        ((TextBlock)pills.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        _pillHw = MakePill("HWiNFO");
        _pillLhm = MakePill("LibreHardwareMonitor");
        _pillNv = MakePill("nvidia-smi");
        pills.Children.Add(_pillHw);
        pills.Children.Add(_pillLhm);
        pills.Children.Add(_pillNv);
        _updated = UiExtra.Text("", "MutedBrush", 12, wrap: false);
        _updated.VerticalAlignment = VerticalAlignment.Center;
        _updated.Margin = new Thickness(6, 0, 0, 0);
        pills.Children.Add(_updated);
        strip.Children.Add(pills);
        _reset = new Button { Content = Loc.T("Zerar mín/máx", "Reset min/max"), Padding = new Thickness(12, 4, 12, 4) };
        _reset.Click += (_, _) => ResetStats();
        Grid.SetColumn(_reset, 1);
        strip.Children.Add(_reset);
        root.Children.Add(strip);

        _waiting = Ui.Muted(Loc.T("Aguardando a primeira leitura dos sensores...", "Waiting for the first sensor reading..."), 13);
        root.Children.Add(_waiting);

        _emptyCard = BuildEmptyCard();
        root.Children.Add(_emptyCard);

        _simpleCard = BuildSimpleCard();
        root.Children.Add(_simpleCard);

        _detailed.Children.Add(_hwHost);
        root.Children.Add(_detailed);

        Content = root;
        ApplyMode();

        Ui.ModeChanged += OnModeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateRunning();
        _timer.Tick += async (_, _) => await TickAsync();
    }

    // ---------- Ciclo de vida e agendamento ----------

    /// <summary>Ao carregar, passa a observar o estado da janela (minimizada) e reavalia o timer.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        var w = Window.GetWindow(this);
        if (!ReferenceEquals(w, _win))
        {
            if (_win != null) _win.StateChanged -= OnWindowState;
            _win = w;
            if (_win != null) _win.StateChanged += OnWindowState;
        }
        UpdateRunning();
    }

    /// <summary>Ao descarregar, solta o evento da janela para não vazar a aba.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_win != null) _win.StateChanged -= OnWindowState;
        _win = null;
        UpdateRunning();
    }

    /// <summary>Minimizar/restaurar a janela liga ou desliga o timer.</summary>
    private void OnWindowState(object? sender, EventArgs e) => UpdateRunning();

    /// <summary>Liga o timer só com a aba visível e a janela fora do estado minimizado.</summary>
    private void UpdateRunning()
    {
        bool run = !_disposed && IsVisible && IsLoaded && _win is { WindowState: not WindowState.Minimized };
        if (run == _timer.IsEnabled) return;
        if (run)
        {
            _timer.Start();
            _ = TickAsync();
        }
        else _timer.Stop();
    }

    /// <summary>Um ciclo de leitura. Não sobrepõe leituras lentas: se a anterior não terminou, pula o tick. Qualquer falha é engolida porque sensor nunca pode derrubar o app.</summary>
    private async Task TickAsync()
    {
        if (_busy || _disposed) return; // leitura anterior ainda em andamento: pula o tick
        _busy = true;
        try
        {
            var snap = await _read();
            if (snap != null && !_disposed) Apply(snap);
        }
        catch { /* leitura de sensores nunca derruba o app */ }
        finally { _busy = false; }
    }

    /// <summary>Para o timer e solta os eventos.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Ui.ModeChanged -= OnModeChanged;
        if (_win != null) _win.StateChanged -= OnWindowState;
        _win = null;
    }

    /// <summary>Reação ao botão Simples/Detalhado.</summary>
    private void OnModeChanged()
    {
        if (_disposed) return;
        ApplyMode();
    }

    // ---------- Construção fixa ----------

    /// <summary>Cria um selo de fonte, inicialmente apagado.</summary>
    private static Border MakePill(string text)
    {
        var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold };
        b.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return b;
    }

    /// <summary>Acende/apaga um selo (só toca no recurso quando o estado muda).</summary>
    private void SetPill(Border b, bool active)
    {
        if (_pillState.TryGetValue(b, out var cur) && cur == active) return;
        _pillState[b] = active;
        b.SetResourceReference(Border.BackgroundProperty, active ? "OkBrush" : "LineBrush");
        ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, active ? "BadgeTextBrush" : "MutedBrush");
        ((TextBlock)b.Child).ToolTip = active ? Loc.T("Fonte em uso nesta leitura", "Source in use in this reading") : Loc.T("Fonte sem leituras nesta leitura", "Source with no readings in this reading");
    }

    /// <summary>Abre um endereço no navegador padrão; ignora falha.</summary>
    private static void OpenUrl(string url)
    {
        try { WinDiag.Core.Util.Shell.OpenUnelevated(url); }
        catch { /* sem navegador padrão */ }
    }

    /// <summary>Cartão com orientação para quando a temperatura do processador não pode ser lida (HWiNFO ou PawnIO; o WinDiag não baixa nem instala drivers).</summary>
    private static Border BuildEmptyCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardTitle(Loc.T("Temperatura do processador indisponível", "Processor temperature unavailable")));
        var t = UiExtra.Text(Loc.T("A temperatura do processador precisa de um sensor de baixo nível. Duas opções: " +
            "(1) abra o HWiNFO com 'Shared Memory Support' ligado (Configurações do HWiNFO > Shared Memory Support) e o WinDiag lê todos os sensores dele; " +
            "(2) instale o driver PawnIO pelo site oficial (o WinDiag não baixa nem instala drivers).",
            "Processor temperature needs a low-level sensor. Two options: " +
            "(1) open HWiNFO with 'Shared Memory Support' turned on (HWiNFO settings > Shared Memory Support) and WinDiag reads all of its sensors; " +
            "(2) install the PawnIO driver from the official site (WinDiag does not download or install drivers)."), "TextBrush", 13);
        sp.Children.Add(t);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var hw = new Button { Content = Loc.T("Abrir site do HWiNFO", "Open the HWiNFO website"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        hw.Click += (_, _) => OpenUrl("https://www.hwinfo.com/");
        var pawn = new Button { Content = Loc.T("Abrir site do PawnIO", "Open the PawnIO website"), Padding = new Thickness(12, 4, 12, 4) };
        pawn.Click += (_, _) => OpenUrl("https://pawnio.eu/");
        row.Children.Add(hw);
        row.Children.Add(pawn);
        sp.Children.Add(row);
        var card = Ui.Card(sp);
        card.Visibility = Visibility.Collapsed;
        return card;
    }

    /// <summary>Cartão do modo Simples: temperatura de CPU/GPU com faixa em palavras e estado das ventoinhas.</summary>
    private Border BuildSimpleCard()
    {
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Ui.CardTitle(Loc.T("Resumo da temperatura", "Temperature summary")));
        ((TextBlock)head.Children[0]).Margin = new Thickness(0);
        var info = UiExtra.Icon("", 13, "MutedBrush");
        info.Margin = new Thickness(6, 0, 0, 0);
        info.Cursor = System.Windows.Input.Cursors.Help;
        info.Background = Brushes.Transparent;
        var tip = new StackPanel { MaxWidth = 360 };
        tip.Children.Add(new TextBlock { Text = Loc.T("Como ler as faixas", "How to read the ranges"), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        tip.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("Normal: abaixo de 75 °C. Morno: de 75 a 84 °C. Quente: 85 °C ou mais. " +
                   "São faixas de referência para processador e placa de vídeo sob carga; o limite real depende do modelo. " +
                   "Temperatura alta por si só não prova defeito: veja também o uso, as ventoinhas e a poeira.",
                   "Normal: below 75 °C. Warm: 75 to 84 °C. Hot: 85 °C or more. " +
                   "These are reference ranges for the processor and graphics card under load; the real limit depends on the model. " +
                   "A high temperature alone does not prove a fault: also check usage, fans and dust.")
        });
        var tt = new ToolTip { Content = tip };
        tt.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tt.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        tt.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        info.ToolTip = tt;
        ToolTipService.SetInitialShowDelay(info, 250);
        ToolTipService.SetShowDuration(info, 60000);
        System.Windows.Automation.AutomationProperties.SetName(info, Loc.T("Explicação das faixas de temperatura", "Explanation of the temperature ranges"));
        head.Children.Add(info);
        sp.Children.Add(head);
        foreach (var t in new[] { _simpleCpu, _simpleGpu, _simpleFan })
        {
            t.FontSize = 14;
            t.Margin = new Thickness(0, 3, 0, 3);
            t.Text = WinDiag.Core.Fmt.NA;
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            sp.Children.Add(t);
        }
        return Ui.Card(sp);
    }

    // ---------- Modo Simples/Detalhado ----------

    /// <summary>Alterna entre Simples (resumo, sem Média/Origem) e Detalhado (todas as colunas e o botão de zerar).</summary>
    private void ApplyMode()
    {
        bool simple = Ui.Simple;
        _simpleCard.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        _detailed.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        _reset.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        foreach (var g in _groups) ApplyGroupMode(g, !simple);
        foreach (var r in _rows.Values)
        {
            r.Avg.Visibility = r.Src.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Mostra ou oculta as colunas Média/Origem de um grupo (colunas de largura 0 no Simples).</summary>
    private static void ApplyGroupMode(GroupUi g, bool detailed)
    {
        g.Grid.ColumnDefinitions[4].Width = new GridLength(detailed ? WideCol : 0);
        g.Grid.ColumnDefinitions[5].Width = new GridLength(detailed ? SrcCol : 0);
        var v = detailed ? Visibility.Visible : Visibility.Collapsed;
        g.HeadAvg.Visibility = g.HeadSrc.Visibility = v;
    }

    // ---------- Atualização ----------

    /// <summary>Aplica um snapshot na UI thread: atualiza linhas existentes e cria, em lote ordenado, as novas.</summary>
    private void Apply(SensorSnapshot snap)
    {
        try
        {
            if (!_hasSnapshot)
            {
                _hasSnapshot = true;
                _waiting.Visibility = Visibility.Collapsed;
            }
            var fresh = new List<SensorReading>();
            foreach (var r in snap.Readings)
            {
                if (!double.IsFinite(r.Value)) continue;
                if (_rows.TryGetValue(KeyOf(r), out var row)) UpdateRow(row, r);
                else fresh.Add(r);
            }
            if (fresh.Count > 0)
            {
                fresh.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                foreach (var r in fresh)
                {
                    var key = KeyOf(r);
                    if (_rows.TryGetValue(key, out var existing)) UpdateRow(existing, r);
                    else _rows[key] = CreateRow(r);
                }
            }

            var sources = snap.SourcesUsed;
            SetPill(_pillHw, Has(sources, "hwinfo"));
            SetPill(_pillLhm, Has(sources, "librehardware") || Has(sources, "lhm"));
            SetPill(_pillNv, Has(sources, "nvidia"));
            _updated.Text = Loc.T("Atualizado às ", "Updated at ") + snap.Time.ToString("HH:mm:ss", Pt);

            var (cpu, gpu) = PickTemps(snap);
            _emptyCard.Visibility = cpu.HasValue ? Visibility.Collapsed : Visibility.Visible;
            UpdateSimple(snap, cpu, gpu);
        }
        catch { /* falha ao exibir uma leitura não derruba a aba */ }
    }

    /// <summary>true se alguma fonte usada contém o trecho informado.</summary>
    private static bool Has(IReadOnlyList<string> list, string part)
    {
        foreach (var s in list)
            if (s.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Chave estável do sensor.</summary>
    private static string KeyOf(SensorReading r) => r.Hardware + "|" + r.Name + "|" + r.Kind;

    /// <summary>Zera mín/máx/média tomando o valor atual como base.</summary>
    private void ResetStats()
    {
        foreach (var r in _rows.Values)
        {
            r.Reset();
            r.MinT.Text = r.MaxT.Text = r.Avg.Text = Fmt(r.Kind, r.Last, r.Unit);
        }
    }

    /// <summary>Atualiza estatística, textos e cor de uma linha; a cor só muda para temperaturas.</summary>
    private static void UpdateRow(SensorRow row, SensorReading r)
    {
        row.Add(r.Value);
        string unit = r.Unit;
        row.Unit = unit;
        SetText(row.Cur, Fmt(row.Kind, r.Value, unit));
        SetText(row.MinT, Fmt(row.Kind, row.Min, unit));
        SetText(row.MaxT, Fmt(row.Kind, row.Max, unit));
        if (!Ui.Simple) SetText(row.Avg, Fmt(row.Kind, row.Sum / row.Count, unit));
        string brush = row.Kind == SensorKind.Temperature
            ? (r.Value >= BadTemp ? "BadBrush" : r.Value >= WarnTemp ? "WarnBrush" : "TextBrush")
            : "TextBrush";
        if (brush != row.CurBrush)
        {
            row.CurBrush = brush;
            row.Cur.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }
    }

    /// <summary>Atribui texto só se mudou, evitando invalidar o layout à toa.</summary>
    private static void SetText(TextBlock t, string s)
    {
        if (!string.Equals(t.Text, s, StringComparison.Ordinal)) t.Text = s;
    }

    // ---------- Linhas e cartões ----------

    /// <summary>Grupo (seção) do cartão para cada tipo de sensor.</summary>
    private static int GroupOf(SensorKind k) => k switch
    {
        SensorKind.Temperature => 0,
        SensorKind.Fan => 1,
        SensorKind.Voltage => 2,
        SensorKind.Power or SensorKind.Current => 3,
        SensorKind.Clock => 4,
        SensorKind.Load => 5,
        _ => 6
    };

    /// <summary>Traduz o tipo de hardware das fontes em ordem e rótulo do cartão.</summary>
    private static (int Rank, string Label) HardwareLabel(string kind)
    {
        string k = kind ?? "";
        bool Is(string p) => k.Contains(p, StringComparison.OrdinalIgnoreCase);
        if (Is("cpu")) return (0, Loc.T("Processador", "Processor"));
        if (IsGpuKind(k)) return (1, Loc.T("Placa de vídeo", "Graphics card"));
        if (Is("mainboard") || Is("motherboard") || Is("superio") || Is("embedded") || Is("placa-m")) return (2, Loc.T("Placa-mãe", "Motherboard"));
        if (Is("memory") || Is("memória") || Is("ram")) return (3, Loc.T("Memória", "Memory"));
        if (Is("storage") || Is("armazenamento") || Is("disk") || Is("hdd") || Is("ssd")) return (4, Loc.T("Armazenamento", "Storage"));
        return (5, string.IsNullOrWhiteSpace(k) ? Loc.T("Outros", "Other") : k);
    }

    /// <summary>Obtém ou cria o cartão da peça, inserindo-o na ordem de importância.</summary>
    private HwCardUi GetCard(SensorReading r)
    {
        string key = r.HardwareKind + "|" + r.Hardware;
        if (_cards.TryGetValue(key, out var c)) return c;
        var (rank, label) = HardwareLabel(r.HardwareKind);
        var body = new StackPanel();
        string title = string.IsNullOrWhiteSpace(r.Hardware) || string.Equals(r.Hardware, label, StringComparison.OrdinalIgnoreCase) ? label : label + " — " + r.Hardware;
        body.Children.Add(Ui.CardTitle(title));
        c = new HwCardUi { Rank = rank, Body = body, Card = Ui.Card(body) };
        _cards[key] = c;
        int at = 0;
        for (int i = 0; i < _hwHost.Children.Count; i++)
        {
            if (_hwHost.Children[i] is Border b && b.Tag is int rk && rk <= rank) at = i + 1;
        }
        c.Card.Tag = rank;
        _hwHost.Children.Insert(at, c.Card);
        return c;
    }

    /// <summary>Obtém ou cria o grupo dentro do cartão, mantendo a ordem dos tipos.</summary>
    private GroupUi GetGroup(HwCardUi card, int order)
    {
        foreach (var g in card.Groups)
            if (g.Order == order) return g;

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WideCol + 10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WideCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WideCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WideCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SrcCol) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var g2 = new GroupUi { Order = order, Grid = grid };
        for (int i = 0; i < ColTitles.Length; i++)
        {
            var h = i == 0
                ? UiExtra.Text(GroupTitles[order], "AccentBrush", 12.5, FontWeights.SemiBold, wrap: false)
                : UiExtra.Text(ColTitles[i], "MutedBrush", 11.5, wrap: false);
            h.Margin = new Thickness(0, 4, 8, 2);
            Grid.SetColumn(h, i);
            grid.Children.Add(h);
            if (i == 4) g2.HeadAvg = h;
            if (i == 5) g2.HeadSrc = h;
        }

        int before = 0;
        foreach (var g in card.Groups)
            if (g.Order < order) before++;
        card.Groups.Insert(before, g2);
        card.Body.Children.Insert(1 + before, grid);
        _groups.Add(g2);
        ApplyGroupMode(g2, !Ui.Simple);
        return g2;
    }

    /// <summary>Cria a linha de um sensor novo (nome, atual, mín, máx, média, origem).</summary>
    private SensorRow CreateRow(SensorReading r)
    {
        var card = GetCard(r);
        var grp = GetGroup(card, GroupOf(r.Kind));
        int rowIndex = grp.NextRow++;
        grp.Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock Cell(int col, string text, bool muted, bool right)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 1, 8, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            if (right) t.TextAlignment = TextAlignment.Right;
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
            Grid.SetRow(t, rowIndex);
            Grid.SetColumn(t, col);
            grp.Grid.Children.Add(t);
            return t;
        }

        var row = new SensorRow { Kind = r.Kind, Unit = r.Unit, Last = r.Value, Min = r.Value, Max = r.Value, Sum = 0, Count = 0 };
        row.Name = Cell(0, r.Name, false, false);
        row.Name.ToolTip = r.Name;
        row.Cur = Cell(1, "", false, true);
        row.Cur.FontWeight = FontWeights.SemiBold;
        row.MinT = Cell(2, "", true, true);
        row.MaxT = Cell(3, "", true, true);
        row.Avg = Cell(4, "", true, true);
        row.Src = Cell(5, r.Source ?? "", true, false);
        row.CurBrush = "TextBrush";
        bool simple = Ui.Simple;
        row.Avg.Visibility = row.Src.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        row.Avg.Text = Fmt(row.Kind, r.Value, r.Unit);
        UpdateRow(row, r);
        return row;
    }

    // ---------- Formatação e resumo ----------

    /// <summary>Formata o valor conforme o tipo do sensor (°C, RPM, V, A, W, MHz/GHz, %).</summary>
    private static string Fmt(SensorKind kind, double v, string unit)
    {
        string u = (unit ?? "").Trim();
        switch (kind)
        {
            case SensorKind.Temperature: return v.ToString("0", Pt) + " °C";
            case SensorKind.Fan:
                return u == "%" ? v.ToString("0", Pt) + " %" : v.ToString("0", Pt) + " RPM";
            case SensorKind.Voltage: return v.ToString("0.000", Pt) + " V";
            case SensorKind.Current: return v.ToString("0.000", Pt) + " A";
            case SensorKind.Power: return v.ToString("0.0", Pt) + " W";
            case SensorKind.Clock:
                return u.Contains("GHz", StringComparison.OrdinalIgnoreCase) ? v.ToString("0.00", Pt) + " GHz" : v.ToString("0", Pt) + " MHz";
            case SensorKind.Load: return v.ToString("0.0", Pt) + " %";
            default: return v.ToString("0.##", Pt) + (u.Length > 0 ? " " + u : "");
        }
    }

    /// <summary>true se o tipo de hardware indica placa de vídeo.</summary>
    private static bool IsGpuKind(string k) =>
        k.Contains("gpu", StringComparison.OrdinalIgnoreCase) || k.Contains("vídeo", StringComparison.OrdinalIgnoreCase) || k.Contains("video", StringComparison.OrdinalIgnoreCase);

    /// <summary>Temperatura representativa do processador e da placa de vídeo (mesma regra dos alertas; null = indisponível).</summary>
    internal static (double? Cpu, double? Gpu) PickTemps(SensorSnapshot s) => WinDiag.Core.Collectors.SensorsCollector.PickMaxTemps(s.Readings);

    /// <summary>Atualiza o resumo do modo Simples (CPU, GPU e ventoinhas).</summary>
    private void UpdateSimple(SensorSnapshot snap, double? cpu, double? gpu)
    {
        SetSimple(_simpleCpu, Loc.T("Processador", "Processor"), cpu);
        SetSimple(_simpleGpu, Loc.T("Placa de vídeo", "Graphics card"), gpu);

        int fans = 0, spinning = 0;
        foreach (var r in snap.Readings)
        {
            if (r.Kind != SensorKind.Fan || !double.IsFinite(r.Value)) continue;
            fans++;
            if (r.Value > 0) spinning++;
        }
        string text = fans == 0 ? Loc.T("Ventoinhas: n/d (nenhum sensor de ventoinha)", "Fans: n/a (no fan sensor)")
            : spinning > 0 ? Loc.T("Ventoinhas: girando", "Fans: spinning")
            : Loc.T("Ventoinhas: paradas (podem estar em modo silencioso)", "Fans: stopped (they may be in silent mode)");
        SetStyled(_simpleFan, text, fans == 0 ? "MutedBrush" : "TextBrush");
    }

    /// <summary>Mostra a temperatura com faixa em palavras (normal, morno, quente); sem leitura mostra "n/d".</summary>
    private void SetSimple(TextBlock t, string label, double? temp)
    {
        if (!temp.HasValue)
        {
            SetStyled(t, label + Loc.T(": n/d (sensor indisponível)", ": n/a (sensor unavailable)"), "MutedBrush");
            return;
        }
        double v = temp.Value;
        string band = v >= BadTemp ? Loc.T("quente", "hot") : v >= WarnTemp ? Loc.T("morno", "warm") : Loc.T("normal", "normal");
        string brush = v >= BadTemp ? "BadBrush" : v >= WarnTemp ? "WarnBrush" : "OkBrush";
        SetStyled(t, $"{label}: {v.ToString("0", Pt)} °C ({band})", brush);
    }

    /// <summary>Atribui texto e pincel do tema (só se mudaram).</summary>
    private void SetStyled(TextBlock t, string text, string brush)
    {
        SetText(t, text);
        if (_brushOf.TryGetValue(t, out var cur) && cur == brush) return;
        _brushOf[t] = brush;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
