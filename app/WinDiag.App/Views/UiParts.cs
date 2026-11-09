using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinDiag.Core;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>
/// Camada de destaque temporário: borda de 2 px (AccentBrush) e fundo suave que esmaecem em cerca de 1,5 s.
/// Só a opacidade da camada é animada; os pincéis continuam os do tema (atualizam ao vivo).
/// </summary>
public sealed class HighlightOverlay : Grid
{
    /// <summary>Cria a camada invisível e sem interação.</summary>
    public HighlightOverlay()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        var fill = new Border { CornerRadius = new CornerRadius(6), Opacity = 0.18 };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var outline = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2) };
        outline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        Children.Add(fill);
        Children.Add(outline);
    }

    /// <summary>Acende a camada e a esmaece em ~1,5 s.</summary>
    public void Flash()
    {
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1750))));
        BeginAnimation(OpacityProperty, anim);
    }
}

/// <summary>Linha de verificação (achado) clicável, com detalhes expansíveis e destaque ao navegar até ela.</summary>
public sealed class FindingRow : Border
{
    /// <summary>Painel com explicação, ação e botão da BIOS; recolhido/exibido ao clicar na linha.</summary>
    private readonly StackPanel _details;
    /// <summary>Seta indicadora (ícone) que alterna entre expandido e recolhido.</summary>
    private readonly TextBlock _chevron;
    /// <summary>false quando o achado não tem nada a mostrar além do título (a linha então não é clicável).</summary>
    private readonly bool _hasDetails;
    /// <summary>Camada de destaque usada pela navegação a partir de outras telas.</summary>
    private readonly HighlightOverlay _flash = new();

    /// <summary>Id estável do achado ("" se ausente).</summary>
    public string FindingId { get; }

    /// <summary>Título do achado.</summary>
    public string FindingTitle { get; }

    /// <summary>Monta a linha; <paramref name="expanded"/> inicia com os detalhes visíveis.</summary>
    public FindingRow(Finding f, bool expanded)
    {
        FindingId = f.Id ?? "";
        FindingTitle = f.Title;
        Tag = FindingId;
        Uid = FindingId;
        SetResourceReference(BackgroundProperty, "PanelAltBrush");
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10, 7, 10, 7);
        Margin = new Thickness(0, 0, 0, 5);
        Cursor = Cursors.Hand;

        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Badge(f));
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run(f.Title) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(f.Evidence))
        {
            var ev = new Run(" -> " + f.Evidence);
            ev.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            text.Inlines.Add(ev);
        }
        Grid.SetColumn(text, 1);
        head.Children.Add(text);

        // No modo Simples mostra a explicação leve primeiro; o texto técnico fica atrás de "ver detalhes técnicos".
        bool simple = Ui.Simple && !string.IsNullOrWhiteSpace(f.Simple);
        var tech = new StackPanel();
        AddDetail(tech, Loc.T("O que significa", "What it means"), f.Explain);
        AddDetail(tech, Loc.T("O que fazer", "What to do"), f.Action);

        _details = new StackPanel { Margin = new Thickness(74, 6, 0, 0), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        if (simple)
        {
            _details.Children.Add(new TextBlock { Text = f.Simple, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
            if (tech.Children.Count > 0)
            {
                tech.Visibility = Visibility.Collapsed;
                var link = new TextBlock { Text = Loc.T("ver detalhes técnicos", "show technical details"), Cursor = Cursors.Hand, FontSize = 12, TextDecorations = TextDecorations.Underline, Margin = new Thickness(0, 0, 0, 4) };
                link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                link.MouseLeftButtonUp += (_, e) =>
                {
                    tech.Visibility = Visibility.Visible;
                    link.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                };
                _details.Children.Add(link);
            }
        }
        _details.Children.Add(tech);
        if (!string.IsNullOrWhiteSpace(f.GuideTopic))
        {
            var topic = f.GuideTopic;
            var bios = new Button { Content = Loc.T("Como fazer na BIOS", "How to do it in the BIOS"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 2) };
            bios.Click += (_, _) => Ui.ShowBios(topic);
            _details.Children.Add(bios);
        }

        _hasDetails = simple || tech.Children.Count > 0 || !string.IsNullOrWhiteSpace(f.GuideTopic);
        _chevron = new TextBlock
        {
            Text = expanded ? "" : "",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = _hasDetails ? Visibility.Visible : Visibility.Collapsed
        };
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_chevron, 2);
        head.Children.Add(_chevron);
        stack.Children.Add(head);
        stack.Children.Add(_details);

        var layer = new Grid();
        layer.Children.Add(stack);
        layer.Children.Add(_flash);
        Child = layer;

        if (_hasDetails) MouseLeftButtonUp += (_, _) => SetExpanded(_details.Visibility != Visibility.Visible);
    }

    /// <summary>Mostra/oculta os detalhes e ajusta a seta (ícone Segoe MDL2).</summary>
    private void SetExpanded(bool show)
    {
        _details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Text = show ? "" : "";
    }

    /// <summary>Expande os detalhes (se houver).</summary>
    public void Expand()
    {
        if (_hasDetails) SetExpanded(true);
    }

    /// <summary>Aplica o destaque suave (borda + fundo que esmaecem).</summary>
    public void Flash() => _flash.Flash();

    /// <summary>true se o id (ou, na falta de id, o título) informado identifica este achado.</summary>
    public bool Matches(string idOrTitle) =>
        (FindingId.Length > 0 && string.Equals(FindingId, idOrTitle, StringComparison.OrdinalIgnoreCase))
        || string.Equals(FindingTitle, idOrTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adiciona um bloco "título: texto" aos detalhes; ignora corpo vazio.</summary>
    private static void AddDetail(StackPanel p, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontSize = 12.5 };
        var r = new Run(title + ": ") { FontWeight = FontWeights.SemiBold };
        r.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        t.Inlines.Add(r);
        t.Inlines.Add(new Run(body));
        p.Children.Add(t);
    }
}

/// <summary>Seletor segmentado (um botão por opção; o selecionado usa o estilo de destaque).</summary>
public sealed class Segmented : StackPanel
{
    /// <summary>Botões na ordem das opções, para poder trocar o estilo do selecionado.</summary>
    private readonly List<Button> _buttons = new();

    /// <summary>Cria o seletor; <paramref name="picked"/> é chamado só em escolhas do usuário.</summary>
    public Segmented(IReadOnlyList<string> labels, int selected, Action<int> picked)
    {
        Orientation = Orientation.Horizontal;
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var b = new Button { Content = labels[i], Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0), MinWidth = 76 };
            b.Click += (_, _) =>
            {
                Select(index);
                picked(index);
            };
            _buttons.Add(b);
            Children.Add(b);
        }
        Select(selected);
    }

    /// <summary>Marca a opção sem disparar o retorno de chamada.</summary>
    public void Select(int index)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (i == index) _buttons[i].Style = (Style)Application.Current.FindResource("AccentButton");
            else _buttons[i].ClearValue(StyleProperty);
        }
    }
}
