using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Aba "Otimizações": interruptor por item, com desfazer individual pelo valor salvo no snapshot.</summary>
public sealed class OptimizationsView : UserControl
{
    /// <summary>Cultura do idioma atual para formatar números (ponto/vírgula).</summary>
    private static CultureInfo PtBr => WinDiag.Core.Loc.Culture;
    /// <summary>Dica do interruptor travado: valor já aplicado fora do WinDiag, sem valor anterior para voltar.</summary>
    private static string LockedTip => Loc.T("Configurado fora do WinDiag (ex.: AtlasOS) ou padrão do Windows; sem valor anterior salvo para voltar.", "Configured outside WinDiag (e.g. AtlasOS) or the Windows default; no previous value saved to go back to.");

    /// <summary>Leitor da última varredura completa (nulo enquanto ela roda).</summary>
    private readonly Func<SystemFacts?> _getFacts;
    /// <summary>Refaz a varredura da janela principal.</summary>
    private readonly Func<Task> _rescan;
    /// <summary>Conteúdo rolável da aba, reconstruído a cada atualização.</summary>
    private readonly StackPanel _content = new();
    /// <summary>Mensagens do último resultado.</summary>
    private readonly StackPanel _logPanel = new();
    /// <summary>Cartão "Resultado", exibido depois da primeira operação.</summary>
    private readonly Border _logCard;
    /// <summary>Camada translúcida exibida enquanto uma operação roda.</summary>
    private readonly Border _busy;
    /// <summary>Texto da camada ("Aplicando...", "Desfazendo..." etc.).</summary>
    private readonly TextBlock _busyText;
    /// <summary>Mostra também os itens que não se aplicam a este PC ou a outro perfil.</summary>
    private bool _showInapplicable;
    /// <summary>true durante uma operação; bloqueia cliques duplicados.</summary>
    private bool _working;
    /// <summary>Número da atualização mais recente; respostas atrasadas de atualizações antigas são descartadas.</summary>
    private int _refreshVersion;
    /// <summary>Avisa a janela principal que há operação em curso.</summary>
    private readonly Action _beginBusy;
    /// <summary>Encerra o aviso de operação em curso.</summary>
    private readonly Action _endBusy;

    /// <summary>
    /// Cria a aba; <paramref name="getFacts"/> fornece a última leitura COMPLETA (null durante a varredura),
    /// <paramref name="rescan"/> refaz a varredura e <paramref name="beginBusy"/>/<paramref name="endBusy"/> bloqueiam o Reler enquanto uma operação roda.
    /// </summary>
    public OptimizationsView(Func<SystemFacts?> getFacts, Func<Task> rescan, Action beginBusy, Action endBusy)
    {
        _getFacts = getFacts;
        _rescan = rescan;
        _beginBusy = beginBusy;
        _endBusy = endBusy;

        var root = new Grid();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content };
        SmoothScroll.Attach(scroll);
        _content.Margin = new Thickness(0, 0, 8, 0);
        root.Children.Add(scroll);

        _busyText = new TextBlock { Text = Loc.T("Aplicando...", "Applying..."), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _busy = new Border
        {
            Background = Ui.Res("OverlayBrush"),
            Visibility = Visibility.Collapsed,
            Child = _busyText,
        };
        root.Children.Add(_busy);
        Content = root;

        var logBody = new StackPanel();
        logBody.Children.Add(Ui.CardTitle(Loc.T("Resultado", "Result")));
        logBody.Children.Add(_logPanel);
        _logCard = Ui.Card(logBody);
        _logCard.Visibility = Visibility.Collapsed;

        Refresh();
    }

    /// <summary>Recalcula os status e redesenha a lista.</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>Recalcula os status fora da thread de UI e redesenha; só usa fatos de uma varredura completa.</summary>
    public async Task RefreshAsync()
    {
        int version = ++_refreshVersion;
        var facts = _getFacts();
        if (facts is null)
        {
            // Varredura em andamento (ou ainda não concluída): espera o evento de varredura completa.
            _content.Children.Clear();
            _content.Children.Add(Ui.Empty(Loc.T("Atualizando...", "Updating...")));
            return;
        }

        // Fora da UI thread: calcular o estado lê registro e arquivos de snapshot, o que pode demorar.
        List<FixStatus> statuses;
        string? error = null;
        try { statuses = await Task.Run(() => new FixEngine().GetStatuses(facts)); }
        catch (Exception ex) { statuses = new List<FixStatus>(); error = ex.Message; }
        if (version != _refreshVersion) return; // chamada mais recente assume

        _content.Children.Clear();
        if (error is not null)
        {
            _content.Children.Add(Ui.Muted(Loc.T("Não foi possível calcular o estado das otimizações: ", "Could not calculate the state of the optimizations: ") + error, 13));
            return;
        }

        if (!facts.IsAdmin)
            _content.Children.Add(Banner(Loc.T("Alterações exigem executar como administrador.", "Changes require running as administrator."), "WarnBrush"));

        _content.Children.Add(BuildHeader(facts, statuses));

        var profile = facts.Profile;
        var items = statuses.Where(s => !s.Fix.Temp && !s.Fix.StartupApps).ToList();
        var visible = items.Where(s => s.Kind != FixStatusKind.NaoAplicavel && ProfileUi.Matches(s.Fix.Scope, profile))
            .OrderBy(s => s.Kind switch { FixStatusKind.AtivoWinDiag => 0, FixStatusKind.Disponivel => 1, _ => 2 })
            .ToList();
        foreach (var s in visible) _content.Children.Add(BuildCard(s, facts, false));

        if (profile == UsageProfile.Work) _content.Children.Add(new BloatCardView(_getFacts, _rescan, _beginBusy, _endBusy));

        var hidden = items.Where(s => s.Kind == FixStatusKind.NaoAplicavel || !ProfileUi.Matches(s.Fix.Scope, profile)).ToList();
        if (hidden.Count > 0)
        {
            var cb = new CheckBox
            {
                Content = Loc.T("Mostrar itens que não se aplicam a este PC ou a outro perfil", "Show items that do not apply to this PC or belong to another profile"),
                IsChecked = _showInapplicable,
                Foreground = Ui.Res("MutedBrush"),
                Margin = new Thickness(2, 4, 0, 10),
            };
            cb.Click += (_, _) => { _showInapplicable = cb.IsChecked == true; Refresh(); };
            _content.Children.Add(cb);
            if (_showInapplicable)
                foreach (var s in hidden) _content.Children.Add(BuildCard(s, facts, true));
        }

        var startup = statuses.FirstOrDefault(s => s.Fix.StartupApps);
        if ((startup is not null && startup.Kind != FixStatusKind.NaoAplicavel) || facts.StartupItems.Count > 0)
            _content.Children.Add(BuildStartupCard(facts));

        _content.Children.Add(_logCard);
    }

    /// <summary>Faixa de aviso com borda colorida (ex.: falta de administrador).</summary>
    private static Border Banner(string text, string brushKey)
    {
        var b = Ui.Res(brushKey);
        return new Border
        {
            BorderBrush = b,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock { Text = text, Foreground = b, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
        };
    }

    /// <summary>Cartão de cabeçalho: texto explicativo, "Limpar temporários" e histórico de snapshots (janela modal, bloqueando a navegação).</summary>
    private Border BuildHeader(SystemFacts facts, List<FixStatus> statuses)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardTitleInfo(Loc.T("Otimizações", "Optimizations"), "Otimizações"));
        sp.Children.Add(Ui.Muted(Loc.T("Cada item ligado pelo WinDiag pode ser desligado sozinho, voltando ao valor anterior salvo no snapshot, sem restaurar o snapshot inteiro. Itens que já estavam assim antes do WinDiag (ex.: AtlasOS) aparecem travados, pois não há valor anterior para voltar.", "Each item turned on by WinDiag can be turned off on its own, returning to the previous value saved in the snapshot, without restoring the whole snapshot. Items that were already like this before WinDiag (e.g. AtlasOS) appear locked, since there is no previous value to return to."), 12.5));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var temp = statuses.FirstOrDefault(s => s.Fix.Temp);
        var clean = new Button
        {
            Content = Loc.T($"Limpar temporários ({facts.TempMB.ToString("0.#", PtBr)} MB)", $"Clear temporary files ({facts.TempMB.ToString("0.#", PtBr)} MB)"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = temp is not null && temp.Kind != FixStatusKind.NaoAplicavel && !_working,
        };
        clean.Click += async (_, _) => await CleanTempAsync();
        row.Children.Add(clean);

        var hist = new Button { Content = Loc.T("Histórico de snapshots", "Snapshot history"), Padding = new Thickness(12, 5, 12, 5) };
        hist.Click += (_, _) =>
        {
            var w = new SnapshotHistoryWindow(_rescan, facts.IsAdmin);
            var owner = Window.GetWindow(this);
            if (owner is not null) w.Owner = owner;
            _beginBusy();
            try { w.ShowDialog(); }
            finally { _endBusy(); }
        };
        row.Children.Add(hist);
        sp.Children.Add(row);
        return Ui.Card(sp);
    }

    /// <summary>Cartão de uma otimização: nome, risco, explicação, estado e interruptor (ou botão, se for ação pontual sem desfazer).</summary>
    private Border BuildCard(FixStatus st, SystemFacts facts, bool inapplicable)
    {
        var fix = st.Fix;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = fix.ShortName, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(RiskBadge(fix));
        if (fix.Reboot)
            title.Children.Add(new TextBlock { Text = Loc.T("Exige reinício", "Requires restart"), Foreground = Ui.Res("WarnBrush"), FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (ProfileUi.ScopeLabel(fix.Scope) is { } scopeLabel && !ProfileUi.Matches(fix.Scope, facts.Profile))
        {
            var pill = UiExtra.Pill(scopeLabel, "InfoBrush");
            pill.Margin = new Thickness(10, 0, 0, 0);
            title.Children.Add(pill);
        }
        left.Children.Add(title);
        left.Children.Add(Ui.Muted(fix.Name, 12));
        left.Children.Add(new TextBlock { Text = fix.Explain, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) });

        var brush = st.Kind switch
        {
            FixStatusKind.AtivoWinDiag => Ui.Res("OkBrush"),
            FixStatusKind.Disponivel => Ui.Res("InfoBrush"),
            _ => Ui.Res("MutedBrush"),
        };
        left.Children.Add(new TextBlock { Text = st.StatusText, Foreground = brush, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        g.Children.Add(left);

        if (fix.IsAction)
        {
            if (st.Kind == FixStatusKind.NaoAplicavel)
            {
                // Ação que não se aplica (ex.: spooler desativado de propósito): só o estado, sem botão.
                var plain = Ui.Card(g);
                if (inapplicable) plain.Opacity = 0.5;
                return plain;
            }
            var run = new Button
            {
                Content = fix.ShortName,
                Padding = new Thickness(14, 6, 14, 6),
                IsEnabled = facts.IsAdmin && !inapplicable && st.Kind != FixStatusKind.NaoAplicavel && !_working,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            if (!facts.IsAdmin) run.ToolTip = Loc.T("Alterações exigem executar como administrador.", "Changes require running as administrator.");
            System.Windows.Automation.AutomationProperties.SetName(run, fix.ShortName);
            run.Click += async (_, _) => await RunActionAsync(fix);
            Grid.SetColumn(run, 1);
            g.Children.Add(run);
            var actionCard = Ui.Card(g);
            if (inapplicable) actionCard.Opacity = 0.5;
            return actionCard;
        }

        var tg = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("SwitchToggle"),
            IsChecked = st.IsOn,
            IsEnabled = st.CanToggle && facts.IsAdmin && !inapplicable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        if (st.Kind == FixStatusKind.AtivoExterno) tg.ToolTip = LockedTip;
        else if (!facts.IsAdmin && st.CanToggle) tg.ToolTip = Loc.T("Alterações exigem executar como administrador.", "Changes require running as administrator.");
        System.Windows.Automation.AutomationProperties.SetName(tg, fix.ShortName);
        tg.Click += async (_, _) =>
        {
            // O interruptor só muda depois da confirmação e da releitura: volta ao estado real e a lista é refeita.
            tg.IsChecked = st.IsOn; // desfaz a mudança visual até confirmar
            await ToggleAsync(st);
        };
        Grid.SetColumn(tg, 1);
        g.Children.Add(tg);

        var card = Ui.Card(g);
        if (inapplicable) card.Opacity = 0.5;
        return card;
    }

    /// <summary>Selo de risco (baixo/médio/alto) com a cor da severidade.</summary>
    private static Border RiskBadge(FixDefinition fix)
    {
        var key = fix.Risk switch { FixRisk.Alto => "BadBrush", FixRisk.Medio => "WarnBrush", _ => "OkBrush" };
        return new Border
        {
            Background = Ui.Res(key),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Loc.T("Risco ", "Risk ") + fix.RiskLabel.ToLower(PtBr),
                Foreground = Ui.Res("BadgeTextBrush"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            },
        };
    }

    /// <summary>Cartão que abre a janela de apps na inicialização.</summary>
    private Border BuildStartupCard(SystemFacts facts)
    {
        var on = facts.StartupItems.Count(i => i.Enabled);
        var off = facts.StartupItems.Count - on;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = Loc.T("Apps na inicialização", "Startup apps"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        left.Children.Add(Ui.Muted(Loc.T($"{on} habilitado(s) e {off} desabilitado(s). Cada app pode ser ligado ou desligado individualmente, com snapshot.", $"{on} enabled and {off} disabled. Each app can be turned on or off individually, with a snapshot.")));
        g.Children.Add(left);
        var btn = new Button { Content = Loc.T("Gerenciar...", "Manage..."), Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        btn.Click += (_, _) =>
        {
            var w = new StartupAppsWindow(facts, _rescan);
            var owner = Window.GetWindow(this);
            if (owner is not null) w.Owner = owner;
            _beginBusy();
            try { w.ShowDialog(); }
            finally { _endBusy(); }
        };
        Grid.SetColumn(btn, 1);
        g.Children.Add(btn);
        return Ui.Card(g);
    }

    // ------------------------------------------------------------------ ações

    /// <summary>Liga (com confirmação e snapshot) ou desfaz (volta ao valor salvo) um item; risco alto exige digitar CONFIRMO.</summary>
    private async Task ToggleAsync(FixStatus st)
    {
        if (_working) return;
        var facts = _getFacts();
        if (facts is null) return;
        var fix = st.Fix;
        var owner = Window.GetWindow(this);

        if (st.Kind == FixStatusKind.AtivoWinDiag)
        {
            var when = (st.SourceFile?.Created)is { } created ? Fmt.DateTime(created) : Fmt.NA;
            if (!ConfirmDialog.Ask(owner, Loc.T("Desfazer otimização", "Undo optimization"), Loc.T($"Desfazer só este item? Volta ao valor salvo em {when}.", $"Undo only this item? It returns to the value saved on {when}."), fix.ShortName))
                return;
            await RunAsync(() => new FixEngine().Disable(st), Loc.T("Desfazendo...", "Undoing..."));
        }
        else if (st.Kind == FixStatusKind.Disponivel)
        {
            var body = Loc.T("Risco: ", "Risk: ") + $"{fix.RiskLabel}\n\n{fix.Explain}";
            if (fix.Reboot) body += Loc.T("\n\nEste item exige reiniciar o PC para ter efeito completo.", "\n\nThis item requires restarting the PC to take full effect.");
            body += Loc.T("\n\nUm snapshot com os valores atuais será gravado antes, e o item poderá ser desligado depois por este mesmo interruptor.", "\n\nA snapshot with the current values will be saved first, and the item can be turned off later with this same switch.");
            if (!ConfirmDialog.Ask(owner, Loc.T("Aplicar otimização", "Apply optimization"), fix.Name + "?", body,
                    requiredText: fix.Risk == FixRisk.Alto ? Loc.T("CONFIRMO", "CONFIRM") : null, warn: fix.Risk != FixRisk.Baixo))
                return;
            await RunAsync(() => new FixEngine().Enable(fix, facts), Loc.T("Aplicando...", "Applying..."));
        }
    }

    /// <summary>Executa uma ação pontual (ex.: reiniciar o spooler) depois de confirmação; não há desfazer.</summary>
    private async Task RunActionAsync(FixDefinition fix)
    {
        if (_working) return;
        var body = Loc.T("Risco: ", "Risk: ") + $"{fix.RiskLabel}\n\n{fix.Explain}"
            + Loc.T("\n\nEsta ação não pode ser desfeita.", "\n\nThis action cannot be undone.");
        if (!ConfirmDialog.Ask(Window.GetWindow(this), Loc.T("Executar ação", "Run action"), fix.Name + "?", body,
                requiredText: fix.Risk == FixRisk.Alto ? Loc.T("CONFIRMO", "CONFIRM") : null, warn: true,
                okText: Loc.T("Executar", "Run"), cancelText: Loc.T("Cancelar", "Cancel")))
            return;
        await RunAsync(() => new FixEngine().RunAction(fix), Loc.T("Executando...", "Running..."));
    }

    /// <summary>Confirma e limpa os temporários (não reversível; o aviso diz isso).</summary>
    private async Task CleanTempAsync()
    {
        if (_working) return;
        var facts = _getFacts();
        var mb = facts?.TempMB.ToString("0.#", PtBr) ?? Fmt.NA;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), Loc.T("Limpar temporários", "Clear temporary files"), Loc.T($"Limpar as pastas de arquivos temporários (cerca de {mb} MB)?", $"Clear the temporary files folders (about {mb} MB)?"),
                Loc.T("Arquivos em uso são ignorados. Esta limpeza não é reversível.", "Files in use are skipped. This cleanup is not reversible.")))
            return;
        await RunAsync(() => new FixEngine().CleanTemp(), Loc.T("Limpando...", "Clearing..."));
    }

    /// <summary>Executa a operação em thread de fundo com a camada de ocupado, mostra o resultado e refaz varredura e lista.</summary>
    private async Task RunAsync(Func<OpResult> work, string busyText)
    {
        _working = true;
        _beginBusy();
        _busyText.Text = busyText;
        _busy.Visibility = Visibility.Visible;
        try
        {
            OpResult res;
            try { res = await Task.Run(work); }
            catch (Exception ex) { res = new OpResult { Ok = false, Error = ex.Message }; }

            ShowResult(res);
            try { await _rescan(); } catch { /* a releitura falhar não invalida o resultado já exibido */ }
        }
        finally
        {
            _working = false;
            _busy.Visibility = Visibility.Collapsed;
            _endBusy();
            // Refaz a lista a partir do estado real: interruptores voltam ao valor verdadeiro se a operação falhou.
            await RefreshAsync();
        }
    }

    /// <summary>Mostra mensagens, erro e aviso de reinício do resultado da operação.</summary>
    private void ShowResult(OpResult res)
    {
        _logPanel.Children.Clear();
        foreach (var m in res.Messages)
            _logPanel.Children.Add(new TextBlock { Text = m, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(0, 1, 0, 1) });
        if (!res.Ok && !string.IsNullOrWhiteSpace(res.Error))
            _logPanel.Children.Add(new TextBlock { Text = Loc.T("Erro: ", "Error: ") + res.Error, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("BadBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        if (res.RebootNeeded)
            _logPanel.Children.Add(new TextBlock { Text = Loc.T("Reinicie o PC para concluir.", "Restart the PC to finish."), Foreground = Ui.Res("WarnBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        if (res.Ok && res.Messages.Count == 0)
            _logPanel.Children.Add(Ui.Muted(Loc.T("Concluído.", "Done.")));
        _logCard.Visibility = Visibility.Visible;
    }
}
