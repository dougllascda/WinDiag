using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinDiag.App.Themes;
using WinDiag.Core;

namespace WinDiag.App.Views;

/// <summary>Janela "Sobre": logo, nome, versão, descrição, licença, repositório e nota de privacidade.</summary>
public sealed class AboutWindow : Window
{
    /// <summary>Cria a janela modal; defina <see cref="Window.Owner"/> antes de <c>ShowDialog</c>.</summary>
    public AboutWindow()
    {
        Title = Loc.T("Sobre o ", "About ") + AppInfo.Name;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Register(this);

        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new Image
        {
            Source = (ImageSource)Application.Current.FindResource("WinDiagLogo"),
            Width = 72,
            Height = 72,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        root.Children.Add(new TextBlock
        {
            Text = AppInfo.Name,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        });
        var ver = Ui.Muted(Loc.T("Versão ", "Version ") + AppInfo.Version, 13);
        ver.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(ver);

        var desc = new TextBlock
        {
            Text = Loc.T("Inventário de hardware, diagnóstico guiado e otimizações reversíveis para jogos no Windows.", "Hardware inventory, guided diagnostics and reversible optimizations for gaming on Windows."),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        root.Children.Add(desc);

        var card = new StackPanel();
        card.Children.Add(Row(Loc.T("Licença", "License"), new TextBlock { Text = "MIT · © 2026 dougllascda" }));
        var link = new Hyperlink(new Run(AppInfo.RepoUrl)) { NavigateUri = SafeUri(AppInfo.RepoUrl) };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        };
        var linkText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        linkText.Inlines.Add(link);
        var linkRow = Row(Loc.T("Repositório", "Repository"), linkText);
        card.Children.Add(linkRow);
        var cardBorder = Ui.Card(card);
        cardBorder.Margin = new Thickness(0, 24, 0, 0);
        root.Children.Add(cardBorder);

        var privacy = Ui.Muted(Loc.T("Sem telemetria; nada é enviado pela rede.", "No telemetry; nothing is sent over the network."), 12.5);
        privacy.HorizontalAlignment = HorizontalAlignment.Center;
        privacy.Margin = new Thickness(0, 0, 0, 0);
        root.Children.Add(privacy);

        var ok = new Button { Content = Loc.T("Fechar", "Close"), IsDefault = true, IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        ok.Click += (_, _) => Close();
        root.Children.Add(ok);

        Content = root;
    }

    private static Grid Row(string label, UIElement value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, Foreground = Ui.Res("MutedBrush") });
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        return g;
    }

    private static Uri SafeUri(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u : new Uri("https://github.com/");
}
