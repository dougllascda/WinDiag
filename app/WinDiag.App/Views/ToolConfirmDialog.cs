using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Tools;

namespace WinDiag.App.Views;

/// <summary>Resultado da confirmação de uma ferramenta: se confirmou, se pediu o ponto de restauração e as opções marcadas.</summary>
/// <param name="Confirmed">true se o usuário confirmou.</param>
/// <param name="RestorePoint">true se marcou "Criar ponto de restauração antes".</param>
/// <param name="OptionIds">Ids das opções marcadas (vazio se a ferramenta não tem opções).</param>
public sealed record ToolConfirmResult(bool Confirmed, bool RestorePoint, IReadOnlyList<string> OptionIds);

/// <summary>Janela de confirmação de uma ferramenta: o que faz, o que será executado, avisos, opções com tamanho e ponto de restauração.</summary>
public sealed class ToolConfirmDialog : Window
{
    /// <summary>Botão de confirmação; fica desabilitado enquanto as opções são calculadas.</summary>
    private readonly Button _ok;
    /// <summary>Caixa "Criar ponto de restauração antes" (null se não for oferecida).</summary>
    private readonly CheckBox? _restore;
    /// <summary>Caixas das opções (cada uma com o id da opção e o tamanho).</summary>
    private readonly List<(CheckBox Box, ToolOption Option)> _options = new();
    /// <summary>Caixa "Estou ciente" (null se a ferramenta não tem aviso de confirmação).</summary>
    private CheckBox? _ack;
    /// <summary>true se a ferramenta tem opções a escolher.</summary>
    private bool _hasOptions;
    /// <summary>Quantidade de opções marcadas (só vale depois do cálculo).</summary>
    private int _selected;
    /// <summary>Painel das opções, preenchido quando o cálculo termina.</summary>
    private readonly StackPanel _optionsPanel = new();
    /// <summary>Texto do total selecionado.</summary>
    private readonly TextBlock _total = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    /// <summary>Monta a janela; <paramref name="options"/> (se houver) é a tarefa que calcula as opções em segundo plano.</summary>
    private ToolConfirmDialog(ToolDefinition tool, Task<IReadOnlyList<ToolOption>>? options, bool offerRestore, string? warning)
    {
        Title = Loc.T("Executar ferramenta", "Run tool");
        Width = 560;
        MaxHeight = 700;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Themes.ThemeManager.Register(this);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = tool.Name + "?", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("WarnBrush") });
        root.Children.Add(new TextBlock { Text = tool.Explain, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Res("MutedBrush") });

        if (tool.Commands.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = Loc.T("O que será executado", "What will be run"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), Foreground = Ui.Res("AccentBrush") });
            var box = new TextBox { Text = string.Join(Environment.NewLine, tool.Commands), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
            box.Style = (Style)Application.Current.FindResource("ValueBox");
            System.Windows.Automation.AutomationProperties.SetName(box, Loc.T("Comandos que serão executados", "Commands that will be run"));
            var frame = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
            root.Children.Add(frame);
        }

        var warns = new List<string>();
        if (tool.NeedsRestart) warns.Add(Loc.T("Pode exigir reiniciar o PC para concluir.", "May require restarting the PC to finish."));
        if (tool.NeedsAdmin) warns.Add(Loc.T("Exige executar como administrador.", "Requires running as administrator."));
        warns.Add(Loc.T("Esta ação não pode ser desfeita pelo WinDiag.", "This action cannot be undone by WinDiag."));
        foreach (var w in warns)
            root.Children.Add(new TextBlock { Text = w, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Ui.Res("WarnBrush") });

        if (!string.IsNullOrWhiteSpace(warning))
        {
            var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 12, 0, 0), BorderBrush = Ui.Res("WarnBrush") };
            box.Child = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("WarnBrush") };
            root.Children.Add(box);
            _ack = new CheckBox { Content = Loc.T("Estou ciente", "I understand"), Margin = new Thickness(0, 8, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_ack, Loc.T("Estou ciente do aviso", "I understand the warning"));
            _ack.Click += (_, _) => UpdateOk();
            root.Children.Add(_ack);
        }

        _ok = new Button { Content = Loc.T("Executar", "Run"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetName(_ok, Loc.T("Executar ferramenta", "Run tool"));

        if (options is not null)
        {
            _hasOptions = true;
            root.Children.Add(new TextBlock { Text = Loc.T("O que limpar", "What to clean"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), Foreground = Ui.Res("AccentBrush") });
            _optionsPanel.Children.Add(Ui.Muted(Loc.T("Calculando tamanhos...", "Calculating sizes..."), 12.5));
            _optionsPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 6, 0, 0) });
            root.Children.Add(_optionsPanel);
            root.Children.Add(_total);
            _ = LoadOptionsAsync(options);
        }

        if (offerRestore)
        {
            _restore = new CheckBox { Content = Loc.T("Criar ponto de restauração antes", "Create a restore point first"), Margin = new Thickness(0, 12, 0, 0), IsChecked = tool.Risk == FixRisk.Alto };
            System.Windows.Automation.AutomationProperties.SetName(_restore, Loc.T("Criar ponto de restauração antes", "Create a restore point first"));
            root.Children.Add(_restore);
        }

        UpdateOk();
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = Loc.T("Cancelar", "Cancel"), MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    /// <summary>Espera o cálculo das opções e cria as caixas de seleção; falha vira mensagem e mantém o botão desabilitado.</summary>
    private async Task LoadOptionsAsync(Task<IReadOnlyList<ToolOption>> task)
    {
        try
        {
            var list = await task;
            _optionsPanel.Children.Clear();
            if (list.Count == 0)
            {
                _optionsPanel.Children.Add(Ui.Muted(Loc.T("Nenhuma opção disponível.", "No option available."), 12.5));
                return;
            }
            foreach (var o in list)
            {
                string size = o.Bytes.HasValue ? " (" + ToolsView.Size(o.Bytes.Value) + ")"
                    : !string.IsNullOrWhiteSpace(o.Note) ? " (" + o.Note + ")" : "";
                var cb = new CheckBox { Content = o.Label + size, IsChecked = o.DefaultOn, Margin = new Thickness(0, 2, 0, 2) };
                if (!o.Bytes.HasValue && size.Length > 0) cb.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
                System.Windows.Automation.AutomationProperties.SetName(cb, o.Label + size);
                cb.Click += (_, _) => UpdateTotal();
                _options.Add((cb, o));
                _optionsPanel.Children.Add(cb);
            }
            _total.Visibility = Visibility.Visible;
            UpdateTotal();
        }
        catch (Exception ex)
        {
            _optionsPanel.Children.Clear();
            _optionsPanel.Children.Add(new TextBlock { Text = Loc.T("Não foi possível calcular as opções: ", "Could not calculate the options: ") + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("BadBrush") });
        }
    }

    /// <summary>Atualiza o total selecionado e habilita a confirmação só com pelo menos uma opção marcada.</summary>
    private void UpdateTotal()
    {
        long sum = 0;
        int n = 0;
        foreach (var (box, o) in _options)
        {
            if (box.IsChecked != true) continue;
            n++;
            sum += o.Bytes ?? 0;
        }
        _total.Text = Loc.T("Total selecionado: ", "Total selected: ") + ToolsView.Size(sum);
        _selected = n;
        UpdateOk();
    }

    /// <summary>Habilita "Executar" só com o aviso reconhecido e, se houver opções, com pelo menos uma marcada.</summary>
    private void UpdateOk() =>
        _ok.IsEnabled = (_ack is null || _ack.IsChecked == true) && (!_hasOptions || _selected > 0);

    /// <summary>Exibe a confirmação (modal) e devolve a escolha do usuário.</summary>
    public static ToolConfirmResult Ask(Window? owner, ToolDefinition tool, Task<IReadOnlyList<ToolOption>>? options, bool offerRestore, string? warning = null)
    {
        var d = new ToolConfirmDialog(tool, options, offerRestore, warning);
        if (owner is { IsLoaded: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        bool ok = d.ShowDialog() == true;
        var ids = d._options.Where(x => x.Box.IsChecked == true).Select(x => x.Option.Id).ToList();
        return new ToolConfirmResult(ok, ok && d._restore?.IsChecked == true, ids);
    }
}
