using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDiag.App.Themes;
using WinDiag.Core;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Diálogo da primeira abertura: "Para que você usa este PC?" com dois cartões (Jogos e Trabalho).</summary>
public sealed class ProfileChooserWindow : Window
{
    /// <summary>Perfil escolhido; null se a janela foi fechada sem escolha.</summary>
    public UsageProfile? Chosen { get; private set; }

    /// <summary>Construtor privado: use <see cref="Ask"/>.</summary>
    private ProfileChooserWindow()
    {
        Title = "WinDiag";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Register(this);

        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(new TextBlock { Text = Loc.T("Para que você usa este PC?", "What do you use this PC for?"), FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Ui.Muted(Loc.T("O WinDiag ajusta as verificações, a nota, o plano e as otimizações ao uso escolhido.", "WinDiag adapts the checks, score, plan and optimizations to the chosen use."), 13));

        var row = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var gaming = ChoiceCard(UsageProfile.Gaming, Loc.T("Jogos", "Gaming"),
            Loc.T("Desempenho em jogos: GPU, PCIe, monitores, engasgos", "Gaming performance: GPU, PCIe, displays, stutter"));
        var work = ChoiceCard(UsageProfile.Work, Loc.T("Trabalho", "Work"),
            Loc.T("Escritório e notebook: impressoras, programas de fábrica, bateria, atualizações, inicialização", "Office and laptop: printers, factory-installed programs, battery, updates, startup"));
        gaming.Margin = new Thickness(0, 0, 8, 0);
        work.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(work, 1);
        row.Children.Add(gaming);
        row.Children.Add(work);
        root.Children.Add(row);

        var hint = Ui.Muted(Loc.T("Você pode trocar depois em Configurações.", "You can change this later in Settings."), 12.5);
        hint.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(hint);
        Content = root;
    }

    /// <summary>Cartão clicável (mouse, Enter ou Espaço) que escolhe o perfil e fecha o diálogo; o foco por teclado também destaca a borda.</summary>
    private Border ChoiceCard(UsageProfile profile, string title, string text)
    {
        var body = new StackPanel();
        var icon = UiExtra.Icon(ProfileUi.Glyph(profile), 40, "AccentBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(icon);
        var t = UiExtra.Text(title, "TextBrush", 18, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 10, 0, 4);
        body.Children.Add(t);
        body.Children.Add(UiExtra.Text(text, "MutedBrush", 12.5));

        var card = Ui.Card(body);
        card.Margin = new Thickness(0);
        card.Padding = new Thickness(18);
        card.MinHeight = 190;
        card.Cursor = Cursors.Hand;
        card.Focusable = true;
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.GotKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        card.LostKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        void Pick()
        {
            Chosen = profile;
            DialogResult = true;
        }
        card.MouseLeftButtonUp += (_, _) => Pick();
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                Pick();
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(card, title + ": " + text);
        return card;
    }

    /// <summary>Mostra o diálogo e devolve o perfil escolhido (null se fechado sem escolher).</summary>
    public static UsageProfile? Ask(Window? owner)
    {
        var w = new ProfileChooserWindow();
        if (owner is { IsLoaded: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Chosen;
    }
}
