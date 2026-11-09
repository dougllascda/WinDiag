using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Work;

namespace WinDiag.App.Views;

/// <summary>
/// Cartão "Programas de fábrica" (perfil Trabalho): lista de <see cref="SystemFacts.Bloat"/> agrupada por veredito.
/// O WinDiag só abre o desinstalador do próprio programa; não remove nada sozinho.
/// </summary>
public sealed class BloatCardView : UserControl
{
    /// <summary>Leitor da última varredura (nulo enquanto ela roda).</summary>
    private readonly Func<SystemFacts?> _getFacts;
    /// <summary>Refaz a varredura da janela principal (botão "Ler de novo").</summary>
    private readonly Func<Task> _rescan;
    /// <summary>Avisa a janela principal que há operação em curso (bloqueia a navegação).</summary>
    private readonly Action _beginBusy;
    /// <summary>Encerra o aviso de operação em curso.</summary>
    private readonly Action _endBusy;
    /// <summary>Conteúdo do cartão; reconstruído a cada <see cref="Refresh"/>.</summary>
    private readonly StackPanel _body = new();
    /// <summary>Mensagem de resultado da última tentativa de desinstalar.</summary>
    private readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    /// <summary>Botão "Ler de novo", mostrado depois que um desinstalador é aberto (a lista fica desatualizada).</summary>
    private Button _reload;
    /// <summary>true depois que um desinstalador foi aberto; o botão "Ler de novo" (recriado a cada Rebuild) aparece enquanto a lista estiver desatualizada.</summary>
    private bool _showReload;
    /// <summary>true enquanto um desinstalador está sendo aberto; evita cliques repetidos.</summary>
    private bool _launching;

    /// <summary>Cria o cartão; <paramref name="getFacts"/> devolve a última leitura completa (null durante a varredura).</summary>
    public BloatCardView(Func<SystemFacts?> getFacts, Func<Task> rescan, Action beginBusy, Action endBusy)
    {
        _getFacts = getFacts;
        _rescan = rescan;
        _beginBusy = beginBusy;
        _endBusy = endBusy;
        _reload = NewReloadButton();
        Content = Ui.Card(_body);
        SafeRebuild();
    }

    /// <summary>Cria um botão "Ler de novo" novo (um Button WPF só pode ter um pai, então não é reaproveitado entre reconstruções).</summary>
    private Button NewReloadButton()
    {
        var btn = new Button { Content = Loc.T("Ler de novo", "Scan again"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0), Visibility = _showReload ? Visibility.Visible : Visibility.Collapsed };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            try { await _rescan(); } catch { /* a releitura falhar não invalida a tela */ }
            finally { btn.IsEnabled = true; }
        };
        return btn;
    }

    /// <summary>Redesenha a lista a partir da leitura mais recente.</summary>
    public void Refresh() => SafeRebuild();

    /// <summary>Reconstrói o cartão; se algo falhar, mostra o erro no próprio cartão em vez de derrubar a aba.</summary>
    private void SafeRebuild()
    {
        try { Rebuild(); }
        catch (Exception ex)
        {
            _body.Children.Clear();
            _body.Children.Add(Ui.CardTitleInfo(Loc.T("Programas de fábrica", "Factory-installed programs"), "Programas de fábrica"));
            _body.Children.Add(Ui.Empty(Loc.T("Não foi possível montar a lista: ", "Could not build the list: ") + ex.Message));
        }
    }

    /// <summary>Reconstrói título, grupos por veredito e botões. "Útil, manter" começa recolhido para o usuário focar no que é removível.</summary>
    private void Rebuild()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.CardTitleInfo(Loc.T("Programas de fábrica", "Factory-installed programs"), "Programas de fábrica"));
        _body.Children.Add(Ui.Muted(Loc.T(
            "Programas que costumam vir instalados pelo fabricante do PC. A classificação é uma sugestão baseada no nome e no fabricante; confira antes de remover. O WinDiag só abre o desinstalador do próprio programa.",
            "Programs that usually ship pre-installed by the PC maker. The classification is a suggestion based on name and publisher; check before removing. WinDiag only opens the program's own uninstaller."), 12.5));

        var facts = _getFacts();
        if (facts is null)
        {
            _body.Children.Add(Ui.Empty(Loc.T("Aguardando a leitura...", "Waiting for the scan...")));
            return;
        }

        var list = facts.Bloat ?? new List<BloatItem>();
        if (list.Count == 0) _body.Children.Add(Ui.Empty(Loc.T("Nenhum programa de fábrica identificado nesta leitura.", "No factory-installed program identified in this scan.")));

        AddGroup(list, BloatVerdict.Remove, Loc.T("Remover", "Remove"), "WarnBrush", collapsed: false);
        AddGroup(list, BloatVerdict.Optional, Loc.T("Opcional", "Optional"), "InfoBrush", collapsed: false);
        AddGroup(list, BloatVerdict.Keep, Loc.T("Útil, manter", "Useful, keep"), "OkBrush", collapsed: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var settings = new Button { Content = Loc.T("Abrir Configurações > Aplicativos", "Open Settings > Apps"), Padding = new Thickness(12, 5, 12, 5) };
        settings.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); }
            catch (Exception ex) { Say(Loc.T("Não foi possível abrir as Configurações: ", "Could not open Settings: ") + ex.Message, "WarnBrush"); }
        };
        row.Children.Add(settings);
        _reload = NewReloadButton();
        row.Children.Add(_reload);
        _body.Children.Add(row);
        _body.Children.Add(_msg);
    }

    /// <summary>Cria o grupo; os grupos recolhidos só montam as linhas na primeira abertura (listas grandes ficam leves).</summary>
    private void AddGroup(List<BloatItem> all, BloatVerdict verdict, string title, string brushKey, bool collapsed)
    {
        var items = all.Where(b => b.Verdict == verdict).OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (items.Count == 0) return;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4), Background = System.Windows.Media.Brushes.Transparent };
        var chevron = UiExtra.Icon(collapsed ? "" : "", 11, "MutedBrush");
        chevron.Margin = new Thickness(0, 0, 8, 0);
        if (collapsed) head.Children.Add(chevron);
        head.Children.Add(UiExtra.Text(title, brushKey, 14, FontWeights.SemiBold, wrap: false));
        head.Children.Add(UiExtra.Text("  (" + items.Count.ToString(Loc.Culture) + ")", "MutedBrush", 13, wrap: false));
        _body.Children.Add(head);

        var panel = new StackPanel();
        bool built = false;
        void Build()
        {
            if (built) return;
            built = true;
            foreach (var b in items) panel.Children.Add(ItemRow(b));
        }
        if (!collapsed)
        {
            Build();
        }
        else
        {
            panel.Visibility = Visibility.Collapsed;
            head.Cursor = Cursors.Hand;
            head.Focusable = true;
            void Toggle()
            {
                bool show = panel.Visibility != Visibility.Visible;
                if (show) Build();
                panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = show ? "" : "";
            }
            head.MouseLeftButtonUp += (_, _) => Toggle();
            head.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    e.Handled = true;
                    Toggle();
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(head, title + Loc.T(": mostrar ou ocultar a lista", ": show or hide the list"));
        }
        _body.Children.Add(panel);
    }

    /// <summary>Linha de um programa; o botão abre o desinstalador do próprio programa (nunca remove sozinho).</summary>
    private Border ItemRow(BloatItem b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(UiExtra.Text(string.IsNullOrWhiteSpace(b.Name) ? Fmt.NA : b.Name, "TextBrush", 13.5, FontWeights.SemiBold));
        var meta = new List<string>();
        meta.Add(Loc.T("Fabricante: ", "Publisher: ") + (string.IsNullOrWhiteSpace(b.Publisher) ? Fmt.NA : b.Publisher));
        if (!string.IsNullOrWhiteSpace(b.Version)) meta.Add(Loc.T("Versão ", "Version ") + b.Version);
        left.Children.Add(UiExtra.Text(string.Join(" · ", meta), "MutedBrush", 12));
        if (!string.IsNullOrWhiteSpace(b.Reason)) left.Children.Add(UiExtra.Text(Loc.T("Motivo: ", "Reason: ") + b.Reason, "MutedBrush", 12));
        g.Children.Add(left);

        bool canLaunch = b.IsAppx || !string.IsNullOrWhiteSpace(b.UninstallCommand);
        var btn = new Button
        {
            Content = Loc.T("Desinstalar…", "Uninstall…"),
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = canLaunch,
        };
        if (!canLaunch) btn.ToolTip = Loc.T("Este programa não informa um desinstalador. Use Configurações > Aplicativos.", "This program does not provide an uninstaller. Use Settings > Apps.");
        System.Windows.Automation.AutomationProperties.SetName(btn, Loc.T("Desinstalar ", "Uninstall ") + b.Name);
        btn.Click += async (_, _) => await LaunchAsync(b);
        Grid.SetColumn(btn, 1);
        g.Children.Add(btn);

        var row = new Border { Child = g, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(6) };
        row.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        return row;
    }

    /// <summary>Exibe uma mensagem colorida com a chave de pincel do tema (acompanha a troca de tema).</summary>
    private void Say(string text, string brushKey)
    {
        _msg.Text = text;
        _msg.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        _msg.Visibility = Visibility.Visible;
    }

    /// <summary>Confirma e abre o desinstalador em thread de fundo, bloqueando a navegação enquanto isso (não há desfazer: o aviso diz isso).</summary>
    private async Task LaunchAsync(BloatItem b)
    {
        if (_launching) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), Loc.T("Desinstalar programa", "Uninstall program"), b.Name + "?",
                Loc.T("Abre o desinstalador do próprio programa. O WinDiag não remove nada sozinho e não consegue desfazer isso.",
                      "Opens the program's own uninstaller. WinDiag does not remove anything by itself and cannot undo this."),
                warn: true, okText: Loc.T("Abrir desinstalador", "Open uninstaller"), cancelText: Loc.T("Cancelar", "Cancel")))
            return;

        _launching = true;
        _beginBusy();
        try
        {
            OpResult res;
            try { res = await Task.Run(() => Uninstaller.Launch(b)); }
            catch (Exception ex) { res = new OpResult { Ok = false, Error = ex.Message }; }

            if (res.Ok)
            {
                Say(Loc.T("Desinstalador aberto. Quando terminar, use \"Ler de novo\" para atualizar a lista.", "Uninstaller opened. When it finishes, use \"Scan again\" to update the list."), "OkBrush");
                _showReload = true;
                _reload.Visibility = Visibility.Visible;
            }
            else
            {
                Say(Loc.T("Não foi possível abrir o desinstalador: ", "Could not open the uninstaller: ") + (string.IsNullOrWhiteSpace(res.Error) ? Fmt.NA : res.Error), "BadBrush");
            }
        }
        finally
        {
            _launching = false;
            _endBusy();
        }
    }
}
