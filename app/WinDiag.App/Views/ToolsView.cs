using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Tools;

namespace WinDiag.App.Views;

/// <summary>Aba "Ferramentas": ações pontuais (impressão, rede, limpeza, reparo, relatórios e atalhos) com confirmação, saída ao vivo e histórico.</summary>
public sealed class ToolsView : UserControl
{
    /// <summary>Máximo de linhas mantidas na saída ao vivo de um cartão.</summary>
    private const int MaxOutputLines = 500;

    /// <summary>Leitor da última varredura completa (nulo durante a varredura).</summary>
    private readonly Func<SystemFacts?> _getFacts;
    /// <summary>Refaz a varredura da janela principal.</summary>
    private readonly Func<Task> _rescan;
    /// <summary>Avisa a janela principal que há operação em curso.</summary>
    private readonly Action _beginBusy;
    /// <summary>Encerra o aviso de operação em curso.</summary>
    private readonly Action _endBusy;
    /// <summary>Executor das ferramentas (grava histórico e snapshots quando aplicável).</summary>
    private readonly ToolRunner _runner = new();
    /// <summary>Campo de busca.</summary>
    private readonly TextBox _search = new() { Margin = new Thickness(0, 8, 0, 0), MaxLength = 80 };
    /// <summary>Faixa "aberto com outra conta de administrador", visível só quando a leitura indica isso.</summary>
    private readonly ContentControl _userBanner = new() { IsTabStop = false };
    /// <summary>Contêiner das seções por categoria, refeito ao filtrar ou trocar de perfil.</summary>
    private readonly StackPanel _sections = new();
    /// <summary>Mensagem do último atalho executado.</summary>
    private readonly TextBlock _shortcutMsg = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    /// <summary>Cartões e atalhos por id da ferramenta (criados uma vez; só o layout é refeito).</summary>
    private readonly Dictionary<string, ToolCard> _cards = new();
    /// <summary>true enquanto uma ferramenta roda (as demais ficam desabilitadas).</summary>
    private bool _running;
    /// <summary>Handler de troca de perfil, guardado para poder ser removido.</summary>
    private readonly Action _onProfile;

    /// <summary>Formata bytes no idioma atual (MB abaixo de 1 GB, GB acima).</summary>
    public static string Size(long bytes) => DiskCleanup.FormatBytes(bytes);

    /// <summary>Cria a aba; <paramref name="getFacts"/> devolve a última leitura completa e <paramref name="rescan"/> refaz a varredura.</summary>
    public ToolsView(Func<SystemFacts?> getFacts, Func<Task> rescan, Action beginBusy, Action endBusy)
    {
        _getFacts = getFacts;
        _rescan = rescan;
        _beginBusy = beginBusy;
        _endBusy = endBusy;

        var root = new StackPanel();
        var head = new StackPanel();
        head.Children.Add(Ui.CardTitle(Loc.T("Buscar ferramenta", "Find a tool")));
        _onProfile = () => Dispatcher.BeginInvoke(Rebuild);
        System.Windows.Automation.AutomationProperties.SetName(_search, Loc.T("Buscar ferramenta por nome ou descrição", "Search tools by name or description"));
        _search.TextChanged += (_, _) => Rebuild();
        head.Children.Add(_search);
        head.Children.Add(Ui.Muted(Loc.T("Cada ferramenta mostra o que será executado antes de rodar. Só uma roda por vez.", "Each tool shows what will be run before it starts. Only one runs at a time."), 12));
        var hist = new Button { Content = Loc.T("Histórico de ferramentas", "Tool history"), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(hist, Loc.T("Abrir o histórico de ferramentas", "Open the tool history"));
        hist.Click += (_, _) =>
        {
            var w = new ToolHistoryWindow(_runner);
            if (Window.GetWindow(this) is { } owner) w.Owner = owner;
            w.ShowDialog();
        };
        head.Children.Add(hist);
        root.Children.Add(_userBanner);
        root.Children.Add(Ui.Card(head));
        root.Children.Add(_sections);
        Content = root;

        try
        {
            foreach (var t in ToolCatalog.All)
                _cards[t.Id] = new ToolCard(this, t, t.Category == ToolCategory.Shortcuts);
        }
        catch (Exception ex) { _sections.Children.Add(Ui.Muted(Loc.T("Não foi possível carregar o catálogo de ferramentas: ", "Could not load the tool catalog: ") + ex.Message, 13)); return; }

        Loaded += (_, _) => ProfileUi.Changed += _onProfile;
        Unloaded += (_, _) => ProfileUi.Changed -= _onProfile;
        Rebuild();
    }

    /// <summary>Ordem das categorias conforme o perfil ativo.</summary>
    private static ToolCategory[] CategoryOrder() => ProfileUi.IsWork
        ? new[] { ToolCategory.Printing, ToolCategory.Network, ToolCategory.Cleanup, ToolCategory.Repair, ToolCategory.Reports, ToolCategory.Shortcuts, ToolCategory.Gaming }
        : new[] { ToolCategory.Gaming, ToolCategory.Network, ToolCategory.Cleanup, ToolCategory.Repair, ToolCategory.Printing, ToolCategory.Reports, ToolCategory.Shortcuts };

    /// <summary>Atualiza os motivos de bloqueio com a leitura mais recente (chamado ao fim de cada varredura).</summary>
    public void Refresh()
    {
        var facts = _getFacts();
        _userBanner.Content = Ui.OtherUserBanner(facts);
        foreach (var c in _cards.Values) c.SetBlock(SafeBlock(c.Tool, facts));
        UpdateEnabled();
    }

    /// <summary>Motivo de bloqueio da ferramenta ou null; nunca lança.</summary>
    private string? SafeBlock(ToolDefinition t, SystemFacts? facts)
    {
        try { return _runner.BlockReason(t, facts); }
        catch (Exception ex) { return Loc.T("Não foi possível verificar se a ferramenta pode rodar: ", "Could not check whether the tool can run: ") + ex.Message; }
    }

    /// <summary>Refaz as seções conforme busca e perfil; os cartões são reaproveitados (a saída em andamento não se perde).</summary>
    private void Rebuild()
    {
        try
        {
            _sections.Children.Clear();
            foreach (var c in _cards.Values) c.Detach();
            string q = _search.Text.Trim();
            var profile = ProfileUi.Current;
            var facts = _getFacts();
            bool any = false;
            foreach (var cat in CategoryOrder())
            {
                var tools = ToolCatalog.All.Where(t => t.Category == cat
                    && (q.Length == 0 || t.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.Explain.Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
                if (tools.Count == 0) continue;
                any = true;
                var title = UiExtra.Text(ToolCatalog.CategoryName(cat), "AccentBrush", 15, FontWeights.SemiBold);
                title.Margin = new Thickness(0, 8, 0, 8);
                _sections.Children.Add(title);
                if (cat == ToolCategory.Shortcuts)
                {
                    _sections.Children.Add(_shortcutMsg);
                    var wrap = new WrapPanel();
                    foreach (var t in tools) wrap.Children.Add(_cards[t.Id]);
                    _sections.Children.Add(wrap);
                }
                else
                {
                    foreach (var t in tools) _sections.Children.Add(_cards[t.Id]);
                }
            }
            if (!any) _sections.Children.Add(Ui.Empty(Loc.T("Nenhuma ferramenta encontrada para esta busca.", "No tool found for this search.")));
            foreach (var c in _cards.Values)
            {
                c.SetBlock(SafeBlock(c.Tool, facts));
                c.SetOtherProfile(!ProfileUi.Matches(c.Tool.Scope, profile));
            }
            UpdateEnabled();
        }
        catch (Exception ex)
        {
            _sections.Children.Clear();
            _sections.Children.Add(Ui.Muted(Loc.T("Falha ao exibir as ferramentas: ", "Failed to display the tools: ") + ex.Message, 13));
        }
    }

    /// <summary>Habilita os botões só quando nada está rodando e a ferramenta não está bloqueada.</summary>
    private void UpdateEnabled()
    {
        foreach (var c in _cards.Values) c.SetRunning(_running);
    }

    /// <summary>Rola até o cartão da ferramenta (limpando a busca se preciso) e o destaca.</summary>
    public void FocusTool(string toolId)
    {
        try
        {
            if (!_cards.TryGetValue(toolId, out var card)) return;
            if (_search.Text.Length > 0) _search.Text = ""; // Rebuild roda pelo evento TextChanged
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                card.UpdateLayout();
                card.BringIntoView();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    card.BringIntoView();
                    card.Flash();
                }));
            }));
        }
        catch { /* navegar até a ferramenta nunca derruba o app */ }
    }

    /// <summary>Mostra a mensagem de um atalho (ou erro) abaixo do título da seção de atalhos.</summary>
    private void SayShortcut(string text, string brushKey)
    {
        _shortcutMsg.Text = text;
        _shortcutMsg.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        _shortcutMsg.Visibility = Visibility.Visible;
    }

    /// <summary>Fluxo completo: bloqueio, confirmação (se alterar algo ou exigir administrador), ponto de restauração opcional, execução com saída ao vivo e resultado.</summary>
    private async Task StartAsync(ToolCard card)
    {
        if (_running) return;
        var t = card.Tool;
        Operations.Ticket? ticket = null;
        CancellationTokenSource? cts = null;
        bool started = false;
        try
        {
            if (Operations.IsBusy) { Operations.WarnBusy(Window.GetWindow(this)); return; }
            var facts = _getFacts();
            string? block = SafeBlock(t, facts);
            if (block is not null) { card.Say(block, "WarnBrush"); return; }

            IReadOnlyList<string>? optionIds = null;
            bool restore = false;
            // Só roda direto a ferramenta somente-leitura que não exige administrador.
            bool direct = t.ReadOnly && !t.NeedsAdmin;
            if (!direct)
            {
                Task<IReadOnlyList<ToolOption>>? opts = t.HasOptions ? Task.Run(() => _runner.GetOptions(t)) : null;
                var rp = ToolCatalog.Get("RESTORE_POINT");
                bool offer = t.Risk != FixRisk.Baixo && rp is not null && t.Id != "RESTORE_POINT" && SafeBlock(rp, facts) is null;
                string? warning = null;
                try { warning = _runner.ConfirmWarning(t, facts); }
                catch { warning = null; /* sem o aviso adicional a confirmação continua valendo */ }
                var r = ToolConfirmDialog.Ask(Window.GetWindow(this), t, opts, offer, warning);
                if (!r.Confirmed) return;
                restore = r.RestorePoint;
                if (t.HasOptions) optionIds = r.OptionIds;
            }

            cts = new CancellationTokenSource();
            ticket = Operations.TryBegin(t.Name, cts);
            if (ticket is null) { Operations.WarnBusy(Window.GetWindow(this)); return; }
            started = true;
            var ct = cts.Token;
            _running = true;
            _beginBusy();
            UpdateEnabled();
            card.BeginRun(t.LongRunning);
            var progress = new Progress<string>(card.Append);

            OpResult res;
            try
            {
                OpResult? pre = null;
                if (restore && ToolCatalog.Get("RESTORE_POINT") is { } rpTool)
                {
                    card.Append(Loc.T("Criando ponto de restauração...", "Creating restore point..."));
                    pre = await Task.Run(() => _runner.RunAsync(rpTool, progress, ct));
                }
                bool go = true;
                if (pre is { Ok: false })
                {
                    string why = string.IsNullOrWhiteSpace(pre.Error) ? Loc.T("motivo não informado", "reason not provided") : pre.Error!;
                    go = ConfirmDialog.Ask(Window.GetWindow(this), Loc.T("Ponto de restauração", "Restore point"),
                        Loc.T("Continuar mesmo assim?", "Continue anyway?"),
                        Loc.T("O ponto de restauração não foi criado: ", "The restore point was not created: ") + why,
                        warn: true, okText: Loc.T("Continuar", "Continue"), cancelText: Loc.T("Não", "No"), defaultCancel: true);
                }
                if (!go)
                {
                    res = new OpResult { Ok = false, Error = Loc.T("Cancelado: o ponto de restauração não foi criado. ", "Cancelled: the restore point was not created. ") + pre?.Error };
                }
                else
                {
                    var ids = optionIds;
                    res = await Task.Run(() => _runner.RunAsync(t, progress, ct, ids));
                }
            }
            catch (OperationCanceledException) { res = new OpResult { Ok = false, Error = Loc.T("Cancelado pelo usuário.", "Cancelled by the user.") }; }
            catch (Exception ex) { res = new OpResult { Ok = false, Error = ex.Message }; }

            card.EndRun(res);
        }
        catch (Exception ex)
        {
            card.Say(Loc.T("Erro: ", "Error: ") + ex.Message, "BadBrush");
        }
        finally
        {
            if (started)
            {
                _running = false;
                _endBusy();
            }
            ticket?.Dispose();
            cts?.Dispose();
            UpdateEnabled();
        }
    }
    /// <summary>Cartão (ou botão de atalho) de uma ferramenta, com saída ao vivo e resultado.</summary>
    private sealed class ToolCard : Border
    {
        /// <summary>Tela dona (recebe cliques de executar, cancelar e reler).</summary>
        private readonly ToolsView _owner;
        /// <summary>Definição da ferramenta.</summary>
        public ToolDefinition Tool { get; }
        /// <summary>true = botão compacto de atalho (sem painel de saída).</summary>
        private readonly bool _compact;
        /// <summary>Botão principal (Executar, ou o próprio atalho).</summary>
        private readonly Button _run;
        /// <summary>Botão Cancelar (ferramentas longas).</summary>
        private readonly Button _cancel = new() { Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        /// <summary>Motivo de bloqueio exibido no cartão.</summary>
        private readonly TextBlock _blockText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        /// <summary>Barra indeterminada exibida durante a execução.</summary>
        private readonly ProgressBar _bar = new() { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        /// <summary>Saída ao vivo (monoespaçada, rolagem automática).</summary>
        private readonly TextBox _output = new()
        {
            IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0),
        };
        /// <summary>Resultado final (mensagens, avisos e botão de reler).</summary>
        private readonly StackPanel _result = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        /// <summary>Linhas da saída ao vivo.</summary>
        private readonly List<string> _lines = new();
        /// <summary>Camada de destaque da navegação.</summary>
        private readonly HighlightOverlay _flash = new();
        /// <summary>Pílula do perfil quando a ferramenta é de outro perfil.</summary>
        private readonly Border _scopePill;
        /// <summary>Motivo de bloqueio atual (null = pode rodar).</summary>
        private string? _block;
        /// <summary>true enquanto ESTA ferramenta roda.</summary>
        private bool _thisRunning;

        /// <summary>Monta o cartão (ou o botão compacto) da ferramenta.</summary>
        public ToolCard(ToolsView owner, ToolDefinition tool, bool compact)
        {
            _owner = owner;
            Tool = tool;
            _compact = compact;
            Uid = tool.Id;
            _scopePill = UiExtra.Pill(ProfileUi.ScopeLabel(tool.Scope) ?? "", "InfoBrush");
            _scopePill.Visibility = Visibility.Collapsed;

            _run = new Button { Content = compact ? tool.Name : Loc.T("Executar", "Run"), Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(14, 6, 14, 6) };
            System.Windows.Automation.AutomationProperties.SetName(_run, (compact ? "" : Loc.T("Executar: ", "Run: ")) + tool.Name);
            _run.Click += async (_, _) => await _owner.StartAsync(this);
            _cancel.Content = Loc.T("Cancelar", "Cancel");
            System.Windows.Automation.AutomationProperties.SetName(_cancel, Loc.T("Cancelar: ", "Cancel: ") + tool.Name);
            _cancel.Click += (_, _) => Operations.Cancel();
            System.Windows.Automation.AutomationProperties.SetName(_output, Loc.T("Saída de ", "Output of ") + tool.Name);
            _output.Style = (Style)Application.Current.FindResource("ValueBox");

            if (compact)
            {
                _run.ToolTip = tool.Explain;
                Margin = new Thickness(0, 0, 8, 8);
                var g = new Grid();
                g.Children.Add(_run);
                g.Children.Add(_flash);
                Child = g;
                return;
            }

            Style = (Style)Application.Current.FindResource("Card");
            var layer = new Grid();
            var stack = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = tool.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            badges.Children.Add(UiExtra.Pill(Loc.T("Risco ", "Risk ") + RiskText(tool.Risk), tool.Risk switch { FixRisk.Alto => "BadBrush", FixRisk.Medio => "WarnBrush", _ => "OkBrush" }));
            if (tool.NeedsAdmin) badges.Children.Add(UiExtra.Pill(Loc.T("Exige administrador", "Requires administrator"), "InfoBrush"));
            if (tool.NeedsRestart) badges.Children.Add(UiExtra.Pill(Loc.T("Exige reinício", "Requires restart"), "WarnBrush"));
            if (tool.ReadOnly) badges.Children.Add(UiExtra.Pill(Loc.T("Não altera nada", "Changes nothing"), "OkBrush"));
            if (!string.IsNullOrWhiteSpace(tool.Duration)) badges.Children.Add(UiExtra.Pill(tool.Duration, "MutedBrush"));
            badges.Children.Add(_scopePill);
            left.Children.Add(badges);
            left.Children.Add(new TextBlock { Text = tool.Explain, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) });

            if (tool.Commands.Count > 0)
            {
                var cmds = new TextBox
                {
                    Text = string.Join(Environment.NewLine, tool.Commands), FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0),
                };
                cmds.Style = (Style)Application.Current.FindResource("ValueBox");
                System.Windows.Automation.AutomationProperties.SetName(cmds, Loc.T("Comandos de ", "Commands of ") + tool.Name);
                var link = new TextBlock { Text = Loc.T("Ver o que será executado", "See what will be run"), Cursor = Cursors.Hand, FontSize = 12, TextDecorations = TextDecorations.Underline, Margin = new Thickness(0, 6, 0, 0), Focusable = true };
                link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                System.Windows.Automation.AutomationProperties.SetName(link, Loc.T("Mostrar ou ocultar o que será executado em ", "Show or hide what will be run in ") + tool.Name);
                void Toggle() => cmds.Visibility = cmds.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                link.MouseLeftButtonUp += (_, _) => Toggle();
                link.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Toggle(); }
                };
                left.Children.Add(link);
                left.Children.Add(cmds);
            }
            _blockText.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            left.Children.Add(_blockText);
            head.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 0, 0, 0) };
            right.Children.Add(_run);
            right.Children.Add(_cancel);
            Grid.SetColumn(right, 1);
            head.Children.Add(right);

            stack.Children.Add(head);
            stack.Children.Add(_bar);
            stack.Children.Add(_output);
            stack.Children.Add(_result);
            layer.Children.Add(stack);
            layer.Children.Add(_flash);
            Child = layer;
        }

        /// <summary>Rótulo do risco no idioma atual.</summary>
        private static string RiskText(FixRisk r) => r switch { FixRisk.Alto => Loc.T("alto", "high"), FixRisk.Medio => Loc.T("médio", "medium"), _ => Loc.T("baixo", "low") };

        /// <summary>Remove o cartão do painel atual para poder ser adicionado ao novo layout.</summary>
        public void Detach()
        {
            if (Parent is Panel p) p.Children.Remove(this);
        }

        /// <summary>Define o motivo de bloqueio (null = liberado) e o exibe no cartão.</summary>
        public void SetBlock(string? reason)
        {
            _block = reason;
            if (_compact) _run.ToolTip = reason is null ? Tool.Explain : Tool.Explain + "\n" + reason;
            else
            {
                _blockText.Text = reason ?? "";
                _blockText.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
            }
            _run.IsEnabled = !_ownerRunning() && reason is null;
        }

        /// <summary>true se alguma ferramenta está rodando na tela dona.</summary>
        private bool _ownerRunning() => _owner._running;

        /// <summary>Mostra a pílula de perfil quando a ferramenta é de outro perfil.</summary>
        public void SetOtherProfile(bool other) => _scopePill.Visibility = other && !_compact ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Atualiza o estado dos botões conforme alguma ferramenta esteja rodando.</summary>
        public void SetRunning(bool anyRunning) => _run.IsEnabled = !anyRunning && _block is null;

        /// <summary>Acende o destaque de navegação.</summary>
        public void Flash() => _flash.Flash();

        /// <summary>Mostra uma mensagem no cartão (ou na área de atalhos).</summary>
        public void Say(string text, string brushKey)
        {
            if (_compact) { _owner.SayShortcut(text, brushKey); return; }
            _result.Children.Clear();
            _result.Children.Add(UiExtra.Text(text, brushKey, 12.5, FontWeights.SemiBold));
            _result.Visibility = Visibility.Visible;
        }

        /// <summary>Prepara o cartão para a execução: limpa saída e resultado e mostra barra e (se longa) o botão Cancelar.</summary>
        public void BeginRun(bool cancellable)
        {
            _thisRunning = true;
            _lines.Clear();
            _output.Clear();
            _result.Children.Clear();
            _result.Visibility = Visibility.Collapsed;
            if (_compact) { _owner.SayShortcut(Loc.T("Executando: ", "Running: ") + Tool.Name + "...", "MutedBrush"); return; }
            _bar.Visibility = Visibility.Visible;
            _output.Visibility = Visibility.Visible;
            _cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Acrescenta uma linha à saída ao vivo, mantendo no máximo <see cref="MaxOutputLines"/> linhas.</summary>
        public void Append(string line)
        {
            if (_compact || !_thisRunning || line is null) return;
            _lines.Add(line);
            if (_lines.Count > MaxOutputLines)
            {
                _lines.RemoveRange(0, _lines.Count - MaxOutputLines + 100);
                _output.Text = string.Join(Environment.NewLine, _lines);
            }
            else if (_output.Text.Length == 0) _output.Text = line;
            else _output.AppendText(Environment.NewLine + line);
            _output.ScrollToEnd();
        }

        /// <summary>Mostra o resultado final: verde/vermelho, mensagens, aviso de reinício e "Ler de novo" quando altera algo.</summary>
        public void EndRun(OpResult res)
        {
            _thisRunning = false;
            _bar.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            string head = res.Ok ? Loc.T("Concluído com sucesso.", "Completed successfully.") : Loc.T("Não foi concluído.", "Not completed.");
            if (_compact)
            {
                string msg = head + (res.Messages.Count > 0 ? " " + string.Join(" ", res.Messages) : "") + (!res.Ok && !string.IsNullOrWhiteSpace(res.Error) ? " " + res.Error : "");
                _owner.SayShortcut(Tool.Name + ": " + msg, res.Ok ? "OkBrush" : "BadBrush");
                return;
            }
            _result.Children.Clear();
            _result.Children.Add(UiExtra.Text(head, res.Ok ? "OkBrush" : "BadBrush", 13, FontWeights.SemiBold));
            foreach (var m in res.Messages) _result.Children.Add(Ui.Muted(m, 12));
            if (!res.Ok && !string.IsNullOrWhiteSpace(res.Error)) _result.Children.Add(UiExtra.Text(Loc.T("Erro: ", "Error: ") + res.Error, "BadBrush", 12.5));
            if (res.RebootNeeded || Tool.NeedsRestart)
                _result.Children.Add(UiExtra.Text(Loc.T("Reinicie o PC para concluir.", "Restart the PC to finish."), "WarnBrush", 12.5, FontWeights.SemiBold));
            if (!Tool.ReadOnly && res.Ok)
            {
                var again = new Button { Content = Loc.T("Ler de novo", "Scan again"), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(again, Loc.T("Ler de novo depois de ", "Scan again after ") + Tool.Name);
                again.Click += async (_, _) =>
                {
                    again.IsEnabled = false;
                    try { await _owner._rescan(); } catch { /* a releitura falhar não invalida o resultado */ }
                    finally { again.IsEnabled = true; }
                };
                _result.Children.Add(again);
            }
            _result.Visibility = Visibility.Visible;
        }
    }
}
