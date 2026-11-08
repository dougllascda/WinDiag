using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Renderização genérica de uma SectionResult: grupos, tabelas, verificações e notas.</summary>
public sealed class SectionView : UserControl
{
    /// <summary>Monta a visualização de uma seção; <paramref name="showTitle"/> exibe o título acima dos cartões.</summary>
    public SectionView(SectionResult r, bool showTitle)
    {
        var root = new StackPanel();
        if (showTitle)
        {
            var title = new TextBlock { Text = r.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) };
            root.Children.Add(Ui.WithInfo(title, r.Title));
        }

        if (r.Groups.Count == 0 && r.Findings.Count == 0)
            root.Children.Add(Ui.Empty());

        foreach (var g in r.Groups)
        {
            var body = new StackPanel();
            body.Children.Add(Ui.CardTitleInfo(g.Title));
            foreach (var row in g.Rows)
                body.Children.Add(Ui.LabelValue(row.Label, row.Value, row.Sev.HasValue ? Ui.SevBrush(row.Sev.Value) : null));
            foreach (var t in g.Tables)
            {
                if (!string.IsNullOrWhiteSpace(t.Title))
                    body.Children.Add(new TextBlock { Text = t.Title, Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(0, 8, 0, 3), FontWeight = FontWeights.SemiBold });
                body.Children.Add(Ui.Table(t));
            }
            foreach (var n in g.Notes) body.Children.Add(Ui.Muted(n));
            root.Children.Add(Ui.Card(body));
        }

        if (r.Findings.Count > 0)
        {
            var body = new StackPanel();
            body.Children.Add(Ui.CardTitle(Loc.T("Verificações", "Checks")));
            var ordered = r.Findings.OrderByDescending(f => f.Sev).ToList();
            body.Children.Add(Ui.FindingList(ordered));
            root.Children.Add(Ui.Card(body));
        }

        if (r.Notes.Count > 0)
        {
            var notes = new StackPanel { Margin = new Thickness(2, 0, 0, 10) };
            foreach (var n in r.Notes) notes.Children.Add(Ui.Muted("• " + n));
            root.Children.Add(notes);
        }
        Content = root;
    }
}
