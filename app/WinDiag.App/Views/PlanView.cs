using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.App.Views;

/// <summary>Aba "Plano de ação": passos numerados, do mais simples e grátis ao que exige trocar peça.</summary>
public sealed class PlanView : UserControl
{
    /// <summary>Cultura do idioma atual, para numerar e contar com o formato correto.</summary>
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Abre uma aba pela chave interna (ex.: "optimizations").</summary>
    private readonly Action<string> _goTo;
    /// <summary>Contêiner dos passos; reconstruído a cada mudança.</summary>
    private readonly StackPanel _list = new();
    /// <summary>Texto "N de M concluídos".</summary>
    private readonly TextBlock _counter = new() { FontWeight = FontWeights.SemiBold };
    /// <summary>Barra de progresso (0 a 1) dos passos concluídos.</summary>
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
    /// <summary>Cartão do contador e da barra; oculto quando não há passos.</summary>
    private readonly Border _progressCard;
    /// <summary>Passos do plano da última varredura.</summary>
    private List<ActionStep> _steps = new();
    /// <summary>Ids das otimizações que o app sabe aplicar; o botão "Aplicar otimização" só aparece para elas.</summary>
    private HashSet<string> _available = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>false até a primeira varredura terminar (mostra "Aguardando a leitura...").</summary>
    private bool _loaded;

    /// <summary>Disparado quando o usuário marca ou desmarca um passo (a Visão geral atualiza os "Próximos passos").</summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// Cria a aba. <paramref name="goTo"/> abre uma aba pela chave interna (ex.: "optimizations");
    /// "Ver detalhes" navega até a seção/achado com <see cref="Ui.GoTo"/>.
    /// </summary>
    public PlanView(Action<string> goTo)
    {
        _goTo = goTo;

        var root = new StackPanel();
        root.Children.Add(Ui.Muted(Loc.T("Do mais simples e grátis para o que exige trocar peça.", "From the simplest and free to what requires replacing a part."), 13));

        var prog = new StackPanel();
        _counter.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        prog.Children.Add(_counter);
        prog.Children.Add(_bar);
        _progressCard = Ui.Card(prog);
        _progressCard.Margin = new Thickness(0, 12, 0, 12);
        _progressCard.Visibility = Visibility.Collapsed;
        root.Children.Add(_progressCard);

        _list.Children.Add(Ui.Empty(Loc.T("Aguardando a leitura...", "Waiting for the scan...")));
        root.Children.Add(_list);
        Content = root;
    }

    /// <summary>Recebe o plano calculado e os ids de otimização disponíveis (na thread de UI).</summary>
    public void Update(List<ActionStep> steps, HashSet<string> availableFixes)
    {
        _steps = steps;
        _available = availableFixes;
        _loaded = true;
        Rebuild();
    }

    /// <summary>Reconstrói a lista (ex.: depois de trocar o modo Simples/Detalhado).</summary>
    public void RefreshView() => Rebuild();

    /// <summary>Reconstrói contador, barra e grupos (grátis primeiro, depois hardware); passos feitos descem para o fim do grupo.</summary>
    private void Rebuild()
    {
        _list.Children.Clear();
        if (!_loaded) { _list.Children.Add(Ui.Empty(Loc.T("Aguardando a leitura...", "Waiting for the scan..."))); return; }

        if (_steps.Count == 0)
        {
            _progressCard.Visibility = Visibility.Collapsed;
            _list.Children.Add(Ui.Card(Ui.Empty(Loc.T("Nenhum passo pendente: a configuração está em ordem.", "No pending steps: the configuration is in order."))));
            return;
        }

        int done = _steps.Count(s => PlanStore.IsDone(s.Key));
        _progressCard.Visibility = Visibility.Visible;
        _counter.Text = Loc.T($"{done} de {_steps.Count} concluídos", $"{done} of {_steps.Count} completed");
        _bar.Value = (double)done / _steps.Count;

        AddGroup("Grátis", "Free", ActionCost.Gratis);
        AddGroup("Exige hardware", "Requires hardware", ActionCost.Hardware);
    }

    /// <summary>Adiciona um grupo de custo (título, contagem e cartões), se houver passos nele.</summary>
    private void AddGroup(string ptTitle, string enTitle, ActionCost cost)
    {
        var items = _steps.Where(s => s.Cost == cost)
            .OrderBy(s => PlanStore.IsDone(s.Key) ? 1 : 0)
            .ThenBy(s => s.Order)
            .ToList();
        if (items.Count == 0) return;

        var title = Loc.T(ptTitle, enTitle);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        head.Children.Add(Ui.WithInfo(UiExtra.Text(title, "AccentBrush", 15, FontWeights.SemiBold, wrap: false), ptTitle));
        head.Children.Add(UiExtra.Text("  (" + items.Count.ToString(Pt) + ")", "MutedBrush", 13, wrap: false));
        _list.Children.Add(head);
        foreach (var s in items) _list.Children.Add(BuildCard(s));
    }

    /// <summary>Monta o cartão de um passo; marcar "Feito" grava em <see cref="PlanStore"/> e avisa a Visão geral.</summary>
    private Border BuildCard(ActionStep s)
    {
        bool done = PlanStore.IsDone(s.Key);
        var body = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = UiExtra.Text(s.Order.ToString(Pt) + ".", "AccentBrush", 16, FontWeights.Bold, wrap: false);
        num.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(num);

        var titleBox = new StackPanel();
        titleBox.Children.Add(UiExtra.Text(s.Title, "TextBrush", 14.5, FontWeights.SemiBold));
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        badges.Children.Add(s.Cost == ActionCost.Gratis ? UiExtra.Pill(Loc.T("GRÁTIS", "FREE"), "OkBrush") : UiExtra.Pill("HARDWARE", "InfoBrush"));
        var sev = Ui.Badge(new Finding(s.Sev, "", ""));
        sev.Margin = new Thickness(0, 0, 8, 0);
        sev.VerticalAlignment = VerticalAlignment.Center;
        badges.Children.Add(sev);
        titleBox.Children.Add(badges);
        Grid.SetColumn(titleBox, 1);
        head.Children.Add(titleBox);

        var cb = new CheckBox { Content = Loc.T("Feito", "Done"), IsChecked = done, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0) };
        var key = s.Key;
        cb.Click += (_, _) =>
        {
            PlanStore.SetDone(key, cb.IsChecked == true);
            Rebuild();
            ProgressChanged?.Invoke();
        };
        Grid.SetColumn(cb, 2);
        head.Children.Add(cb);
        body.Children.Add(head);

        var detail = new StackPanel { Margin = new Thickness(24, 4, 0, 0) };
        if (!string.IsNullOrWhiteSpace(s.Where)) detail.Children.Add(UiExtra.Detail(Loc.T("Onde está o limite", "Where the limit is"), s.Where));
        var what = Ui.Simple && !string.IsNullOrWhiteSpace(s.Simple) ? s.Simple : s.What;
        if (!string.IsNullOrWhiteSpace(what)) detail.Children.Add(UiExtra.Detail(Loc.T("O que fazer", "What to do"), what));
        if (!string.IsNullOrWhiteSpace(s.Gain)) detail.Children.Add(UiExtra.Detail(Loc.T("Ganho esperado", "Expected gain"), s.Gain));

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 2) };
        if (!string.IsNullOrWhiteSpace(s.FixId) && _available.Contains(s.FixId))
        {
            var apply = new Button { Content = Loc.T("Aplicar otimização", "Apply optimization"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            apply.Click += (_, _) => _goTo("optimizations");
            buttons.Children.Add(apply);
        }
        if (Ui.ToolLink(s.ToolId) is { } toolBtn) buttons.Children.Add(toolBtn);
        if (!string.IsNullOrWhiteSpace(s.SectionKey))
        {
            var details = new Button { Content = Loc.T("Ver detalhes", "View details"), Padding = new Thickness(12, 4, 12, 4) };
            var section = s.SectionKey;
            var finding = s.FindingId;
            details.Click += (_, _) => Ui.GoTo(section, string.IsNullOrWhiteSpace(finding) ? null : finding);
            buttons.Children.Add(details);
        }
        if (!string.IsNullOrWhiteSpace(s.GuideTopic))
        {
            var bios = new Button { Content = Loc.T("Como fazer na BIOS", "How to do it in the BIOS"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            var topic = s.GuideTopic;
            bios.Click += (_, _) => Ui.ShowBios(topic);
            buttons.Children.Add(bios);
        }
        if (buttons.Children.Count > 0) detail.Children.Add(buttons);
        body.Children.Add(detail);

        var card = Ui.Card(body);
        if (done) card.Opacity = 0.55;
        return card;
    }
}
