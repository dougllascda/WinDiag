using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinDiag.App.Themes;
using WinDiag.Core;
using WinDiag.Core.Knowledge;

namespace WinDiag.App.Views;

/// <summary>Janela "Como fazer na BIOS": fabricante detectado, tecla de entrada, passos, como voltar atrás e aviso.</summary>
public sealed class BiosGuideWindow : Window
{
    /// <summary>Texto simples do guia, no mesmo formato exibido, usado pelo botão "Copiar passos".</summary>
    private readonly string _plain;

    /// <summary>Abre o guia do tópico informado (texto do GuideTopic do achado/passo), usando a placa detectada.</summary>
    public static void ShowFor(string topicText)
    {
        var owner = Application.Current?.MainWindow;
        if (!BiosGuides.TryParse(topicText, out var topic))
        {
            MessageBox.Show(owner, Loc.T("Não há um guia de BIOS cadastrado para este item.", "There is no BIOS guide registered for this item."), "WinDiag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var board = (owner as MainWindow)?.CurrentFacts?.BoardModel;
        var guide = BiosGuides.For(topic, board);
        var w = new BiosGuideWindow(guide, board);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    /// <summary>Monta a janela a partir do guia já resolvido; tudo é feito em código, sem XAML.</summary>
    private BiosGuideWindow(BiosGuide guide, string? board)
    {
        Title = Loc.T("Como fazer na BIOS", "How to do it in the BIOS");
        Width = 580;
        Height = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ThemeManager.Register(this);

        string detected = string.IsNullOrWhiteSpace(board) ? Fmt.NA : board.Trim();
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        root.Children.Add(new TextBlock { Text = guide.Title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Ui.LabelValue(Loc.T("Placa-mãe detectada", "Detected motherboard"), detected));
        root.Children.Add(Ui.LabelValue(Loc.T("Guia para", "Guide for"), string.IsNullOrWhiteSpace(guide.Vendor) ? Fmt.NA : guide.Vendor));
        root.Children.Add(Ui.LabelValue(Loc.T("Tecla para entrar", "Key to enter"), string.IsNullOrWhiteSpace(guide.EnterKey) ? Fmt.NA : guide.EnterKey));

        var sb = new StringBuilder();
        sb.AppendLine(guide.Title);
        sb.AppendLine(Loc.T("Placa-mãe detectada: ", "Detected motherboard: ") + detected);
        sb.AppendLine(Loc.T("Tecla para entrar: ", "Key to enter: ") + (string.IsNullOrWhiteSpace(guide.EnterKey) ? Fmt.NA : guide.EnterKey));
        sb.AppendLine();

        root.Children.Add(Section(Loc.T("Passos", "Steps")));
        sb.AppendLine(Loc.T("Passos:", "Steps:"));
        for (int i = 0; i < guide.Steps.Count; i++)
        {
            var line = $"{i + 1}. {guide.Steps[i]}";
            sb.AppendLine(line);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var n = UiExtra.Text((i + 1) + ".", "AccentBrush", 13, FontWeights.Bold, wrap: false);
            var t = new TextBlock { Text = guide.Steps[i], TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(t, 1);
            row.Children.Add(n);
            row.Children.Add(t);
            root.Children.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(guide.Revert))
        {
            root.Children.Add(Section(Loc.T("Como voltar atrás", "How to revert")));
            root.Children.Add(new TextBlock { Text = guide.Revert, TextWrapping = TextWrapping.Wrap });
            sb.AppendLine().AppendLine(Loc.T("Como voltar atrás: ", "How to revert: ") + guide.Revert);
        }
        if (!string.IsNullOrWhiteSpace(guide.Caution))
        {
            var warn = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6) };
            warn.SetResourceReference(Border.BorderBrushProperty, "WarnBrush");
            warn.Child = UiExtra.Text(Loc.T("Atenção: ", "Caution: ") + guide.Caution, "WarnBrush", 13, FontWeights.SemiBold);
            root.Children.Add(warn);
            sb.AppendLine().AppendLine(Loc.T("Atenção: ", "Caution: ") + guide.Caution);
        }
        _plain = sb.ToString();

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        var copy = new Button { Content = Loc.T("Copiar passos", "Copy steps"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_plain); } catch (System.Runtime.InteropServices.ExternalException) { /* área de transferência ocupada */ }
        };
        var close = new Button { Content = Loc.T("Fechar", "Close"), MinWidth = 90, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 16) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(scroll);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;
    }

    /// <summary>Cabeçalho de seção em destaque (cor de acento).</summary>
    private static TextBlock Section(string text)
    {
        var t = UiExtra.Text(text, "AccentBrush", 14, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 14, 0, 4);
        return t;
    }
}
