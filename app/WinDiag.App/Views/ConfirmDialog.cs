using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDiag.Core;

namespace WinDiag.App.Views;

/// <summary>Janela modal de confirmação (Sim/Não). Com requiredText, o botão só habilita com o texto exato.</summary>
public sealed class ConfirmDialog : Window
{
    private readonly Button _ok;

    private ConfirmDialog(string title, string headline, string? body, Brush? headlineBrush, string? requiredText, string okText, string cancelText)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Themes.ThemeManager.Register(this);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = headlineBrush ?? Ui.Res("TextBrush"),
        });
        if (!string.IsNullOrWhiteSpace(body))
            root.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Res("MutedBrush") });

        _ok = new Button { Content = okText, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        if (requiredText is not null)
        {
            root.Children.Add(new TextBlock
            {
                Text = Loc.T($"Risco alto. Digite {requiredText} para confirmar.", $"High risk. Type {requiredText} to confirm."),
                Margin = new Thickness(0, 12, 0, 4),
                Foreground = Ui.Res("BadBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            var tb = new TextBox();
            tb.TextChanged += (_, _) => _ok.IsEnabled = tb.Text == requiredText;
            root.Children.Add(tb);
            _ok.IsEnabled = false;
            Loaded += (_, _) => tb.Focus();
        }
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = cancelText, MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Pergunta Sim/Não. Retorna true se o usuário confirmou.</summary>
    public static bool Ask(Window? owner, string title, string headline, string? body = null,
        string? requiredText = null, bool warn = false, string? okText = null, string? cancelText = null)
    {
        var d = new ConfirmDialog(title, headline, body, warn ? Ui.Res("WarnBrush") : null, requiredText, okText ?? Loc.T("Sim", "Yes"), cancelText ?? Loc.T("Não", "No"));
        if (owner is { IsLoaded: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true;
    }
}
