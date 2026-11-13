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
    /// <summary>Cultura do idioma atual para formatar números.</summary>
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Uma aba: ícone, rótulo, chaves de seção exibidas e conteúdo.</summary>
    private sealed class Page
    {
        /// <summary>Ícone (Segoe MDL2), rótulo traduzido, chave interna estável e rótulo em português (chave do glossário).</summary>
        public string Icon = "", Label = "", Key = "", PtLabel = "";
        /// <summary>Chaves das seções de dados exibidas nesta aba.</summary>
        public string[] Keys = Array.Empty<string>();
        /// <summary>Aba de Resumo (recalculada a cada seção) / aba de controle próprio, sem seções de dados.</summary>
        public bool IsSummary, IsPlaceholder;
        /// <summary>Indicador "Lendo..." mostrado até todas as seções da aba chegarem.</summary>
        public StackPanel Loading = new();
        /// <summary>Conteúdo das seções (recriado a cada atualização).</summary>
        public StackPanel Body = new();
        /// <summary>Cartão "Ao vivo" opcional, atualizado pelo timer de 1 s.</summary>
        public LiveCard? Live;
        /// <summary>Aba com controle próprio (Otimizações, Sessão de jogo, Captura).</summary>
        public Func<FrameworkElement>? View;
        /// <summary>Item de aba criado para esta página (usado para ocultar por modo).</summary>
        public TabItem? Tab;
    }

    /// <summary>Abas visíveis no modo Simples (as demais aparecem só no modo Detalhado), identificadas pela chave interna estável.</summary>
    private static readonly HashSet<string> SimpleTabs = new()
    {
        "overview", "plan", "optimizations", "tools", "session", "sensors", "work", "settings"
    };

    /// <summary>Rótulos do seletor Simples/Detalhado do cabeçalho.</summary>
    private static readonly string[] ModeLabels = { Loc.T("Simples", "Simple"), Loc.T("Detalhado", "Detailed") };
    /// <summary>Seletor do cabeçalho; sincronizado em <see cref="OnModeChanged"/> quando o modo muda por outra tela.</summary>
    private Segmented? _modeSeg;

    /// <summary>true se o perfil ativo oculta a aba (Sessão de jogo e Captura no Trabalho; Trabalho fora do perfil Trabalho).</summary>
    private static bool IsHiddenByProfile(Page p) => p.Key switch
    {
        "session" or "capture" => ProfileUi.IsWork,
        "work" => !ProfileUi.IsWork,
        _ => false
    };

    /// <summary>Regra única de visibilidade da aba: perfil primeiro, depois o modo Simples/Detalhado.</summary>
    private static bool IsPageVisible(Page p) => !IsHiddenByProfile(p) && (!Ui.Simple || SimpleTabs.Contains(p.Key));

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
        _work?.Refresh();
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
        if (IsHiddenByProfile(_pages[i]))
        {
            i = _pages.FindIndex(p => p.IsSummary);
            if (i < 0) return;
        }
        if (!IsPageVisible(_pages[i])) Ui.SetSimple(false);
        var page = _pages[i];
        Tabs.SelectedIndex = i;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => ScrollToTarget(page, findingId)));
    }

    /// <summary>Rola até o achado (ou ao topo) depois que a aba destino fez o layout; roda em ContextIdle para o BringIntoView ter medidas válidas.</summary>
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

    /// <summary>Procura, na árvore lógica, a linha de achado que corresponde ao id ou título.</summary>
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

    /// <summary>Espera a varredura em curso e dispara uma nova, evitando varreduras simultâneas (fila de no máximo 1).</summary>
    private async Task RunQueuedAsync()
    {
        try { await _current; } catch { /* a falha da varredura anterior não impede a nova */ }
        _queued = null;
        if (_closed) return;
        await BeginScan();
    }

    /// <summary>Inicia a varredura e guarda a Task, para que a fila saiba quando ela termina.</summary>
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

    /// <summary>Abas na ordem de exibição.</summary>
    private readonly List<Page> _pages = new();
    /// <summary>Amostrador dos contadores ao vivo (CPU, memória, GPU); descartado no fechamento.</summary>
    private readonly LiveSampler _sampler = new();
    /// <summary>Timer de 1 s que dispara a amostragem ao vivo.</summary>
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>Varredura mais recente, de onde as abas leem resultados e fatos.</summary>
    private ScanEngine? _engine;
    /// <summary>Cancela a varredura em curso no fechamento da janela.</summary>
    private CancellationTokenSource? _cts;
    /// <summary>Cronômetro da duração da leitura exibida na barra de status.</summary>
    private Stopwatch _sw = new();
    /// <summary>Quantidade de seções esperadas (usada no "Lendo N de M").</summary>
    private int _total;
    /// <summary>true enquanto uma amostra ao vivo está em curso; evita acumular leituras lentas.</summary>
    private bool _sampling;
    /// <summary>true durante a varredura (UI thread); os fatos só são "completos" quando volta a false.</summary>
    private bool _scanning;
    /// <summary>Contador de operações em curso (otimizações etc.); enquanto maior que 0, o Reler fica desabilitado.</summary>
    private int _busyCount;
    /// <summary>Task da varredura atual.</summary>
    private Task _current = Task.CompletedTask;
    /// <summary>Varredura enfileirada para depois da atual, se houver.</summary>
    private Task? _queued;
    /// <summary>true durante a exportação do relatório.</summary>
    private bool _exporting;
    /// <summary>true depois do fechamento: tarefas e eventos atrasados checam isso e saem sem tocar na interface.</summary>
    private bool _closed;
    /// <summary>Abas descartáveis (sensores, captura...), descartadas no fechamento para soltar timers e eventos.</summary>
    private readonly List<IDisposable> _disposableViews = new();
    /// <summary>Assinaturas de <see cref="ScanCompleted"/> feitas por esta janela, removidas no descarte.</summary>
    private readonly List<Action<ScanEngine>> _scanHandlers = new();
    /// <summary>Visão geral, criada sob demanda pela fábrica da aba.</summary>
    private DashboardView? _dashboard;
    /// <summary>Plano de ação, criado sob demanda.</summary>
    private PlanView? _plan;
    /// <summary>Aba Trabalho, criada sob demanda.</summary>
    private WorkView? _work;
    /// <summary>Aba Ferramentas, criada sob demanda.</summary>
    private ToolsView? _tools;

    /// <summary>Perfil dos fatos da última leitura; antes da primeira, o perfil gravado.</summary>
    private UsageProfile ActiveProfile => _engine?.Facts.Profile ?? ProfileUi.Current;

    /// <summary>Atualiza o chip do perfil ativo no cabeçalho.</summary>
    private void UpdateProfileChip()
    {
        var p = ProfileUi.Current;
        ProfileGlyph.Text = ProfileUi.Glyph(p);
        ProfileText.Text = ProfileUi.Label(p);
        string tip = Loc.T("Perfil de uso: ", "Usage profile: ") + ProfileUi.Label(p) + Loc.T(". Clique para trocar em Configurações.", ". Click to change it in Settings.");
        ProfileChip.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(ProfileChip, tip);
    }

    /// <summary>Clique no chip do perfil no cabeçalho: abre as Configurações.</summary>
    private void ProfileChip_Click(object sender, MouseButtonEventArgs e) => GoToTab("settings");

    /// <summary>Enter/Espaço no chip do perfil (acessibilidade por teclado).</summary>
    private void ProfileChip_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            GoToTab("settings");
        }
    }

    /// <summary>O perfil foi gravado: ajusta abas e chip e refaz a leitura com o novo perfil.</summary>
    private void OnProfileChanged()
    {
        if (_closed) return;
        UpdateProfileChip();
        ApplyModeToTabs();
        _ = RescanAsync();
    }

    /// <summary>Primeira abertura: pergunta para que o PC é usado; fechar sem escolher mantém o padrão e pergunta de novo na próxima vez.</summary>
    private void AskProfile()
    {
        if (_closed) return;
        var chosen = ProfileChooserWindow.Ask(this);
        if (chosen.HasValue) ProfileUi.Set(chosen.Value);
    }

    /// <summary>Abre a aba da chave interna informada (ex.: "plan", "optimizations").</summary>
    public void OpenTab(string key) => GoToTab(key);

    /// <summary>Abre a aba Ferramentas e destaca o cartão da ferramenta de id informado.</summary>
    public void GoToTool(string toolId)
    {
        GoToTab("tools");
        _tools?.FocusTool(toolId);
    }

    /// <summary>Abre a aba da chave interna informada (uso interno).</summary>
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
            // Em thread de fundo (Task.Run): GameScore, ActionPlan e o histórico fazem cálculo e E/S; a UI só recebe o resultado pronto.
            var r = await Task.Run(() =>
            {
                // Descarta o resultado se uma varredura mais nova já substituiu esta.
                var findings = engine.AllFindings().ToList();
                var profile = engine.Facts.Profile;
                var score = GameScore.Compute(findings, profile, engine.WorkReadingsPartial);
                var steps = ActionPlan.Build(findings, profile);
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
        UpdateProfileChip();
        ApplyModeToTabs();
        Ui.ModeChanged += OnModeChanged;
        ProfileUi.Changed += OnProfileChanged;
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
        void Show() => StatusText.Text = Loc.T("Não foi possível gravar as preferências; o perfil escolhido vale só nesta sessão.",
                                               "Could not save preferences; the chosen profile applies to this session only.");
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

    /// <summary>Monta a lista de abas. Cada aba com "view" é criada sob demanda pela fábrica; as demais recebem as seções de dados da varredura.</summary>
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
        Add("", "work", "Trabalho", "Work", new[] { "trabalho" }, view: () =>
        {
            var v = new WorkView(() => CompletedFacts, () => _engine == null ? null : Snapshot(_engine).FirstOrDefault(x => x.Key == "trabalho"),
                RescanAsync, BeginBusy, EndBusy);
            _work = v;
            Action<ScanEngine> h = _ => v.Refresh();
            ScanCompleted += h;
            _scanHandlers.Add(h);
            return v;
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
        Add("\uE945", "optimizations", "Otimizações", "Optimizations", Array.Empty<string>(), view: () =>
        {
            var v = new OptimizationsView(() => CompletedFacts, RescanAsync, BeginBusy, EndBusy);
            Action<ScanEngine> h = _ => v.Refresh();
            ScanCompleted += h;
            _scanHandlers.Add(h);
            return v;
        });
        Add("\uE90F", "tools", "Ferramentas", "Tools", Array.Empty<string>(), view: () =>
        {
            var v = new ToolsView(() => CompletedFacts, RescanAsync, BeginBusy, EndBusy);
            _tools = v;
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
        var p = new Page { Icon = "", Key = "settings", PtLabel = "Configurações", Label = Loc.T("Configurações", "Settings"), IsPlaceholder = true, View = TrackView(() => new SettingsView(RescanAsync, GetSensorStatusAsync, CheckForUpdateNowAsync)) };
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

    /// <summary>Cria a aba: cabeçalho com ícone e rótulo (a cor acompanha a aba selecionada) e conteúdo rolável com rolagem suave.</summary>
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

    /// <summary>Ao carregar a janela: liga o timer ao vivo, inicia a primeira varredura e, na primeira abertura, pergunta o perfil depois que a janela terminou de aparecer.</summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        _ = BeginScan();
        StartUpdateFlow();
        if (ProfileUi.NeedsChoice) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(AskProfile));
    }

    /// <summary>Fechar a janela dispara o mesmo descarte de <see cref="Dispose"/>.</summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && Operations.IsBusy)
        {
            // Nunca fecha no meio de uma operação: os serviços que ela paralisou precisam ser religados no finally dela.
            e.Cancel = true;
            if (_closeAsking) return;
            _closeAsking = true;
            try
            {
                if (ConfirmDialog.Ask(this, "WinDiag", Loc.T("Uma operação está em andamento. Aguarde terminar ou cancele-a antes de fechar.", "An operation is in progress. Wait for it to finish or cancel it before closing."),
                        Loc.T("Em andamento: ", "In progress: ") + Operations.Current, warn: true,
                        okText: Loc.T("Cancelar a operação e fechar", "Cancel the operation and close"), cancelText: Loc.T("Aguardar", "Wait"), defaultCancel: true))
                    _ = CancelAndCloseAsync();
            }
            finally { _closeAsking = false; }
            return;
        }
        Dispose();
    }

    /// <summary>true enquanto a pergunta de fechamento está aberta (evita empilhar diálogos).</summary>
    private bool _closeAsking;
    /// <summary>true depois que a operação terminou e o fechamento foi liberado.</summary>
    private bool _forceClose;

    /// <summary>Cancela a operação em curso, espera ela terminar (seus finally religam serviços) e só então fecha a janela.</summary>
    private async Task CancelAndCloseAsync()
    {
        Operations.Cancel();
        await Operations.WaitIdleAsync(TimeSpan.FromMinutes(2));
        _forceClose = true;
        Close();
    }

    /// <summary>Para o timer e a varredura, descarta as abas descartáveis (ex.: captura) e o amostrador ao vivo.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Ui.ModeChanged -= OnModeChanged;
        ProfileUi.Changed -= OnProfileChanged;
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

    /// <summary>Trava que garante que ninguém descarte o hub enquanto uma leitura de sensores o usa (a varredura compartilha esta trava).</summary>
    private readonly object _hubLock = new();
    /// <summary>Criação assíncrona do hub; o LibreHardwareMonitor demora a abrir e não pode travar a UI.</summary>
    private Task<SensorHub?>? _hubTask;
    /// <summary>1 enquanto há leitura de sensores em curso (Interlocked); as demais chamadas devolvem null.</summary>
    private int _sensorBusy;
    /// <summary>Instante UTC da última leitura de sensores, para não repetir a leitura pós-varredura logo após uma da aba.</summary>
    private DateTime _lastSensorsAt = DateTime.MinValue;

    /// <summary>Hub vivo e utilizável (volatile: lido também pela thread da varredura via SharedHub).</summary>
    private volatile SensorHub? _hubCurrent;   // hub vivo e utilizável (também lido pela varredura via SharedHub)
    /// <summary>true após o fechamento: nenhum hub novo é criado.</summary>
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

    /// <summary>Botão Reler: só vale fora de varredura e sem operações em curso.</summary>
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!_scanning && _busyCount == 0) _ = BeginScan();
    }

    /// <summary>Abre a janela "Sobre" (modal).</summary>
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

    /// <summary>Exporta o relatório (HTML/texto) em thread de fundo; usuário e números de série são mascarados só no arquivo exportado, nunca na tela.</summary>
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
                try { WinDiag.Core.Util.Shell.OpenUnelevated(path); } catch { }
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

    /// <summary>Varredura completa: as seções chegam em outras threads e são levadas à UI thread por Dispatcher.BeginInvoke; seções atrasadas de varreduras antigas são ignoradas.</summary>
    private async Task StartScanAsync()
    {
        // Não há sobreposição: RescanAsync espera a varredura vigente. O cancelamento só ocorre no fechamento da janela.
        var cts = new CancellationTokenSource();
        _cts = cts;
        var engine = new ScanEngine { ProfileOverride = ProfileUi.OverrideForScan() };
        _engine = engine;
        _sw = Stopwatch.StartNew();
        _scanning = true;
        UpdateButtons();
        _work?.Refresh();

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

    /// <summary>Atualiza as abas que mostram a seção recém-chegada, o Resumo e a barra de status.</summary>
    private void OnSection(ScanEngine engine, SectionResult r)
    {
        foreach (var p in _pages.Where(x => !x.IsPlaceholder && x.Keys.Contains(r.Key))) RefreshPage(engine, p);
        if (r.Key == "trabalho") _work?.Refresh();
        RefreshSummary();
        UpdateStatus(engine);
    }

    /// <summary>Cópia da lista de resultados sob trava (a varredura ainda escreve nela em outra thread).</summary>
    private static List<SectionResult> Snapshot(ScanEngine engine)
    {
        lock (engine.Results) return engine.Results.ToList();
    }

    /// <summary>Reconstrói o corpo de uma aba a partir das seções disponíveis; o indicador de carregamento some quando todas chegaram.</summary>
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

    /// <summary>Atualiza "Lendo N de M"/"concluída" e o selo de administrador (sem administrador a leitura é parcial).</summary>
    private void UpdateStatus(ScanEngine engine, bool finished = false)
    {
        int done = Snapshot(engine).Count;
        // Uma seção despachada tarde não pode sobrescrever o estado "concluída".
        if (!finished && !_sw.IsRunning && done >= _total) finished = true;
        StatusText.Text = finished
            ? Loc.T($"Leitura concluída em {_sw.Elapsed.TotalSeconds.ToString("0.0", Pt)} s", $"Scan finished in {_sw.Elapsed.TotalSeconds.ToString("0.0", Pt)} s")
            : Loc.T($"Lendo {done} de {_total} seções...", $"Reading {done} of {_total} sections...");
        if (_updatedNote) StatusText.Text += " · " + Loc.T($"Atualizado para a versão {AppInfo.Version}", $"Updated to version {AppInfo.Version}");
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

    /// <summary>Reconstrói o Resumo: análise, contadores por severidade, principais gargalos e todas as verificações.</summary>
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

        var profile = ActiveProfile;
        var top = Bottlenecks.Top(all, profile, 5);
        var topBody = new StackPanel();
        topBody.Children.Add(Ui.CardTitle(profile == UsageProfile.Work ? Loc.T("Pontos de atenção prováveis para trabalho", "Likely points of attention for work") : Loc.T("Gargalos prováveis para jogos", "Likely bottlenecks for games")));
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

    /// <summary>Cartão de contagem de uma severidade.</summary>
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

    /// <summary>Amostra ao vivo a cada segundo: lê fora da UI (Task.Run) e atualiza só os cartões "Ao vivo"; sem sobreposição.</summary>
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
