using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Live;

namespace WinDiag.App.Views;

/// <summary>Aba "Sessão de jogo": lista apps pesados e fecha só os que o usuário marcar.</summary>
public sealed class GameSessionView : UserControl
{
    /// <summary>Lista de apps candidatos, um CheckBox por app.</summary>
    private readonly StackPanel _list = new();
    /// <summary>Texto da RAM disponível (antes/depois do fechamento).</summary>
    private readonly TextBlock _ramText;
    /// <summary>Linha de status/resultado da operação atual.</summary>
    private readonly TextBlock _status;
    /// <summary>Botão que refaz a lista de apps pesados.</summary>
    private readonly Button _refresh = new() { Content = Loc.T("Atualizar lista", "Refresh list"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
    /// <summary>Botão que pede o fechamento dos apps marcados.</summary>
    private readonly Button _close = new() { Content = Loc.T("Fechar selecionados", "Close selected"), Padding = new Thickness(16, 6, 16, 6) };
    /// <summary>Caixa de seleção de cada app e o app correspondente.</summary>
    private readonly List<(CheckBox Box, HeavyApp App)> _items = new();
    /// <summary>true durante listagem ou fechamento; evita operações simultâneas.</summary>
    private bool _busy;

    /// <summary>Monta a aba de sessão de jogo.</summary>
    public GameSessionView()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("Sessão de jogo", "Game session"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });

        var body = new StackPanel();
        body.Children.Add(Ui.CardTitle(Loc.T("Apps em segundo plano que mais usam memória", "Background apps using the most memory")));
        body.Children.Add(Ui.Muted(Loc.T("Pede o fechamento apenas dos apps que você marcar. Processos de sistema e antivírus nunca aparecem. Não exige administrador. Apps com janela que não fecharem em 5 s não são forçados; apps sem janela podem ser encerrados.", "Requests closing only the apps you select. System processes and antivirus never appear. Does not require administrator. Apps with a window that do not close within 5 s are not forced; apps without a window may be terminated.")));
        _ramText = Ui.Muted(Loc.T("RAM disponível: n/d", "Available RAM: n/a"), 13);
        _ramText.Foreground = Ui.Res("TextBrush");
        _ramText.Margin = new Thickness(0, 8, 0, 8);
        body.Children.Add(_ramText);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(_refresh);
        bar.Children.Add(_close);
        body.Children.Add(bar);
        body.Children.Add(_list);
        _status = Ui.Muted("");
        body.Children.Add(_status);
        root.Children.Add(Ui.Card(body));
        Content = root;

        _refresh.Click += async (_, _) => await RefreshAsync();
        _close.Click += async (_, _) => await CloseSelectedAsync();
        Loaded += async (_, _) => { if (_items.Count == 0) await RefreshAsync(); };
    }

    /// <summary>Formata MB como GB com uma casa, ou "n/d".</summary>
    private static string Ram(double? mb) => mb.HasValue ? Fmt.Num(mb.Value / 1024d, 1) + " GB" : Fmt.NA;

    /// <summary>Lista os apps pesados fora da UI thread (enumerar processos é lento) e reconstrói a lista.</summary>
    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true, Loc.T("Procurando apps…", "Looking for apps…"));
        List<HeavyApp> apps = new();
        double? avail = null;
        try { await Task.Run(() => { apps = GameSession.ListHeavyApps(); avail = GameSession.AvailableMB(); }); }
        catch { }
        _items.Clear();
        _list.Children.Clear();
        _ramText.Text = Loc.T("RAM disponível agora: ", "Available RAM now: ") + Ram(avail);
        if (apps.Count == 0)
            _list.Children.Add(Ui.Empty(Loc.T("Nenhum app pesado candidato encontrado (≥ 150 MB). Nada a fechar.", "No heavy candidate app found (≥ 150 MB). Nothing to close.")));
        foreach (var a in apps)
        {
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            var cb = new CheckBox
            {
                Content = Loc.T($"{a.Name}  ({a.Count} processo(s), {Fmt.Num(a.RamMB, 0)} MB)", $"{a.Name}  ({a.Count} process(es), {Fmt.Num(a.RamMB, 0)} MB)"),
                Foreground = Ui.Res("TextBrush"),
                ToolTip = a.Path
            };
            row.Children.Add(cb);
            if (!string.IsNullOrEmpty(a.Warning))
            {
                var w = Ui.Muted(Loc.T("Atenção: ", "Caution: ") + a.Warning, 12);
                w.Foreground = Ui.Res("WarnBrush");
                w.Margin = new Thickness(22, 0, 0, 0);
                row.Children.Add(w);
            }
            _list.Children.Add(row);
            _items.Add((cb, a));
        }
        SetBusy(false, "");
    }

    /// <summary>Confirma e pede o fechamento normal (sem forçar) dos apps marcados; a operação roda em thread de fundo.</summary>
    private async Task CloseSelectedAsync()
    {
        if (_busy) return;
        var chosen = _items.Where(i => i.Box.IsChecked == true).Select(i => i.App).ToList();
        if (chosen.Count == 0) { _status.Text = Loc.T("Nada selecionado. Nada foi fechado.", "Nothing selected. Nothing was closed."); return; }
        var list = string.Join("\n", chosen.Select(a => $" - {a.Name} ({Fmt.Num(a.RamMB, 0)} MB)"));
        var text = Loc.T("Será pedido o fechamento de:\n\n", "Closing has been requested for:\n\n") + list +
                   Loc.T("\n\nSerá pedido o fechamento normal da janela de cada app. Apps com janela que não fecharem em 5 s NÃO serão forçados a encerrar, pois podem ter algo a salvar; eles continuarão abertos e serão indicados como \"não fechou\". Salve seu trabalho antes de continuar.\nConfirmar o pedido de fechamento?",
                         "\n\nA normal close request will be sent to each app window. Apps with a window that do not close within 5 s will NOT be forced to quit, since they may have something to save; they will stay open and be reported as \"did not close\". Save your work before continuing.\nConfirm the close request?");
        if (MessageBox.Show(text, Loc.T("Confirmar fechamento", "Confirm closing"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _status.Text = Loc.T("Cancelado. Nada foi fechado.", "Cancelled. Nothing was closed.");
            return;
        }
        SetBusy(true, Loc.T("Pedindo o fechamento (até 5 s por app)…", "Requesting close (up to 5 s per app)…"));
        CloseResult? res = null;
        try { res = await Task.Run(() => GameSession.Close(chosen)); }
        catch (Exception ex) { _status.Text = Loc.T("Falha ao fechar: ", "Failed to close: ") + ex.Message; }
        if (res is not null)
        {
            _ramText.Text = Loc.T("RAM disponível: ", "Available RAM: ") + $"{Ram(res.AvailBeforeMB)} -> {Ram(res.AvailAfterMB)}";
            _status.Text = string.Join("\n", res.Lines) +
                Loc.T("\nAlguns apps podem reabrir sozinhos (inicialização automática).", "\nSome apps may reopen on their own (automatic startup).");
        }
        await RefreshAfterCloseAsync(res?.AvailAfterMB, res?.AvailBeforeMB);
    }

    /// <summary>Atualiza a lista depois de fechar preservando a mensagem de resultado e a RAM antes/depois, que a nova listagem apagaria.</summary>
    private async Task RefreshAfterCloseAsync(double? after, double? before)
    {
        var keepStatus = _status.Text;
        var keepRam = _ramText.Text;
        SetBusy(false, "");
        await RefreshAsync();
        if (before.HasValue || after.HasValue) _ramText.Text = keepRam;
        _status.Text = keepStatus;
    }

    /// <summary>Alterna o estado ocupado: desabilita os botões e define o texto de status.</summary>
    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _refresh.IsEnabled = !busy;
        _close.IsEnabled = !busy;
        _status.Text = status;
    }
}
