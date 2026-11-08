using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using WinDiag.App.Themes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinDiag.App.Views;
using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Live;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Sensors;

namespace WinDiag.App;

/// <summary>Janela principal: cabeçalho, abas por seção, barra de status e exportação do relatório.</summary>
public partial class MainWindow : Window, IDisposable
{
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Uma aba: ícone, rótulo, chaves de seção exibidas e conteúdo.</summary>
    private sealed class Page
    {
        public string Icon = "", Label = "", Key = "", PtLabel = "";
        public string[] Keys = Array.Empty<string>();
        public bool IsSummary, IsPlaceholder;
        public StackPanel Loading = new();
        public StackPanel Body = new();
        public LiveCard? Live;
        /// <summary>Aba com controle próprio (Otimizações, Sessão de jogo, Captura).</summary>
        public Func<FrameworkElement>? View;
        /// <summary>Item de aba criado para esta página (usado para ocultar por modo).</summary>
        public TabItem? Tab;
    }

    /// <summary>Abas visíveis no modo Simples (as demais aparecem só no modo Detalhado), identificadas pela chave interna estável.</summary>
    private static readonly HashSet<string> SimpleTabs = new()
    {
        "overview", "plan", "optimizations", "session", "sensors", "settings"
    };

    private static readonly string[] ModeLabels = { Loc.T("Simples", "Simple"), Loc.T("Detalhado", "Detailed") };
    private Segmented? _modeSeg;

    private static bool IsPageVisible(Page p) => !Ui.Simple || SimpleTabs.Contains(p.Key);

    /// <summary>Mostra/oculta as abas conforme o modo; se a aba atual sumir, volta para a Visão geral.</summary>
    private void ApplyModeToTabs()
    {
        foreach (var p in _pages)
            if (p.Tab != null) p.Tab.Visibility = IsPageVisible(p) ? Visibility.Visible : Visibility.Collapsed;
        int sel = Tabs.SelectedIndex;
        if (sel < 0 || sel >= _pages.Count || !IsPageVisible(_pages[sel])) Tabs.SelectedIndex = 0;
    }

    /// <summary>Troca ao vivo entre Simples e Detalhado: abas, páginas, Visão geral e Plano.</summary>
    private void OnModeChanged()
    {
        if (_closed) return;
        _modeSeg?.Select(Ui.Simple ? 0 : 1);
        ApplyModeToTabs();
        if (_engine != null)
        {
            foreach (var p in _pages.Where(x => !x.IsPlaceholder && !x.IsSummary)) RefreshPage(_engine, p);
            RefreshSummary();
        }
        _dashboard?.ApplyMode();
        _plan?.RefreshView();
    }

    /// <summary>
    /// Navega até a seção (chave "analise" abre o Resumo), espera o layout, rola até o achado de Id (ou título) informado,
    /// expande e aplica o destaque suave. No modo Simples, se a aba de destino estiver oculta, passa para o modo Detalhado.
    /// </summary>
    public void GoTo(string sectionKey, string? findingId = null)
    {
        int i = _pages.FindIndex(p => p.Keys.Contains(sectionKey));
        if (i < 0) i = _pages.FindIndex(p => p.IsSummary);
        if (i < 0) return;
        if (!IsPageVisible(_pages[i])) Ui.SetSimple(false);
        var page = _pages[i];
        Tabs.SelectedIndex = i;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => ScrollToTarget(page, findingId)));
    }

    private void ScrollToTarget(Page page, string? findingId)
    {
        if (_closed || page.Tab?.Content is not ScrollViewer sv) return;
        if (string.IsNullOrWhiteSpace(findingId))
        {
            sv.ScrollToTop();
            return;
        }
        var row = FindRow(sv, findingId);
        if (row == null) return;
        row.Expand();
        row.UpdateLayout();
        row.BringIntoView();
        // Depois do ajuste de layout da expansão, repete a rolagem e acende o destaque.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            row.BringIntoView();
            row.Flash();
        }));
    }

    private static FindingRow? FindRow(DependencyObject root, string id)
    {
        if (root is FindingRow r && r.Matches(id)) return r;
        foreach (var c in LogicalTreeHelper.GetChildren(root))
            if (c is DependencyObject d && FindRow(d, id) is { } found) return found;
        return null;
    }

    /// <summary>Fatos da última varredura concluída (ou em andamento).</summary>
    public SystemFacts? CurrentFacts => _engine?.Facts;

    /// <summary>Fatos de uma varredura COMPLETA; null enquanto uma varredura está em andamento ou antes da primeira.</summary>
    public SystemFacts? CompletedFacts => _scanning ? null : _engine?.Facts;

    /// <summary>
    /// Refaz a varredura (usado depois de aplicar/desfazer otimizações). Se já houver uma em andamento, espera ela terminar
    /// e inicia uma nova (fila de no máximo 1); a Task devolvida conclui quando a NOVA varredura termina.
    /// </summary>
    public Task RescanAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (!_scanning) return BeginScan();
        return _queued ??= RunQueuedAsync();
    }

    private async Task RunQueuedAsync()
    {
        try { await _current; } catch { /* a falha da varredura anterior não impede a nova */ }
        _queued = null;
        if (_closed) return;
        await BeginScan();
    }

    private Task BeginScan() => _current = StartScanAsync();

    /// <summary>Marca o início de uma operação de otimização/inicialização/snapshot: Reler (F5) fica desabilitado até o <see cref="EndBusy"/> correspondente.</summary>
    public void BeginBusy()
    {
        _busyCount++;
        UpdateButtons();
    }

    /// <summary>Encerra uma operação marcada por <see cref="BeginBusy"/>.</summary>
    public void EndBusy()
    {
        if (_busyCount > 0) _busyCount--;
        UpdateButtons();
    }

    /// <summary>Disparado ao fim de cada varredura completa.</summary>
    public event Action<ScanEngine>? ScanCompleted;

    private readonly List<Page> _pages = new();
    private readonly LiveSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScanEngine? _engine;
    private CancellationTokenSource? _cts;
    private Stopwatch _sw = new();
    private int _total;
    private bool _sampling;
    private bool _scanning;
    private int _busyCount;
    private Task _current = Task.CompletedTask;
    private Task? _queued;
    private bool _exporting;
    private bool _closed;
    private readonly List<IDisposable> _disposableViews = new();
    private readonly List<Action<ScanEngine>> _scanHandlers = new();
    private DashboardView? _dashboard;
    private PlanView? _plan;

    /// <summary>Abre a aba da chave interna informada (ex.: "plan", "optimizations").</summary>
    private void GoToTab(string key)
    {
        int i = _pages.FindIndex(p => p.Key == key);
        if (i >= 0) Tabs.SelectedIndex = i;
    }

    /// <summary>Calcula nota, plano e comparação fora da UI (uma vez por varredura completa) e atualiza as abas novas.</summary>
    private async Task UpdateInsightsAsync(ScanEngine engine)
    {
        try
        {
            var r = await Task.Run(() =>
            {
                var findings = engine.AllFindings().ToList();
                var score = GameScore.Compute(findings);
                var steps = ActionPlan.Build(findings);
                var avail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var st in new WinDiag.Core.Fixes.FixEngine().GetStatuses(engine.Facts))
                        if (st.Kind == WinDiag.Core.Fixes.FixStatusKind.Disponivel) avail.Add(st.Fix.Id);
                }
                catch { /* sem o estado das otimizações, o botão "Aplicar" simplesmente não aparece */ }
                WinDiag.Core.History.ScanDiff? diff = null;
                try { diff = WinDiag.Core.History.ScanHistory.RecordAndCompare(engine); }
                catch { /* histórico indisponível: tratado como primeira leitura */ }
                return (score, steps, avail, diff);
            });
            if (_closed || engine != _engine) return;
            _dashboard?.Update(engine, r.score, r.steps, r.diff);
            _plan?.Update(r.steps, r.avail);
        }
        catch { /* as abas novas nunca derrubam o app */ }
    }

    /// <summary>Cria a janela já com o tema aplicado (barra de título, ícone e cores) antes de ser exibida.</summary>
    public MainWindow()
    {
        InitializeComponent();
        PublishSharedHub(); // antes de qualquer varredura: a coleta reaproveita o hub em vez de abrir outro Computer
        Prefs.SaveBlocked += OnPrefsSaveBlocked;
        ApplyTexts();
        ThemeManager.Register(this);
        _total = ScanEngine.SectionCount;
        BuildPages();
        AddSettingsPage();
        _modeSeg = new Segmented(ModeLabels, Ui.Simple ? 0 : 1, i => Ui.SetSimple(i == 0));
        ModeHost.Content = _modeSeg;
        ApplyModeToTabs();
        Ui.ModeChanged += OnModeChanged;
        Action<ScanEngine> insights = e => _ = UpdateInsightsAsync(e);
        ScanCompleted += insights;
        _scanHandlers.Add(insights);
        // Ao fim da varredura, uma leitura de sensores (se a aba Sensores não leu há pouco) alimenta a temperatura da Visão geral.
        Action<ScanEngine> sensors = e =>
        {
            if (DateTime.UtcNow - _lastSensorsAt > TimeSpan.FromSeconds(15)) _ = ReadSensorsAsync();
        };
        ScanCompleted += sensors;
        _scanHandlers.Add(sensors);
        VersionText.Text = "v" + AppInfo.Version;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>Aviso discreto quando as preferências não puderam ser lidas e, por isso, não foram gravadas.</summary>
    private void OnPrefsSaveBlocked()
    {
        void Show() => StatusText.Text = Loc.T("Não foi possível ler as preferências; alterações não foram salvas.",
                                               "Could not read preferences; changes were not saved.");
        if (Dispatcher.CheckAccess()) Show();
        else Dispatcher.BeginInvoke((Action)Show);
    }

    /// <summary>Define os textos fixos da barra superior, dica e nomes de acessibilidade no idioma atual.</summary>
    private void ApplyTexts()
    {
        SubtitleText.Text = Loc.T("Diagnóstico de hardware e sistema", "Hardware and system diagnostics");
        StatusText.Text = Loc.T("Iniciando...", "Starting...");
        ExportButton.Content = Loc.T("Exportar relatório", "Export report");
        ExportButton.ToolTip = Loc.T("Salva o relatório (HTML ou texto) no local escolhido, com usuário e números de série mascarados (Ctrl+S)", "Saves the report (HTML or text) to the chosen location, with the user name and serial numbers masked (Ctrl+S)");
        System.Windows.Automation.AutomationProperties.SetName(ExportButton, Loc.T("Exportar relatório", "Export report"));
        ReloadButton.Content = Loc.T("Reler", "Rescan");
        ReloadButton.ToolTip = Loc.T("Refaz a varredura (F5)", "Runs the scan again (F5)");
        System.Windows.Automation.AutomationProperties.SetName(ReloadButton, Loc.T("Reler", "Rescan"));
        ThemeButton.ToolTip = Loc.T("Tema", "Theme");
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, Loc.T("Tema", "Theme"));
        AboutButton.ToolTip = Loc.T("Sobre o WinDiag", "About WinDiag");
        System.Windows.Automation.AutomationProperties.SetName(AboutButton, Loc.T("Sobre", "About"));
        System.Windows.Automation.AutomationProperties.SetName(ModeHost, Loc.T("Modo de exibição", "Display mode"));
        System.Windows.Automation.AutomationProperties.SetName(Tabs, Loc.T("Seções", "Sections"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861", Justification = "Executado uma única vez na criação da janela.")]
    private void BuildPages()
    {
        void Add(string icon, string key, string ptLabel, string enLabel, string[] keys, bool summary = false, bool placeholder = false, LiveCard? live = null, Func<FrameworkElement>? view = null)
        {
            var p = new Page { Icon = icon, Key = key, PtLabel = ptLabel, Label = Loc.T(ptLabel, enLabel), Keys = keys, IsSummary = summary, IsPlaceholder = placeholder || view != null, Live = live, View = view };
            _pages.Add(p);
            p.Tab = CreateTab(p);
            Tabs.Items.Add(p.Tab);
        }

        Add("", "overview", "Visão geral", "Overview", Array.Empty<string>(), view: () => _dashboard = new DashboardView(GoToTab));

        Add("", "summary", "Resumo", "Summary", Array.Empty<string>(), summary: true);
        Add("", "plan", "Plano de ação", "Action plan", Array.Empty<string>(), view: () =>
        {
            _plan = new PlanView(GoToTab);
            _plan.ProgressChanged += () => _dashboard?.RefreshNext();
            return _plan;
        });
        Add("", "cpu", "CPU", "CPU", new[] { "cpu" }, live: new LiveCard(
            (Loc.T("Carga", "Load"), s => LiveCard.N(s.CpuLoadPct, " %", "0.0")),
            (Loc.T("Clock efetivo", "Effective clock"), s => LiveCard.N(s.CpuEffMHz, " MHz")),
            (Loc.T("Clock por thread", "Clock per thread"), s => s.CpuThreadMHz is { Length: > 0 } t ? string.Join("  ", t.Select(x => x.ToString("0", Pt))) + " MHz" : Fmt.NA)));
        Add("", "board", "Placa-mãe", "Motherboard", new[] { "sistema" });
        Add("", "memory", "Memória", "Memory", new[] { "memoria" }, live: new LiveCard(
            (Loc.T("RAM disponível", "Available RAM"), s => LiveCard.N(s.RamAvailGB, " GB", "0.0")),
            (Loc.T("RAM em uso", "RAM in use"), s => LiveCard.N(s.RamUsedPct, " %", "0")),
            ("Hard faults/s", s => LiveCard.N(s.HardFaultsPerSec, "", "0"))));
        Add("", "gpu", "Placa de vídeo", "Graphics card", new[] { "gpu" }, live: new LiveCard(
            (Loc.T("Temperatura", "Temperature"), s => LiveCard.N(s.GpuTempC, " °C")),
            (Loc.T("Clock do núcleo", "Core clock"), s => LiveCard.N(s.GpuCoreMHz, " MHz")),
            (Loc.T("Clock da memória", "Memory clock"), s => LiveCard.N(s.GpuMemMHz, " MHz")),
            (Loc.T("Potência", "Power"), s => LiveCard.N(s.GpuPowerW, " W", "0.0")),
            (Loc.T("Uso", "Usage"), s => LiveCard.N(s.GpuUtilPct, " %")),
            (Loc.T("VRAM usada", "VRAM used"), s => LiveCard.N(s.VramUsedMiB, " MiB")),
            (Loc.T("Ventoinha", "Fan"), s => LiveCard.N(s.GpuFanPct, " %")),
            (Loc.T("Estado P", "P-state"), s => s.GpuPState ?? Fmt.NA),
            (Loc.T("Motivo de limitação", "Throttle reason"), s => s.GpuThrottle ?? Fmt.NA),
            ("PCIe", s => s.PcieGen.HasValue && s.PcieWidth.HasValue ? $"Gen{s.PcieGen} x{s.PcieWidth}" : Fmt.NA)));
        Add("", "sensors", "Sensores", "Sensors", new[] { "sensores" }, view: TrackView(() => new SensorsView(ReadSensorsAsync)));
        Add("", "displays", "Monitores", "Displays", new[] { "monitores" });
        Add("", "storage", "Armazenamento", "Storage", new[] { "armazenamento" });
        Add("", "system", "Sistema e segurança", "System and security", new[] { "seguranca", "processos", "eventos" });
        Add("", "optimizations", "Otimizações", "Optimizations", Array.Empty<string>(), view: () =>
        {
            var v = new OptimizationsView(() => CompletedFacts, RescanAsync, BeginBusy, EndBusy);
            Action<ScanEngine> h = _ => v.Refresh();
            ScanCompleted += h;
            _scanHandlers.Add(h);
            return v;
        });
        Add("", "session", "Sessão de jogo", "Game session", Array.Empty<string>(), view: TrackView(() => new GameSessionView()));
        Add("", "capture", "Captura", "Capture", Array.Empty<string>(), view: TrackView(() => new CaptureView(() => CompletedFacts?.GpuVramMiB)));
    }

    /// <summary>Adiciona a aba "Configurações" (sempre a última).</summary>
    private void AddSettingsPage()
    {
        var p = new Page { Icon = "", Key = "settings", PtLabel = "Configurações", Label = Loc.T("Configurações", "Settings"), IsPlaceholder = true, View = TrackView(() => new SettingsView(RescanAsync, GetSensorStatusAsync)) };
        _pages.Add(p);
        p.Tab = CreateTab(p);
        Tabs.Items.Add(p.Tab);
    }

    /// <summary>Envolve a criação de uma aba para que, se ela for descartável, seja descartada no fechamento da janela.</summary>
    private Func<FrameworkElement> TrackView(Func<FrameworkElement> create) => () =>
    {
        var v = create();
        if (v is IDisposable d) _disposableViews.Add(d);
        return v;
    };

    private TabItem CreateTab(Page p)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = p.Icon,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            Width = 26,
            Foreground = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        });
        ((TextBlock)header.Children[0]).SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(TabItem), 1) });
        var label = new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
        label.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(TabItem), 1) });
        header.Children.Add(label);

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        root.Children.Add(Ui.WithInfo(new TextBlock { Text = p.Label, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) }, p.PtLabel));

        if (p.View != null)
        {
            try { root.Children.Add(p.View()); }
            catch (Exception ex) { root.Children.Add(Ui.Muted(Loc.T("Falha ao abrir esta aba: ", "Failed to open this tab: ") + ex.Message)); }
        }
        else if (p.IsPlaceholder)
        {
            root.Children.Add(Ui.Card(Ui.Empty(Loc.T("Em construção.", "Under construction."))));
        }
        else
        {
            p.Loading.Children.Add(Ui.Muted(Loc.T("Lendo...", "Reading..."), 13));
            p.Loading.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 6, 0, 12), MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left });
            root.Children.Add(p.Loading);
            if (p.Live != null) root.Children.Add(p.Live);
            root.Children.Add(p.Body);
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        SmoothScroll.Attach(scroll);
        var tab = new TabItem { Header = header, Content = scroll };
        System.Windows.Automation.AutomationProperties.SetName(tab, p.Label);
        return tab;
    }

    // ---------- Varredura ----------

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        _ = BeginScan();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => Dispose();

    /// <summary>Para o timer e a varredura, descarta as abas descartáveis (ex.: captura) e o amostrador ao vivo.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Ui.ModeChanged -= OnModeChanged;
        Prefs.SaveBlocked -= OnPrefsSaveBlocked;
        _timer.Stop();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var h in _scanHandlers) ScanCompleted -= h;
        foreach (var d in _disposableViews)
        {
            try { d.Dispose(); } catch { /* fechamento nunca deve falhar */ }
        }
        try { _sampler.Dispose(); } catch { }
        DisposeHub(final: true);
        GC.SuppressFinalize(this);
    }

    // ---------- Sensores (um único SensorHub compartilhado) ----------

    private readonly object _hubLock = new();
    private Task<SensorHub?>? _hubTask;
    private int _sensorBusy;
    private DateTime _lastSensorsAt = DateTime.MinValue;

    private volatile SensorHub? _hubCurrent;   // hub vivo e utilizável (também lido pela varredura via SharedHub)
    private volatile bool _hubDisposed;        // true após o fechamento: nenhum hub novo é criado

    /// <summary>Publica o hub compartilhado para o coletor de sensores da varredura (antes da primeira varredura).</summary>
    private void PublishSharedHub()
    {
        SensorsCollector.SharedHubLock = _hubLock;
        SensorsCollector.SharedHub = () => _hubCurrent;
    }

    /// <summary>Cria o hub sob demanda, fora da UI. Depois do fechamento não cria nada.</summary>
    private Task<SensorHub?> GetHubAsync()
    {
        if (_hubDisposed) return Task.FromResult<SensorHub?>(null);
        return _hubTask ??= Task.Run(() =>
        {
            try
            {
                var h = new SensorHub();
                if (_hubDisposed) { DisposeHubInstance(h, 3); return null; }
                _hubCurrent = h;
                return (SensorHub?)h;
            }
            catch { return null; }
        });
    }

    /// <summary>Descarta o hub, esperando uma leitura em andamento por até <paramref name="waitSeconds"/> s e descartando mesmo assim.</summary>
    private void DisposeHubInstance(SensorHub hub, int waitSeconds)
    {
        if (ReferenceEquals(_hubCurrent, hub)) _hubCurrent = null; // leituras seguintes não usam este hub
        bool got = false;
        try
        {
            Monitor.TryEnter(_hubLock, TimeSpan.FromSeconds(waitSeconds), ref got);
            hub.Dispose();
        }
        catch { /* fechamento nunca deve falhar */ }
        finally { if (got) Monitor.Exit(_hubLock); }
    }

    /// <summary>Descarta o hub (se existir ou se ainda estiver sendo criado). Com <paramref name="final"/>, impede novas criações.</summary>
    private void DisposeHub(bool final = false)
    {
        if (final)
        {
            _hubDisposed = true;
            SensorsCollector.SharedHub = null;
            SensorsCollector.SharedHubLock = null;
        }
        var t = _hubTask;
        _hubTask = null;
        if (t == null) return;
        if (!t.IsCompleted)
        {
            // Criação em andamento: descarta o resultado quando terminar.
            t.ContinueWith(x =>
            {
                if (x.IsCompletedSuccessfully && x.Result is { } h) DisposeHubInstance(h, 3);
            }, TaskScheduler.Default);
            return;
        }
        if (t.IsCompletedSuccessfully && t.Result is { } hub) DisposeHubInstance(hub, 3);
    }

    /// <summary>
    /// Lê os sensores (uma leitura por vez; devolve null se já houver uma em andamento ou se o hub falhar).
    /// Também atualiza a temperatura nos cartões da Visão geral.
    /// </summary>
    public async Task<SensorSnapshot?> ReadSensorsAsync()
    {
        if (_closed || Interlocked.CompareExchange(ref _sensorBusy, 1, 0) != 0) return null;
        try
        {
            var hub = await GetHubAsync();
            if (hub == null || _closed) return null;
            var snap = await Task.Run(() =>
            {
                lock (_hubLock)
                {
                    if (!ReferenceEquals(_hubCurrent, hub)) return null; // descartado enquanto esperava o lock
                    try { return hub.Read(); }
                    catch { return null; }
                }
            });
            if (snap == null || _closed) return null;
            _lastSensorsAt = DateTime.UtcNow;
            var (cpu, gpu) = SensorsCollector.PickMaxTemps(snap.Readings);
            _dashboard?.SetTemps(cpu, gpu);
            return snap;
        }
        catch { return null; }
        finally { Interlocked.Exchange(ref _sensorBusy, 0); }
    }

    /// <summary>Estado das fontes de sensores; com <paramref name="recheck"/> recria o hub para detectar de novo.</summary>
    public async Task<SensorStatus?> GetSensorStatusAsync(bool recheck)
    {
        if (_closed) return null;
        try
        {
            if (recheck)
            {
                // Espera uma leitura em andamento terminar antes de recriar o hub.
                for (int i = 0; i < 30 && Volatile.Read(ref _sensorBusy) != 0; i++) await Task.Delay(100);
                await Task.Run(() => DisposeHub());
            }
            var hub = await GetHubAsync();
            return hub?.Status;
        }
        catch { return null; }
    }

    /// <summary>Atalhos: F5 = Reler, Ctrl+S = Exportar (somente quando o botão correspondente está habilitado).</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F5 && ReloadButton.IsEnabled)
        {
            e.Handled = true;
            _ = BeginScan();
        }
        else if (e.Key == System.Windows.Input.Key.S && Keyboard.Modifiers == ModifierKeys.Control && ExportButton.IsEnabled)
        {
            e.Handled = true;
            Export_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Sincroniza os botões com o estado: Reler só fora de varredura; Exportar só com varredura concluída e sem exportação em curso.</summary>
    private void UpdateButtons()
    {
        ReloadButton.IsEnabled = !_scanning && _busyCount == 0;
        ExportButton.IsEnabled = !_scanning && !_exporting && _engine != null;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!_scanning && _busyCount == 0) _ = BeginScan();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var w = new AboutWindow { Owner = this };
        w.ShowDialog();
    }

    /// <summary>Abre o menu de tema (Automático, Claro, Escuro) com a opção atual marcada.</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = PlacementMode.Bottom };
        foreach (var (mode, label) in new[] { (ThemeMode.Auto, Loc.T("Automático (sistema)", "Automatic (system)")), (ThemeMode.Light, Loc.T("Claro", "Light")), (ThemeMode.Dark, Loc.T("Escuro", "Dark")) })
        {
            var item = new MenuItem { Header = label, IsChecked = ThemeManager.Mode == mode };
            var m = mode;
            item.Click += (_, _) => ThemeManager.SetMode(m);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var engine = _engine;
        if (engine == null || _scanning || _exporting) return;

        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Exportar relatório", "Export report"),
            Filter = Loc.T("Relatório HTML (*.html)|*.html|Texto (*.txt)|*.txt", "HTML report (*.html)|*.html|Text (*.txt)|*.txt"),
            FilterIndex = 1,
            DefaultExt = ".html",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = WinDiag.Core.Report.ReportWriter.DefaultFileName(".html"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FileName;
        bool txt = string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);
        _exporting = true;
        UpdateButtons();
        try
        {
            await Task.Run(() =>
            {
                if (txt) WinDiag.Core.Report.ReportWriter.WriteTxt(engine, path);
                else WinDiag.Core.Report.ReportWriter.WriteHtml(engine, path);
            });
            StatusText.Text = Loc.T("Relatório salvo", "Report saved");
            if (MessageBox.Show(this, Loc.T("Relatório salvo em:\n", "Report saved to:\n") + path + Loc.T("\n\nAbrir agora?", "\n\nOpen now?"), "WinDiag",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Não foi possível salvar o relatório: ", "Could not save the report: ") + ex.Message, "WinDiag", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _exporting = false;
            UpdateButtons();
        }
    }

    private async Task StartScanAsync()
    {
        // Não há sobreposição: RescanAsync espera a varredura vigente. O cancelamento só ocorre no fechamento da janela.
        var cts = new CancellationTokenSource();
        _cts = cts;
        var engine = new ScanEngine();
        _engine = engine;
        _sw = Stopwatch.StartNew();
        _scanning = true;
        UpdateButtons();

        foreach (var p in _pages.Where(x => !x.IsPlaceholder))
        {
            p.Body.Children.Clear();
            p.Loading.Visibility = Visibility.Visible;
        }
        RefreshSummary();
        UpdateStatus(engine);

        engine.SectionReady += r => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || engine != _engine) return;
            OnSection(engine, r);
        });

        try { await engine.RunAsync(cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (engine == _engine) StatusText.Text = Loc.T("Falha na leitura: ", "Scan failed: ") + ex.Message;
        }

        if (_closed || engine != _engine || cts.IsCancellationRequested) return;
        _sw.Stop();
        _scanning = false;
        UpdateButtons();
        // Garante a ordem canônica e o estado final.
        foreach (var p in _pages.Where(x => !x.IsPlaceholder && !x.IsSummary)) RefreshPage(engine, p);
        RefreshSummary();
        UpdateStatus(engine, finished: true);
        ScanCompleted?.Invoke(engine);
    }

    private void OnSection(ScanEngine engine, SectionResult r)
    {
        foreach (var p in _pages.Where(x => x.Keys.Contains(r.Key))) RefreshPage(engine, p);
        RefreshSummary();
        UpdateStatus(engine);
    }

    private static List<SectionResult> Snapshot(ScanEngine engine)
    {
        lock (engine.Results) return engine.Results.ToList();
    }

    private static void RefreshPage(ScanEngine engine, Page p)
    {
        var results = Snapshot(engine);
        p.Body.Children.Clear();
        int have = 0;
        foreach (var key in p.Keys)
        {
            var r = results.FirstOrDefault(x => x.Key == key);
            if (r == null) continue;
            have++;
            try { p.Body.Children.Add(new SectionView(r, p.Keys.Length > 1)); }
            catch (Exception ex) { p.Body.Children.Add(Ui.Muted(Loc.T($"{r.Title}: falha ao exibir ({ex.Message})", $"{r.Title}: failed to display ({ex.Message})"))); }
        }
        p.Loading.Visibility = have >= p.Keys.Length ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStatus(ScanEngine engine, bool finished = false)
    {
        int done = Snapshot(engine).Count;
        // Uma seção despachada tarde não pode sobrescrever o estado "concluída".
        if (!finished && !_sw.IsRunning && done >= _total) finished = true;
        StatusText.Text = finished
            ? Loc.T($"Leitura concluída em {_sw.Elapsed.TotalSeconds.ToString("0.0", Pt)} s", $"Scan finished in {_sw.Elapsed.TotalSeconds.ToString("0.0", Pt)} s")
            : Loc.T($"Lendo {done} de {_total} seções...", $"Reading {done} of {_total} sections...");
        if (engine.Facts.IsAdmin)
        {
            AdminText.Text = Loc.T("Administrador", "Administrator");
            AdminText.Foreground = Ui.Res("OkBrush");
        }
        else
        {
            AdminText.Text = Loc.T("Usuário padrão - leitura parcial", "Standard user - partial read");
            AdminText.Foreground = Ui.Res("WarnBrush");
        }
    }

    // ---------- Resumo ----------

    private void RefreshSummary()
    {
        var p = _pages.First(x => x.IsSummary);
        p.Body.Children.Clear();
        var all = _engine?.AllFindings().ToList() ?? new List<Finding>();
        var done = _engine == null ? 0 : Snapshot(_engine).Count;
        p.Loading.Visibility = done >= _total ? Visibility.Collapsed : Visibility.Visible;

        p.Body.Children.Add(BuildAnalysisCard());

        var (ok, info, aviso, atencao) = Bottlenecks.Count(all);
        var counters = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
        counters.Children.Add(Counter("OK", ok, "OkBrush"));
        counters.Children.Add(Counter("INFO", info, "InfoBrush"));
        counters.Children.Add(Counter(Loc.T("AVISO", "WARNING"), aviso, "WarnBrush"));
        counters.Children.Add(Counter(Loc.T("ATENÇÃO", "ATTENTION"), atencao, "BadBrush"));
        p.Body.Children.Add(counters);

        var top = Bottlenecks.Top(all);
        var topBody = new StackPanel();
        topBody.Children.Add(Ui.CardTitle(Loc.T("Gargalos prováveis para jogos", "Likely bottlenecks for games")));
        if (top.Count == 0)
            topBody.Children.Add(Ui.Muted(done >= _total ? Loc.T("Nenhum gargalo com impacto medido foi encontrado.", "No bottleneck with measured impact was found.") : Loc.T("Aguardando leitura...", "Waiting for the scan...")));
        for (int i = 0; i < top.Count; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = (i + 1) + ".", FontWeight = FontWeights.Bold, Foreground = Ui.Res("AccentBrush"), FontSize = 15 });
            var item = Ui.FindingItem(top[i], expanded: true);
            Grid.SetColumn(item, 1);
            row.Children.Add(item);
            topBody.Children.Add(row);
        }
        p.Body.Children.Add(Ui.Card(topBody));

        var rest = all.Where(f => f.Sev == Severity.Atencao).Concat(all.Where(f => f.Sev == Severity.Aviso)).Concat(all.Where(f => f.Sev == Severity.Info)).ToList();
        var listBody = new StackPanel();
        listBody.Children.Add(Ui.CardTitle(Loc.T("Todas as verificações", "All checks")));
        if (rest.Count == 0) listBody.Children.Add(Ui.Empty(Loc.T("Sem verificações a exibir.", "No checks to display.")));
        else listBody.Children.Add(Ui.FindingList(rest));
        p.Body.Children.Add(Ui.Card(listBody));
    }

    /// <summary>Cartão em destaque com a seção "analise" (grupos e verificações), ou aviso de andamento.</summary>
    private Border BuildAnalysisCard()
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = Loc.T("Análise da plataforma", "Platform analysis"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var r = _engine == null ? null : Snapshot(_engine).FirstOrDefault(x => x.Key == "analise");
        if (r == null) box.Children.Add(Ui.Muted(Loc.T("Analisando a plataforma...", "Analyzing the platform..."), 13));
        else
        {
            try { box.Children.Add(new SectionView(r, false)); }
            catch (Exception ex) { box.Children.Add(Ui.Muted(Loc.T("Falha ao exibir a análise (", "Failed to display the analysis (") + ex.Message + ")")); }
        }
        var card = new Border { Child = box, Padding = new Thickness(16, 12, 16, 4), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        return card;
    }

    private static Border Counter(string label, int n, string brushKey)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = n.ToString(Pt), FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Ui.Res(brushKey), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, Foreground = Ui.Res("MutedBrush"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var b = Ui.Card(sp);
        b.Margin = new Thickness(0, 0, 12, 0);
        return b;
    }

    // ---------- Ao vivo ----------

    private async Task TickAsync()
    {
        if (_sampling || _closed) return; // amostra anterior ainda em andamento: pula o tick
        _sampling = true;
        try
        {
            var s = await Task.Run(() => _sampler.Sample());
            if (_closed) return;
            foreach (var p in _pages) p.Live?.Update(s);
        }
        catch { /* leitura ao vivo nunca derruba o app */ }
        finally { _sampling = false; }
    }
}
