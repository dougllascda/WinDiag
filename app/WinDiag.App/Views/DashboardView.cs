using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinDiag.Core;
using WinDiag.Core.History;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.App.Views;

/// <summary>Aba "Visão geral": nota de configuração, cartões de hardware, mudanças desde a última leitura e próximos passos.</summary>
public sealed class DashboardView : UserControl
{
    /// <summary>Cultura do idioma atual para formatar números.</summary>
    private static CultureInfo Pt => Loc.Culture;
    /// <summary>Diâmetro e espessura (px) do medidor circular da nota.</summary>
    private const double GaugeSize = 150, GaugeThickness = 10;

    /// <summary>Abre uma aba pela chave interna (ex.: "plan").</summary>
    private readonly Action<string> _goTo;
    /// <summary>Cartão principal: medidor da nota e áreas que perderam pontos.</summary>
    private readonly StackPanel _hero = new();
    /// <summary>Grade dos cartões de hardware; passa de 2 para 3 colunas em janelas largas (&gt;= 980 px).</summary>
    private readonly UniformGrid _grid = new() { Columns = 3 };
    /// <summary>Cartões reaproveitados entre leituras; só têm o texto atualizado.</summary>
    private readonly HwCard _cpu, _gpu, _mem, _board, _disks, _displays, _printers, _battery;
    /// <summary>Perfil da última leitura; define o título e a regra de área dos passos.</summary>
    private UsageProfile _profile = UsageProfile.Gaming;
    /// <summary>Conteúdo do cartão "Desde a última leitura".</summary>
    private readonly StackPanel _diff = new();
    /// <summary>Conteúdo do cartão "Próximos passos".</summary>
    private readonly StackPanel _next = new();
    /// <summary>Plano de ação da última leitura.</summary>
    private List<ActionStep> _plan = new();
    /// <summary>false até a primeira varredura terminar (mostra "Aguardando a leitura...").</summary>
    private bool _loaded;
    /// <summary>Conteúdo do cartão "O que está bom" (modo Simples).</summary>
    private readonly StackPanel _good = new();
    /// <summary>Conteúdo do cartão "O que melhorar e como" (modo Simples).</summary>
    private readonly StackPanel _improve = new();
    /// <summary>Cartões de nível superior; a visibilidade deles alterna entre Simples e Detalhado.</summary>
    private readonly Border _goodCard, _improveCard, _diffCard, _nextCard;
    /// <summary>Todas as verificações da última leitura (origem de "O que está bom" e da busca por área).</summary>
    private List<Finding> _findings = new();

    /// <summary>Trechos que marcam o fim do codinome real em "Codinome: X (aprox...)"; o texto depois deles é descartado.</summary>
    private static readonly string[] CodenameCuts = { " (aprox", " (approx" };

    /// <summary>Texto de espera, no idioma atual.</summary>
    private static string Waiting => Loc.T("Aguardando a leitura...", "Waiting for the scan...");

    /// <summary>Converte a chave interna da aba na chave da seção de dados.</summary>
    private static string SectionOfTab(string tabKey) => tabKey switch
    {
        "cpu" => "cpu",
        "gpu" => "gpu",
        "memory" => "memoria",
        "board" => "sistema",
        "storage" => "armazenamento",
        "displays" => "monitores",
        _ => tabKey
    };

    /// <summary>Mostra/oculta os blocos conforme o modo (Simples: nota, "O que está bom", "O que melhorar" e cartões curtos).</summary>
    private void ApplyVisibility()
    {
        bool simple = Ui.Simple;
        _goodCard.Visibility = _improveCard.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        _diffCard.Visibility = _nextCard.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Reaplica o modo Simples/Detalhado ao vivo (sem nova leitura).</summary>
    public void ApplyMode()
    {
        ApplyVisibility();
        foreach (var c in new[] { _cpu, _gpu, _mem, _board, _disks, _displays, _printers, _battery }) c.Render();
        if (_loaded) BuildSimple();
    }

    /// <summary>Acrescenta (ou remove, se null) a temperatura atual nos cartões de Processador e Placa de vídeo.</summary>
    public void SetTemps(double? cpu, double? gpu)
    {
        string? c = cpu.HasValue ? Loc.T("Temperatura: ", "Temperature: ") + cpu.Value.ToString("0", Pt) + " °C" : null;
        string? g = gpu.HasValue ? Loc.T("Temperatura: ", "Temperature: ") + gpu.Value.ToString("0", Pt) + " °C" : null;
        if (c != _cpu.ExtraLine)
        {
            _cpu.ExtraLine = c;
            _cpu.Render();
        }
        if (g != _gpu.ExtraLine)
        {
            _gpu.ExtraLine = g;
            _gpu.Render();
        }
    }

    /// <summary>Cria a aba; <paramref name="goToTab"/> abre uma aba pela chave interna (ex.: "plan"). Os cartões navegam pela seção.</summary>
    public DashboardView(Action<string> goToTab)
    {
        _goTo = goToTab;
        Action<string> goTo = key => Ui.GoTo(SectionOfTab(key));
        _cpu = new HwCard("", "Processador", "Processor", "cpu", goTo);
        _gpu = new HwCard("", "Placa de vídeo", "Graphics card", "gpu", goTo);
        _mem = new HwCard("", "Memória", "Memory", "memory", goTo);
        _board = new HwCard("", "Placa-mãe", "Motherboard", "board", goTo);
        _disks = new HwCard("", "Armazenamento", "Storage", "storage", goTo);
        _displays = new HwCard("", "Monitores", "Displays", "displays", goTo);
        _printers = new HwCard("", "Impressoras", "Printers", "trabalho", goTo);
        _battery = new HwCard("", "Bateria", "Battery", "trabalho", goTo);
        _printers.Visibility = _battery.Visibility = Visibility.Collapsed; // só no perfil Trabalho (definido ao fim da leitura)
        foreach (var c in new[] { _cpu, _gpu, _mem, _board, _disks, _displays, _printers, _battery }) _grid.Children.Add(c);

        var heroCard = Ui.Card(_hero);
        _hero.Children.Add(Ui.Empty(Waiting));
        _diff.Children.Add(Ui.Empty(Waiting));
        _next.Children.Add(Ui.Empty(Waiting));
        _good.Children.Add(Ui.Empty(Waiting));
        _improve.Children.Add(Ui.Empty(Waiting));
        _goodCard = Ui.Card(_good);
        _improveCard = Ui.Card(_improve);
        _diffCard = Ui.Card(_diff);
        _nextCard = Ui.Card(_next);

        var root = new StackPanel();
        root.Children.Add(heroCard);
        root.Children.Add(_goodCard);
        root.Children.Add(_improveCard);
        root.Children.Add(_grid);
        root.Children.Add(_diffCard);
        root.Children.Add(_nextCard);
        Content = root;
        ApplyVisibility();

        SizeChanged += (_, e) =>
        {
            int cols = e.NewSize.Width >= 980 ? 3 : 2;
            if (_grid.Columns != cols) _grid.Columns = cols;
        };
    }

    /// <summary>Atualiza a aba com a varredura concluída (thread de UI). <paramref name="diff"/> é null na primeira leitura registrada.</summary>
    public void Update(ScanEngine engine, ScoreResult score, List<ActionStep> plan, ScanDiff? diff)
    {
        _plan = plan;
        _loaded = true;
        _profile = engine.Facts.Profile;
        try { BuildHero(score); } catch (Exception ex) { Fail(_hero, ex); }
        try { FillCards(engine); } catch { /* cartões mantêm o conteúdo anterior */ }
        try { BuildDiff(diff); } catch (Exception ex) { Fail(_diff, ex); }
        try { _findings = engine.AllFindings().ToList(); } catch { _findings = new List<Finding>(); }
        RefreshNext();
        BuildSimple();
    }

    /// <summary>Troca o conteúdo de um bloco por uma nota de falha: um bloco com erro não derruba a aba inteira.</summary>
    private static void Fail(Panel p, Exception ex)
    {
        p.Children.Clear();
        p.Children.Add(Ui.Muted(Loc.T("Falha ao exibir este bloco: ", "Failed to display this block: ") + ex.Message));
    }

    // ---------- Hero ----------

    /// <summary>Monta o cartão principal: medidor, conceito e quanto cada área custou à nota.</summary>
    private void BuildHero(ScoreResult score)
    {
        _hero.Children.Clear();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var heading = UiExtra.Text(_profile == UsageProfile.Work ? Loc.T("Configuração para trabalho", "Configuration for work") : Loc.T("Configuração para jogos", "Configuration for gaming"), "TextBrush", 13, FontWeights.SemiBold);
        heading.TextAlignment = TextAlignment.Center;
        heading.MaxWidth = 210;
        heading.Margin = new Thickness(0, 0, 0, 8);
        left.Children.Add(heading);
        left.Children.Add(Gauge(score.Score));
        var grade = UiExtra.Text(score.Grade, BrushFor(score.Score), 16, FontWeights.SemiBold);
        grade.HorizontalAlignment = HorizontalAlignment.Center;
        grade.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(grade);
        var sum = UiExtra.Text(score.Summary, "MutedBrush", 12);
        sum.TextAlignment = TextAlignment.Center;
        sum.MaxWidth = 210;
        left.Children.Add(sum);
        g.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(Ui.CardTitleInfo(Loc.T("Onde a nota perdeu pontos", "Where the score lost points"), "Onde a nota perdeu pontos"));
        if (score.Parts.Count == 0) right.Children.Add(Ui.Muted(Loc.T("Nenhuma perda de pontos identificada.", "No loss of points identified."), 13));
        foreach (var p in score.Parts.OrderByDescending(x => x.Lost)) right.Children.Add(PartBar(p));
        if (score.Parts.Count > 0) right.Children.Add(Ui.Muted(Loc.T("Clique em uma área para ver os passos que a melhoram.", "Click an area to see the steps that improve it.")));
        var note = Ui.Muted(Loc.T("Nota de configuração, não é benchmark.", "Configuration score, not a benchmark."));
        note.Margin = new Thickness(0, 10, 0, 0);
        right.Children.Add(note);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        _hero.Children.Add(g);
    }

    /// <summary>Cor da nota: verde a partir de 90, destaque a partir de 75, aviso a partir de 55; abaixo disso, ruim.</summary>
    private static string BrushFor(int score) => score >= 90 ? "OkBrush" : score >= 75 ? "AccentBrush" : score >= 55 ? "WarnBrush" : "BadBrush";

    /// <summary>Medidor circular em arco (0 a 100), com a cor pela faixa; os pincéis do tema acompanham a troca de tema.</summary>
    private static Grid Gauge(int score)
    {
        score = Math.Clamp(score, 0, 100);
        double r = (GaugeSize - GaugeThickness) / 2, c = GaugeSize / 2;
        var canvas = new Canvas { Width = GaugeSize, Height = GaugeSize };
        var ring = new Ellipse { Width = GaugeSize - GaugeThickness, Height = GaugeSize - GaugeThickness, StrokeThickness = GaugeThickness };
        ring.SetResourceReference(Shape.StrokeProperty, "LineBrush");
        Canvas.SetLeft(ring, GaugeThickness / 2);
        Canvas.SetTop(ring, GaugeThickness / 2);
        canvas.Children.Add(ring);

        if (score > 0)
        {
            double ang = Math.Min(score / 100.0 * 360.0, 359.9) * Math.PI / 180.0;
            var end = new Point(c + r * Math.Sin(ang), c - r * Math.Cos(ang));
            var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true));
            var arc = new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { fig }),
                StrokeThickness = GaugeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            arc.SetResourceReference(Shape.StrokeProperty, BrushFor(score));
            canvas.Children.Add(arc);
        }

        var host = new Grid { Width = GaugeSize, Height = GaugeSize };
        host.Children.Add(canvas);
        var num = UiExtra.Text(score.ToString(Pt), "TextBrush", 40, FontWeights.Bold, wrap: false);
        num.HorizontalAlignment = HorizontalAlignment.Center;
        num.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(num);
        return host;
    }

    /// <summary>Barra de uma área que perdeu pontos; clicar (ou Enter/Espaço) expande os passos que a melhoram, montados só na primeira abertura.</summary>
    private StackPanel PartBar(ScorePart p)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 4) };
        var box = new StackPanel { Background = Brushes.Transparent, ToolTip = p.Reason, Cursor = Cursors.Hand, Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(box, p.Area + Loc.T(": mostrar os passos desta área", ": show the steps for this area"));
        bool built = false;
        void Toggle()
        {
            if (panel.Visibility == Visibility.Visible)
            {
                panel.Visibility = Visibility.Collapsed;
                return;
            }
            if (!built)
            {
                built = true;
                FillAreaPanel(panel, p.Area);
            }
            panel.Visibility = Visibility.Visible;
        }
        box.MouseLeftButtonUp += (_, _) => Toggle();
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                Toggle();
            }
        };
        outer.Children.Add(box);
        outer.Children.Add(panel);
        BuildBar(box, p);
        return outer;
    }

    /// <summary>Preenche o painel da nota com os passos do plano da área (título, o que fazer, ganho e botões).</summary>
    private void FillAreaPanel(StackPanel panel, string area)
    {
        // No perfil Trabalho a área é definida por achado (não por seção): resolve pela verificação de origem
        // do passo e só usa a seção quando o passo não tem verificação correspondente.
        string StepArea(ActionStep s)
        {
            var f = _findings.FirstOrDefault(x => !string.IsNullOrEmpty(s.FindingId) && x.Id == s.FindingId);
            return f is not null ? GameScore.AreaOf(f, _profile) : GameScore.AreaOf(s.SectionKey, _profile);
        }
        var steps = _plan.Where(s => StepArea(s) == area).OrderBy(s => s.Order).ToList();
        var inner = new StackPanel();
        if (steps.Count == 0) inner.Children.Add(Ui.Muted(Loc.T("Nenhum passo do plano para esta área.", "No plan steps for this area."), 12.5));
        foreach (var s in steps) inner.Children.Add(StepBlock(s, showTitle: true));
        var card = new Border { Padding = new Thickness(12, 8, 12, 6), CornerRadius = new CornerRadius(6), Child = inner };
        card.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        panel.Children.Add(card);
    }

    /// <summary>Bloco de um passo do plano: título, o que fazer, ganho esperado e botões "Como fazer na BIOS"/"Ver detalhes".</summary>
    private static StackPanel StepBlock(ActionStep s, bool showTitle)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (showTitle) sp.Children.Add(UiExtra.Text(s.Title, "TextBrush", 13.5, FontWeights.SemiBold));
        var what = Ui.Simple && !string.IsNullOrWhiteSpace(s.Simple) ? s.Simple : s.What;
        if (!string.IsNullOrWhiteSpace(what)) sp.Children.Add(UiExtra.Detail(Loc.T("O que fazer", "What to do"), what));
        if (!string.IsNullOrWhiteSpace(s.Gain)) sp.Children.Add(UiExtra.Detail(Loc.T("Ganho esperado", "Expected gain"), s.Gain));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (!string.IsNullOrWhiteSpace(s.GuideTopic))
        {
            var topic = s.GuideTopic;
            var bios = new Button { Content = Loc.T("Como fazer na BIOS", "How to do it in the BIOS"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            bios.Click += (_, _) => Ui.ShowBios(topic);
            buttons.Children.Add(bios);
        }
        if (!string.IsNullOrWhiteSpace(s.SectionKey))
        {
            var section = s.SectionKey;
            var id = s.FindingId;
            var det = new Button { Content = Loc.T("Ver detalhes", "View details"), Padding = new Thickness(12, 4, 12, 4) };
            det.Click += (_, _) => Ui.GoTo(section, string.IsNullOrWhiteSpace(id) ? null : id);
            buttons.Children.Add(det);
        }
        if (buttons.Children.Count > 0) sp.Children.Add(buttons);
        return sp;
    }

    // ---------- Modo Simples ----------

    /// <summary>Monta "O que está bom": no máximo 5 itens OK em linguagem simples, um por assunto, e depois "O que melhorar".</summary>
    private void BuildSimple()
    {
        try
        {
            _good.Children.Clear();
            _good.Children.Add(Ui.CardTitleInfo(Loc.T("O que está bom", "What is in good shape"), "O que está bom"));
            // Só itens com texto para leigos (títulos técnicos como "Fast Startup" não dizem nada a quem não conhece).
            // O mesmo assunto pode vir de duas seções (ex.: Resizable BAR na GPU e na análise): a análise tem prioridade
            // e cada assunto (último segmento do Id) aparece uma vez.
            static string Topic(Finding f)
            {
                if (string.IsNullOrEmpty(f.Id)) return f.Title;
                var t = f.Id[(f.Id.LastIndexOf('.') + 1)..];
                // "rebar_on"/"rebar_off" e "rebar" são o mesmo assunto.
                foreach (var suffix in new[] { "_on", "_off", "_ok" })
                    if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return t[..^suffix.Length];
                return t;
            }
            var good = _findings.Where(f => f.Sev == Severity.Ok && !string.IsNullOrWhiteSpace(f.Simple))
                .OrderBy(f => f.Section == "analise" ? 0 : 1)
                .GroupBy(Topic, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Take(5).ToList();
            if (good.Count == 0) _good.Children.Add(Ui.Muted(Loc.T("Nenhum item em ordem foi destacado nesta leitura.", "No item in good order was highlighted in this scan."), 13));
            foreach (var f in good)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                line.Children.Add(UiExtra.Icon("", 14, "OkBrush"));
                var t = UiExtra.Text(string.IsNullOrWhiteSpace(f.Simple) ? f.Title : f.Simple, "TextBrush", 13);
                t.Margin = new Thickness(8, 0, 0, 0);
                t.MaxWidth = 760;
                line.Children.Add(t);
                var section = f.Section;
                var id = f.Id;
                line.MouseLeftButtonUp += (_, _) => Ui.GoTo(section, string.IsNullOrWhiteSpace(id) ? f.Title : id);
                _good.Children.Add(line);
            }
        }
        catch (Exception ex) { Fail(_good, ex); }
        BuildImprove();
    }

    /// <summary>Monta "O que melhorar e como" com os 5 primeiros passos pendentes do plano.</summary>
    private void BuildImprove()
    {
        try
        {
            _improve.Children.Clear();
            _improve.Children.Add(Ui.CardTitleInfo(Loc.T("O que melhorar e como", "What to improve and how"), "O que melhorar e como"));
            var pending = _plan.Where(s => !PlanStore.IsDone(s.Key)).OrderBy(s => s.Order).Take(5).ToList();
            if (!_loaded) _improve.Children.Add(Ui.Empty(Waiting));
            else if (pending.Count == 0) _improve.Children.Add(Ui.Muted(Loc.T("Nenhum passo pendente: a configuração está em ordem.", "No pending steps: the configuration is in order."), 13));
            foreach (var s in pending)
            {
                var block = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                block.Children.Add(UiExtra.Text(s.Title, "TextBrush", 13.5, FontWeights.SemiBold));
                block.Children.Add(StepBlock(s, showTitle: false));
                _improve.Children.Add(block);
            }
            if (pending.Count > 0)
            {
                var btn = new Button { Content = Loc.T("Ver plano completo", "View full plan"), Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
                btn.Click += (_, _) => _goTo("plan");
                _improve.Children.Add(btn);
            }
        }
        catch (Exception ex) { Fail(_improve, ex); }
    }

    /// <summary>Título, valor perdido e barra proporcional (30 pontos = barra cheia); 15 ou mais usa a cor de erro.</summary>
    private static void BuildBar(StackPanel box, ScorePart p)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(UiExtra.Text(p.Area, "TextBrush", 12.5, wrap: false));
        var lost = UiExtra.Text("-" + p.Lost.ToString(Pt), p.Lost >= 15 ? "BadBrush" : "WarnBrush", 12.5, FontWeights.SemiBold, wrap: false);
        Grid.SetColumn(lost, 1);
        head.Children.Add(lost);
        box.Children.Add(head);

        double f = Math.Clamp(p.Lost / 30.0, 0.03, 1.0);
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 3, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, p.Lost >= 15 ? "BadBrush" : "WarnBrush");
        bar.Children.Add(track);
        bar.Children.Add(fill);
        box.Children.Add(bar);
    }

    // ---------- Cartões de hardware ----------

    /// <summary>Preenche os cartões de hardware com os dados da varredura; cada cartão é independente e lê só dados já coletados (nada de E/S aqui).</summary>
    private void FillCards(ScanEngine engine)
    {
        var f = engine.Facts;
        List<SectionResult> results;
        lock (engine.Results) results = engine.Results.ToList();

        // Processador
        var cpu = new List<string> { ShortCpu(f.CpuName) };
        cpu.Add(f.CpuCores > 0 ? Loc.T($"{f.CpuCores} núcleos / {f.CpuThreads} threads", $"{f.CpuCores} cores / {f.CpuThreads} threads") : Loc.T("Núcleos: n/d", "Cores: n/a"));
        var code = results.FirstOrDefault(r => r.Key == "cpu")?.Groups.SelectMany(g => g.Rows)
            .FirstOrDefault(r => r.Label.StartsWith("Codinome", StringComparison.Ordinal) || r.Label.StartsWith("Codename", StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(code) && code != Fmt.NA)
            cpu.Add(Loc.T("Codinome: ", "Codename: ") + code.Split(CodenameCuts, StringSplitOptions.None)[0]);
        _cpu.Set(cpu, f.CpuCores > 0 ? Loc.T($"Processador com {f.CpuCores} núcleos", $"Processor with {f.CpuCores} cores") : Loc.T("Processador não identificado", "Processor not identified"));

        // Placa de vídeo
        var gpu = new List<string> { Nd(f.GpuName) };
        gpu.Add("VRAM: " + (f.GpuVramMiB is > 0 ? (f.GpuVramMiB.Value / 1024.0).ToString("0.#", Pt) + " GB" : Fmt.NA));
        var host = f.GpuPcieHostMax.HasValue ? "Gen " + f.GpuPcieHostMax : Fmt.NA;
        var cur = f.GpuPcieGenCurrent.HasValue ? "Gen " + f.GpuPcieGenCurrent + (f.GpuPcieWidthCurrent.HasValue ? " x" + f.GpuPcieWidthCurrent : "") : Fmt.NA;
        gpu.Add(Loc.T($"PCIe: host {host} · atual {cur}", $"PCIe: host {host} · current {cur}"));
        _gpu.Set(gpu, f.GpuVramMiB is > 0
            ? Loc.T($"Placa de vídeo com {(f.GpuVramMiB.Value / 1024.0).ToString("0.#", Pt)} GB de memória", $"Graphics card with {(f.GpuVramMiB.Value / 1024.0).ToString("0.#", Pt)} GB of memory")
            : Loc.T("Placa de vídeo identificada", "Graphics card identified"));

        // Memória
        double gb = f.RamInstalledGB > 0 ? f.RamInstalledGB : f.RamGB;
        var mem = new List<string> { (gb > 0 ? gb.ToString("0.#", Pt) + " GB" : Fmt.NA) + (string.IsNullOrWhiteSpace(f.RamType) ? "" : " " + f.RamType) };
        int? mts = f.RamConfiguredMTs ?? f.RamRatedMTs;
        mem.Add(Loc.T("Velocidade: ", "Speed: ") + (mts.HasValue ? mts + " MT/s" : Fmt.NA));
        mem.Add("Dual channel: " + (f.DualChannel is bool d ? (d ? Loc.T("sim", "yes") : Loc.T("não", "no")) : Fmt.NA) + (f.RamModules > 0 ? Loc.T($" · {f.RamModules} pente(s)", $" · {f.RamModules} module(s)") : ""));
        _mem.Set(mem, (gb > 0 ? gb.ToString("0.#", Pt) + Loc.T(" GB de memória", " GB of memory") : Loc.T("Memória não identificada", "Memory not identified"))
            + (f.DualChannel is bool dc ? (dc ? Loc.T(", em canal duplo", ", dual channel") : Loc.T(", em canal único", ", single channel")) : ""));

        // Placa-mãe
        var board = new List<string>
        {
            Nd(f.BoardModel),
            $"Chipset: {Nd(f.Chipset)} · BIOS: {Nd(f.BiosVersion)}",
            "Boot: " + (f.FirmwareUefi is bool u ? (u ? "UEFI" : Loc.T("BIOS legado", "Legacy BIOS")) : Fmt.NA) + " · Secure Boot: " + (f.SecureBoot is bool sb ? (sb ? Loc.T("ligado", "on") : Loc.T("desligado", "off")) : Fmt.NA)
        };
        _board.Set(board, Loc.T("Placa-mãe ", "Motherboard ") + Nd(f.BoardModel));

        // Armazenamento
        var disks = new List<string>();
        var stor = results.FirstOrDefault(r => r.Key == "armazenamento");
        if (stor != null)
        {
            // Grupos de disco: "Disco N" (pt) ou "Disk N" (en); as três primeiras linhas são modelo, tipo e capacidade.
            foreach (var g in stor.Groups.Where(g => g.Title.StartsWith("Disco ", StringComparison.Ordinal) || g.Title.StartsWith("Disk ", StringComparison.Ordinal)))
            {
                string Row(int i) => i < g.Rows.Count ? g.Rows[i].Value : Fmt.NA;
                disks.Add($"{Row(0)} — {Row(1)} · {Row(2)}");
            }
        }
        int diskCount = disks.Count;
        if (disks.Count == 0) disks.Add(Fmt.NA);
        else if (disks.Count > 4) { int extra = disks.Count - 4; disks = disks.Take(4).ToList(); disks.Add(Loc.T($"e mais {extra}", $"and {extra} more")); }
        _disks.Set(disks, diskCount == 0 ? Loc.T("Armazenamento não identificado", "Storage not identified") : diskCount == 1 ? Loc.T("1 disco instalado", "1 disk installed") : Loc.T(diskCount + " discos instalados", diskCount + " disks installed"));

        // Monitores
        var mons = new List<string>();
        foreach (var m in f.Displays.Take(3))
        {
            var res = m.Width > 0 ? $"{m.Width}x{m.Height}" + (m.RefreshHz > 0 ? "@" + m.RefreshHz.ToString("0", Pt) + " Hz" : "") : Fmt.NA;
            mons.Add($"{(string.IsNullOrWhiteSpace(m.Name) ? Loc.T("Monitor", "Display") : m.Name)} — {res}" + (string.IsNullOrWhiteSpace(m.Connection) ? "" : " · " + m.Connection));
        }
        if (f.Displays.Count > 3) mons.Add(Loc.T($"e mais {f.Displays.Count - 3}", $"and {f.Displays.Count - 3} more"));
        if (mons.Count == 0) mons.Add(Fmt.NA);
        _displays.Set(mons, f.Displays.Count == 0 ? Loc.T("Monitor não identificado", "Display not identified") : f.Displays.Count == 1 ? Loc.T("1 monitor conectado", "1 display connected") : Loc.T(f.Displays.Count + " monitores conectados", f.Displays.Count + " displays connected"));

        // Perfil Trabalho: impressoras e bateria (os cartões de hardware continuam)
        bool work = f.Profile == UsageProfile.Work;
        _printers.Visibility = work ? Visibility.Visible : Visibility.Collapsed;
        _battery.Visibility = work && f.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (work)
        {
            var (pl, ps) = WorkText.Printers(f);
            _printers.Set(pl, ps);
            if (f.HasBattery)
            {
                var (bl, bs) = WorkText.Battery(f);
                _battery.Set(bl, bs);
            }
        }
    }

    /// <summary>Texto ou "n/d" quando vazio.</summary>
    private static string Nd(string? s) => string.IsNullOrWhiteSpace(s) ? Fmt.NA : s.Trim();

    /// <summary>Nome curto do processador: tira (R)/(TM), a frequência após "@" e as palavras CPU/Processor.</summary>
    private static string ShortCpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fmt.NA;
        var s = Regex.Replace(name, @"\((R|TM|tm|r)\)", "");
        s = Regex.Replace(s, @"\s*@.*$", "");
        s = Regex.Replace(s, @"\b(CPU|Processor)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? name.Trim() : s;
    }

    // ---------- Desde a última leitura ----------

    /// <summary>Monta "Desde a última leitura": nota anterior e atual, itens resolvidos/melhorados/piorados/novos e mudanças de hardware.</summary>
    private void BuildDiff(ScanDiff? diff)
    {
        _diff.Children.Clear();
        if (diff == null)
        {
            _diff.Children.Add(Ui.CardTitleInfo(Loc.T("Desde a última leitura", "Since the last scan"), "Desde a última leitura"));
            _diff.Children.Add(Ui.Muted(Loc.T("Primeira leitura registrada. Na próxima vez você verá aqui o que mudou.", "First scan recorded. Next time you will see here what changed."), 13));
            return;
        }

        _diff.Children.Add(Ui.CardTitleInfo(Loc.T("Desde a última leitura (", "Since the last scan (") + Fmt.DateTime(diff.PreviousWhen) + ")", "Desde a última leitura"));
        var key = diff.CurrentScore > diff.PreviousScore ? "OkBrush" : diff.CurrentScore < diff.PreviousScore ? "BadBrush" : "MutedBrush";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        row.Children.Add(UiExtra.Text(Loc.T("Nota: ", "Score: "), "MutedBrush", 14, wrap: false));
        row.Children.Add(UiExtra.Text(diff.PreviousScore.ToString(Pt), "TextBrush", 14, FontWeights.SemiBold, wrap: false));
        row.Children.Add(UiExtra.Text("  →  ", key, 14, FontWeights.Bold, wrap: false));
        row.Children.Add(UiExtra.Text(diff.CurrentScore.ToString(Pt), key, 14, FontWeights.Bold, wrap: false));
        _diff.Children.Add(row);

        if (diff.IsEmpty)
        {
            _diff.Children.Add(Ui.Muted(Loc.T("Nada mudou desde a última leitura.", "Nothing changed since the last scan."), 13));
            return;
        }

        AddChanges(Loc.T("Resolvidos", "Resolved"), "✓ ", "OkBrush", diff.Resolved, c => c.Title, navigate: false);
        AddChanges(Loc.T("Melhoraram", "Improved"), "↑ ", "OkBrush", diff.Improved, SevText);
        AddChanges(Loc.T("Pioraram", "Worsened"), "↓ ", "BadBrush", diff.Worsened, SevText);
        AddChanges(Loc.T("Novos", "New"), "• ", "WarnBrush", diff.New, c => c.Title);

        if (diff.FactChanges.Count > 0)
        {
            _diff.Children.Add(Heading(Loc.T("O que mudou no hardware/configuração", "What changed in hardware/configuration"), "AccentBrush"));
            foreach (var fc in diff.FactChanges.Take(10))
                _diff.Children.Add(Line($"{fc.Label}: {fc.Old} → {fc.New}", "TextBrush"));
            if (diff.FactChanges.Count > 10) _diff.Children.Add(Ui.Muted(Loc.T($"e mais {diff.FactChanges.Count - 10}", $"and {diff.FactChanges.Count - 10} more")));
        }
    }

    /// <summary>Texto "título (severidade antiga → nova)" de um achado que mudou.</summary>
    private static string SevText(FindingChange c)
    {
        static string L(Severity? s) => s.HasValue ? new Finding(s.Value, "", "").SevLabel : Fmt.NA;
        return $"{c.Title} ({L(c.OldSev)} → {L(c.NewSev)})";
    }

    /// <summary>Seção de mudanças com no máximo 8 linhas; as clicáveis levam ao item na seção correspondente.</summary>
    private void AddChanges(string title, string mark, string brush, List<FindingChange> list, Func<FindingChange, string> text, bool navigate = true)
    {
        if (list.Count == 0) return;
        _diff.Children.Add(Heading($"{title} ({list.Count.ToString(Pt)})", brush));
        foreach (var c in list.Take(8))
        {
            var line = Line(mark + text(c), brush);
            if (navigate && !string.IsNullOrWhiteSpace(c.Section))
            {
                var change = c;
                line.Cursor = Cursors.Hand;
                line.Background = Brushes.Transparent;
                line.ToolTip = Loc.T("Ir para o item", "Go to the item");
                line.MouseLeftButtonUp += (_, _) => Ui.GoTo(change.Section, change.Title);
            }
            _diff.Children.Add(line);
        }
        if (list.Count > 8) _diff.Children.Add(Ui.Muted(Loc.T($"e mais {list.Count - 8}", $"and {list.Count - 8} more")));
    }

    /// <summary>Subtítulo colorido de uma lista de mudanças.</summary>
    private static TextBlock Heading(string text, string brush)
    {
        var t = UiExtra.Text(text, brush, 13, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 8, 0, 2);
        return t;
    }

    /// <summary>Linha de texto de uma mudança.</summary>
    private static TextBlock Line(string text, string brush)
    {
        var t = UiExtra.Text(text, brush, 12.5);
        t.Margin = new Thickness(8, 1, 0, 1);
        return t;
    }

    // ---------- Próximos passos ----------

    /// <summary>Recalcula os três primeiros passos pendentes (chamado também quando o usuário marca "Feito").</summary>
    public void RefreshNext()
    {
        _next.Children.Clear();
        _next.Children.Add(Ui.CardTitleInfo(Loc.T("Próximos passos", "Next steps"), "Próximos passos"));
        if (!_loaded) { _next.Children.Add(Ui.Empty(Waiting)); return; }
        BuildImprove();

        var pending = _plan.Where(s => !PlanStore.IsDone(s.Key)).OrderBy(s => s.Order).Take(3).ToList();
        if (pending.Count == 0)
        {
            _next.Children.Add(Ui.Muted(Loc.T("Nenhum passo pendente: a configuração está em ordem.", "No pending steps: the configuration is in order."), 13));
            return;
        }
        foreach (var s in pending)
        {
            var line = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(UiExtra.Text(s.Order.ToString(Pt) + ".", "AccentBrush", 14, FontWeights.Bold, wrap: false));
            head.Children.Add(new Border { Width = 8 });
            head.Children.Add(s.Cost == ActionCost.Gratis ? UiExtra.Pill(Loc.T("GRÁTIS", "FREE"), "OkBrush") : UiExtra.Pill("HARDWARE", "InfoBrush"));
            line.Children.Add(head);
            line.Children.Add(UiExtra.Text(s.Title, "TextBrush", 13.5, FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(s.Gain)) line.Children.Add(Ui.Muted(Loc.T("Ganho esperado: ", "Expected gain: ") + s.Gain));
            if (!string.IsNullOrWhiteSpace(s.SectionKey))
            {
                var section = s.SectionKey;
                var id = s.FindingId;
                var det = new Button { Content = Loc.T("Ver detalhes", "View details"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                det.Click += (_, _) => Ui.GoTo(section, string.IsNullOrWhiteSpace(id) ? null : id);
                line.Children.Add(det);
            }
            _next.Children.Add(line);
        }
        var btn = new Button { Content = Loc.T("Ver plano completo", "View full plan"), Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        btn.Click += (_, _) => _goTo("plan");
        _next.Children.Add(btn);
    }

    // ---------- Cartão de hardware ----------

    /// <summary>Cartão clicável de hardware; reaproveita os TextBlocks entre leituras.</summary>
    private sealed class HwCard : Border
    {
        /// <summary>Linhas do cartão (a primeira em destaque).</summary>
        private readonly StackPanel _lines = new();

        /// <summary>Cria o cartão; clicar nele abre a aba indicada e o mouse destaca a borda com o pincel do tema.</summary>
        public HwCard(string glyph, string ptTitle, string enTitle, string targetTab, Action<string> goTo)
        {
            var title = Loc.T(ptTitle, enTitle);
            Style = (Style)Application.Current.FindResource("Card");
            Margin = new Thickness(0, 0, 12, 12);
            Cursor = Cursors.Hand;
            MinHeight = 118;
            var root = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var ic = UiExtra.Icon(glyph, 18, "AccentBrush");
            ic.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(ic);
            head.Children.Add(Ui.CardTitle(title));
            ((TextBlock)head.Children[1]).Margin = new Thickness(0);
            head.Children.Add(Ui.InfoIcon(ptTitle));
            root.Children.Add(head);
            root.Children.Add(_lines);
            Child = root;
            MouseLeftButtonUp += (_, _) => goTo(targetTab);
            MouseEnter += (_, _) => SetResourceReference(BorderBrushProperty, "AccentBrush");
            MouseLeave += (_, _) => SetResourceReference(BorderBrushProperty, "LineBrush");
            System.Windows.Automation.AutomationProperties.SetName(this, title + Loc.T(": abrir detalhes", ": open details"));
        }

        /// <summary>Linhas completas do modo Detalhado.</summary>
        private IReadOnlyList<string> _detailed = new List<string>();
        /// <summary>Frase única do modo Simples.</summary>
        private string _simpleLine = "";

        /// <summary>Guarda as linhas (modo Detalhado) e a frase única (modo Simples) e redesenha.</summary>
        public void Set(IReadOnlyList<string> lines, string simpleLine)
        {
            _detailed = lines;
            _simpleLine = simpleLine;
            Render();
        }

        /// <summary>Redesenha conforme o modo atual.</summary>
        public void Render()
        {
            if (_detailed.Count == 0) return;
            var lines = Ui.Simple && !string.IsNullOrWhiteSpace(_simpleLine) ? new List<string> { _simpleLine } : new List<string>(_detailed);
            if (ExtraLine != null) lines.Add(ExtraLine);
            Show(lines);
        }

        /// <summary>Linha adicional (temperatura atual) exibida ao fim do cartão; null = sem linha.</summary>
        public string? ExtraLine { get; set; }

        /// <summary>Define as linhas do cartão; a primeira em destaque, as demais suaves.</summary>
        private void Show(List<string> lines)
        {
            while (_lines.Children.Count > lines.Count) _lines.Children.RemoveAt(_lines.Children.Count - 1);
            while (_lines.Children.Count < lines.Count)
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                _lines.Children.Add(t);
            }
            for (int i = 0; i < lines.Count; i++)
            {
                var t = (TextBlock)_lines.Children[i];
                t.Text = lines[i];
                t.FontSize = i == 0 ? 14 : 12;
                t.FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal;
                t.SetResourceReference(TextBlock.ForegroundProperty, i == 0 ? "TextBrush" : "MutedBrush");
            }
        }
    }
}
