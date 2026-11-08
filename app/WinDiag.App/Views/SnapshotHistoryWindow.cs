using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Fixes;

namespace WinDiag.App.Views;

/// <summary>Histórico de snapshots com restauração do snapshot inteiro.</summary>
public sealed class SnapshotHistoryWindow : Window
{
    private readonly Func<Task> _rescan;
    private readonly StackPanel _list = new();
    private readonly StackPanel _log = new();
    private readonly Border _busy;
    private bool _changed;

    /// <summary>Cria a janela; <paramref name="rescan"/> refaz a varredura ao fechar se algo foi restaurado.</summary>
    public SnapshotHistoryWindow(Func<Task> rescan, bool isAdmin)
    {
        _rescan = rescan;
        _isAdmin = isAdmin;
        Title = Loc.T("Histórico de snapshots", "Snapshot history");
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Themes.ThemeManager.Register(this);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Ui.Muted(Loc.T("Cada alteração feita pelo WinDiag grava um snapshot com os valores anteriores. Restaurar um snapshot volta todos os itens reversíveis dele; itens marcados como não reversíveis são ignorados.", "Every change made by WinDiag writes a snapshot with the previous values. Restoring a snapshot reverts all of its reversible items; items marked as not reversible are skipped."), 12.5));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12) };
        SmoothScroll.Attach(scroll);
        scroll.Content = _list;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var logScroll = new ScrollViewer { MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _log };
        Grid.SetRow(logScroll, 2);
        root.Children.Add(logScroll);

        _busy = new Border
        {
            Background = Ui.Res("OverlayBrush"),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = Loc.T("Aplicando...", "Applying..."), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Grid.SetRowSpan(_busy, 3);
        root.Children.Add(_busy);
        Content = root;

        Load();
        Closing += (_, e) => { if (_working) e.Cancel = true; };
        Closed += async (_, _) => { if (_changed) { try { await _rescan(); } catch { } } };
    }

    private readonly bool _isAdmin;
    private bool _working;

    private void Load()
    {
        _list.Children.Clear();
        List<SnapshotFile> snaps;
        try { snaps = new SnapshotStore().List(); }
        catch { snaps = new(); }
        if (snaps.Count == 0)
        {
            _list.Children.Add(Ui.Empty(Loc.T("Nenhum snapshot encontrado.", "No snapshots found.")));
            return;
        }
        foreach (var s in snaps)
        {
            var sp = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = Fmt.DateTimeSec(s.Created),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var btn = new Button { Content = Loc.T("Restaurar este snapshot", "Restore this snapshot"), Padding = new Thickness(10, 4, 10, 4), IsEnabled = _isAdmin };
            if (!_isAdmin) btn.ToolTip = Loc.T("Exige executar como administrador.", "Requires running as administrator.");
            var snap = s;
            btn.Click += async (_, _) => await RestoreAsync(snap);
            Grid.SetColumn(btn, 1);
            head.Children.Add(btn);
            sp.Children.Add(head);
            foreach (var f in s.Data.Fixes)
            {
                var action = f.IsApply ? Loc.T("Aplicar", "Apply") : Loc.T("Desfazer", "Undo");
                var t = new TextBlock
                {
                    Text = $"{action}: {f.Name}" + (f.Reversible ? "" : Loc.T("  (não reversível)", "  (not reversible)")),
                    Foreground = f.Reversible ? Ui.Res("TextBrush") : Ui.Res("WarnBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 3, 0, 0),
                    FontSize = 12.5,
                };
                sp.Children.Add(t);
            }
            _list.Children.Add(Ui.Card(sp));
        }
    }

    private async Task RestoreAsync(SnapshotFile snap)
    {
        if (_working) return;
        var when = Fmt.DateTimeSec(snap.Created);
        if (!ConfirmDialog.Ask(this, Loc.T("Restaurar snapshot", "Restore snapshot"), Loc.T($"Restaurar o snapshot de {when}?", $"Restore the snapshot from {when}?"),
                Loc.T("Os itens reversíveis voltam aos valores salvos. Antes disso, o estado atual é gravado em um novo snapshot, então a restauração também pode ser desfeita.", "Reversible items return to their saved values. Before that, the current state is written to a new snapshot, so the restore can also be undone.")))
            return;
        _working = true;
        _busy.Visibility = Visibility.Visible;
        OpResult res;
        try { res = await Task.Run(() => new FixEngine().RestoreSnapshot(snap)); }
        catch (Exception ex) { res = new OpResult { Ok = false, Error = ex.Message }; }
        finally
        {
            _working = false;
            _busy.Visibility = Visibility.Collapsed;
        }
        _changed = true;

        _log.Children.Clear();
        foreach (var m in res.Messages)
            _log.Children.Add(new TextBlock { Text = m, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Res("MutedBrush") });
        if (!res.Ok && res.Error is not null)
            _log.Children.Add(new TextBlock { Text = Loc.T("Erro: ", "Error: ") + res.Error, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("BadBrush") });
        if (res.RebootNeeded)
            _log.Children.Add(new TextBlock { Text = Loc.T("Reinicie o PC para concluir.", "Restart the PC to finish."), Foreground = Ui.Res("WarnBrush"), FontWeight = FontWeights.SemiBold });
        Load();
    }
}
