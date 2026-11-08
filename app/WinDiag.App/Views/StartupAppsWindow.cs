using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Liga/desliga apps da inicialização do Windows (StartupApproved), com snapshot por alteração.</summary>
public sealed class StartupAppsWindow : Window
{
    private readonly SystemFacts _facts;
    private readonly Func<Task> _rescan;
    private readonly bool _isAdmin;
    private readonly StackPanel _log = new();
    private readonly List<(ToggleButton Toggle, bool Editable)> _toggles = new();
    private bool _changed;
    private bool _busy;

    /// <summary>Cria a janela; <paramref name="rescan"/> refaz a varredura ao fechar, se algo foi alterado.</summary>
    public StartupAppsWindow(SystemFacts facts, Func<Task> rescan)
    {
        _facts = facts;
        _rescan = rescan;
        _isAdmin = facts.IsAdmin;
        Title = Loc.T("Apps na inicialização", "Startup apps");
        Width = 780;
        Height = 620;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Themes.ThemeManager.Register(this);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Ui.Muted(Loc.T("Ligado = o app inicia com o Windows. Desligar não remove o programa: apenas marca a entrada como desativada, e cada alteração grava um snapshot.", "On = the app starts with Windows. Turning it off does not remove the program: it only marks the entry as disabled, and each change writes a snapshot."), 12.5));

        var list = new StackPanel();
        if (facts.StartupItems.Count == 0) list.Children.Add(Ui.Empty(Loc.T("Nenhum item de inicialização encontrado.", "No startup items found.")));
        foreach (var it in facts.StartupItems.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            list.Children.Add(BuildRow(it));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12), Content = list };
        SmoothScroll.Attach(scroll);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var logScroll = new ScrollViewer { MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _log };
        Grid.SetRow(logScroll, 2);
        root.Children.Add(logScroll);
        Content = root;

        // Não fecha no meio de uma alteração (o snapshot e a mudança precisam terminar juntos).
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += async (_, _) => { if (_changed) { try { await _rescan(); } catch { } } };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var (t, editable) in _toggles) t.IsEnabled = !busy && editable && _isAdmin;
    }

    private static string Shorten(string s) => s.Length <= 90 ? s : s[..87] + "...";

    private Border BuildRow(StartupItem it)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = it.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Muted(Loc.T("Local: ", "Location: ") + it.Location));
        if (!string.IsNullOrWhiteSpace(it.Command))
        {
            var cmd = Ui.Muted(Shorten(it.Command), 11.5);
            cmd.ToolTip = it.Command;
            text.Children.Add(cmd);
        }
        g.Children.Add(text);

        var editable = it.ApprovedPath is not null && it.ValueName is not null;
        var tg = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("SwitchToggle"),
            IsChecked = it.Enabled,
            IsEnabled = editable && _isAdmin,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        if (!editable) tg.ToolTip = Loc.T("Este item não tem entrada StartupApproved editável.", "This item has no editable StartupApproved entry.");
        else if (!_isAdmin) tg.ToolTip = Loc.T("Alterações exigem executar como administrador.", "Changes require running as administrator.");
        _toggles.Add((tg, editable));
        System.Windows.Automation.AutomationProperties.SetName(tg, it.Name);
        tg.Click += async (_, _) =>
        {
            if (_busy) { tg.IsChecked = it.Enabled; return; }
            var want = tg.IsChecked == true;
            SetBusy(true); // uma operação por vez: evita snapshots concorrentes
            OpResult res;
            try { res = await Task.Run(() => new FixEngine().SetStartupApp(it, want)); }
            catch (Exception ex) { res = new OpResult { Ok = false, Error = ex.Message }; }
            if (res.Ok)
            {
                it.Enabled = want;
                _changed = true;
            }
            else tg.IsChecked = it.Enabled; // volta ao estado real
            SetBusy(false);
            _log.Children.Clear();
            foreach (var m in res.Messages)
                _log.Children.Add(new TextBlock { Text = m, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Res("MutedBrush") });
            if (!res.Ok && res.Error is not null)
                _log.Children.Add(new TextBlock { Text = Loc.T("Erro: ", "Error: ") + res.Error, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("BadBrush") });
        };
        Grid.SetColumn(tg, 1);
        g.Children.Add(tg);
        return Ui.Card(g);
    }
}
