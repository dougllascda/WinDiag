using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Tools;

namespace WinDiag.App.Views;

/// <summary>Janela simples com o histórico das ferramentas executadas (mais recentes primeiro).</summary>
public sealed class ToolHistoryWindow : Window
{
    /// <summary>Cria a janela e lista o histórico do <paramref name="runner"/>; falha de leitura vira mensagem.</summary>
    public ToolHistoryWindow(ToolRunner runner)
    {
        Title = Loc.T("Histórico de ferramentas", "Tool history");
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Themes.ThemeManager.Register(this);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Ui.Muted(Loc.T("Ferramentas executadas por este aplicativo, da mais recente para a mais antiga.", "Tools run by this application, newest first."), 12.5));

        var list = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0), Content = list };
        SmoothScroll.Attach(scroll);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        try
        {
            var entries = runner.History(100);
            if (entries.Count == 0) list.Children.Add(Ui.Empty(Loc.T("Nenhuma ferramenta executada ainda.", "No tool has been run yet.")));
            foreach (var e in entries.OrderByDescending(x => x.When))
            {
                var sp = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = ToolCatalog.Get(e.ToolId)?.Name ?? e.ToolId, FontWeight = FontWeights.SemiBold });
                head.Children.Add(UiExtra.Text("  " + Fmt.DateTimeSec(e.When), "MutedBrush", 12, wrap: false));
                sp.Children.Add(head);
                sp.Children.Add(UiExtra.Text((e.Ok ? Loc.T("Concluída", "Completed") : Loc.T("Falhou", "Failed")) + (string.IsNullOrWhiteSpace(e.Summary) ? "" : ": " + e.Summary), e.Ok ? "OkBrush" : "BadBrush", 12.5));
                list.Children.Add(Ui.Card(sp));
            }
        }
        catch (Exception ex)
        {
            list.Children.Add(Ui.Muted(Loc.T("Não foi possível ler o histórico: ", "Could not read the history: ") + ex.Message, 13));
        }
    }
}
