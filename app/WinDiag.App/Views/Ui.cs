using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinDiag.Core.Live;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Construtores de elementos visuais reutilizados pelas abas.</summary>
public static class Ui
{
    /// <summary>Pincel do tema atual pela chave (o mesmo objeto é atualizado ao vivo na troca de tema).</summary>
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Texto de estado vazio (seção ou lista sem dados).</summary>
    public static TextBlock Empty(string? text = null) => new()
    {
        Text = text ?? WinDiag.Core.Loc.T("Sem dados nesta seção.", "No data in this section."),
        Foreground = Res("MutedBrush"),
        FontSize = 13,
        FontStyle = FontStyles.Italic,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8)
    };

    /// <summary>Pincel de cor da severidade (OK, Info, Aviso, Atenção).</summary>
    public static Brush SevBrush(Severity s) => Res(s switch
    {
        Severity.Ok => "OkBrush",
        Severity.Info => "InfoBrush",
        Severity.Aviso => "WarnBrush",
        _ => "BadBrush"
    });

    /// <summary>Texto auxiliar em cor suave.</summary>
    public static TextBlock Muted(string text, double size = 12) => new()
    {
        Text = text,
        Foreground = Res("MutedBrush"),
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };

    /// <summary>Envolve o conteúdo em um cartão (estilo "Card").</summary>
    public static Border Card(UIElement content)
    {
        var b = new Border { Child = content };
        b.Style = (Style)Application.Current.FindResource("Card");
        return b;
    }

    /// <summary>Título de cartão (cor de destaque).</summary>
    public static TextBlock CardTitle(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
        t.Style = (Style)Application.Current.FindResource("CardTitle");
        return t;
    }

    /// <summary>
    /// Linha rótulo (cinza, 220 px) / valor. O valor é um <see cref="TextBlock"/> leve (uma página tem centenas deles;
    /// caixas de texto deixavam a rolagem pesada). Copiar: duplo clique ou botão direito &gt; "Copiar valor".
    /// </summary>
    public static Grid LabelValue(string label, string value, Brush? valueBrush = null)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, Cursor = Cursors.IBeam };
        if (valueBrush != null) v.Foreground = valueBrush;
        else v.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AttachCopy(v, label);
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    /// <summary>Texto de valor de uma linha criada por <see cref="LabelValue"/>.</summary>
    public static TextBlock ValueOf(Grid row) => (TextBlock)row.Children[1];

    /// <summary>Copia o texto de um <see cref="TextBlock"/> por duplo clique ou pelo menu de contexto.</summary>
    public static void AttachCopy(TextBlock t, string label)
    {
        void Copy()
        {
            try { Clipboard.SetText(t.Text ?? ""); } catch (System.Runtime.InteropServices.ExternalException) { /* área de transferência ocupada */ }
        }
        var item = new MenuItem { Header = WinDiag.Core.Loc.T("Copiar valor", "Copy value") };
        item.Click += (_, _) => Copy();
        var both = new MenuItem { Header = WinDiag.Core.Loc.T("Copiar rótulo e valor", "Copy label and value") };
        both.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{label}: {t.Text}"); } catch (System.Runtime.InteropServices.ExternalException) { }
        };
        t.ContextMenu = new ContextMenu { Items = { item, both } };
        t.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Copy(); e.Handled = true; } };
        t.ToolTip = WinDiag.Core.Loc.T("Duplo clique para copiar", "Double-click to copy");
        ToolTipService.SetInitialShowDelay(t, 900);
    }

    /// <summary>Selo de severidade; o texto usa a chave BadgeTextBrush para ter contraste nos dois temas.</summary>
    public static Border Badge(Finding f)
    {
        var brush = SevBrush(f.Sev);
        return new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 1),
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
            Child = new TextBlock
            {
                Text = f.SevLabel,
                Foreground = Res("BadgeTextBrush"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
    }

    /// <summary>Linha de verificação: selo + título -> evidência; clique expande os detalhes (no modo Simples, a frase simples).</summary>
    public static UIElement FindingItem(Finding f, bool expanded = false) => new FindingRow(f, expanded);

    // ---------- Modo Simples/Detalhado ----------

    /// <summary>Cache do modo; nulo até a primeira consulta, para ler as preferências só uma vez.</summary>
    private static bool? _simple;

    /// <summary>true no modo Simples (padrão); lido de UserPrefs.Mode na primeira consulta.</summary>
    public static bool Simple
    {
        get
        {
            // O padrão é Simples: só "Detailed" (gravado em Configurações) liga o modo técnico.
            _simple ??= !string.Equals(Prefs.Read().Mode, "Detailed", StringComparison.OrdinalIgnoreCase);
            return _simple.Value;
        }
    }

    /// <summary>Disparado depois que o modo muda (as telas se reconstroem).</summary>
    public static event Action? ModeChanged;

    /// <summary>Define o modo, grava em UserPrefs.Mode e avisa as telas.</summary>
    public static void SetSimple(bool simple)
    {
        if (Simple == simple) return;
        _simple = simple;
        // Grava depois de atualizar o cache: se o arquivo estiver ilegível, a escolha vale ao menos na sessão (Prefs.Update avisa).
        Prefs.Update(p => p.Mode = simple ? "Simple" : "Detailed");
        ModeChanged?.Invoke();
    }

    // ---------- Navegação e ajuda ----------

    /// <summary>Abre a seção (e destaca o achado de Id/título informado) na janela principal.</summary>
    public static void GoTo(string sectionKey, string? findingId = null) =>
        (Application.Current?.MainWindow as MainWindow)?.GoTo(sectionKey, findingId);

    /// <summary>Abre a janela "Como fazer na BIOS" para o tópico informado.</summary>
    public static void ShowBios(string topic) => BiosGuideWindow.ShowFor(topic);

    /// <summary>
    /// Botão (i) com explicação do glossário em ToolTip; fica oculto (Collapsed) se o termo não existir no glossário.
    /// </summary>
    public static TextBlock InfoIcon(string? glossaryKeyOrTitle)
    {
        var entry = string.IsNullOrWhiteSpace(glossaryKeyOrTitle) ? null : WinDiag.Core.Knowledge.Glossary.Lookup(glossaryKeyOrTitle);
        var t = new TextBlock
        {
            Text = "\uE946",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 13,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,
            Background = Brushes.Transparent
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        if (entry == null)
        {
            t.Visibility = Visibility.Collapsed;
            return t;
        }
        var tip = new StackPanel { MaxWidth = 360 };
        tip.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        tip.Children.Add(new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap });
        var tt = new ToolTip { Content = tip };
        tt.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tt.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        tt.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        t.ToolTip = tt;
        ToolTipService.SetInitialShowDelay(t, 250);
        ToolTipService.SetShowDuration(t, 60000);
        System.Windows.Automation.AutomationProperties.SetName(t, WinDiag.Core.Loc.T("Explicação: ", "Explanation: ") + entry.Title);
        return t;
    }

    /// <summary>Junta um título ao botão (i) do glossário; sem entrada no glossário devolve o próprio título.</summary>
    public static UIElement WithInfo(FrameworkElement title, string? glossaryKeyOrTitle)
    {
        var icon = InfoIcon(glossaryKeyOrTitle);
        if (icon.Visibility != Visibility.Visible) return title;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(title);
        sp.Children.Add(icon);
        return sp;
    }

    /// <summary>Título de cartão com o (i) do glossário (chave padrão: o próprio texto).</summary>
    public static UIElement CardTitleInfo(string text, string? glossaryKey = null) => WithInfo(CardTitle(text), glossaryKey ?? text);
    /// <summary>Lista de verificações; se explain=true, Explain/Action começam visíveis.</summary>
    public static StackPanel FindingList(IEnumerable<Finding> items, bool expanded = false)
    {
        var p = new StackPanel();
        foreach (var f in items) p.Children.Add(FindingItem(f, expanded));
        return p;
    }

    /// <summary>Tabela somente leitura (rolagem repassada à aba).</summary>
    public static DataGrid Table(InfoTable t)
    {
        var grid = new DataGrid { ItemsSource = t.Rows.Select(r => r.ToArray()).ToList() };
        // A última coluna (normalmente descrição/observação) ocupa o espaço livre e quebra linha,
        // para o texto não ficar cortado atrás de uma rolagem horizontal.
        var wrap = new Style(typeof(TextBlock));
        wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        int last = t.Columns.Count - 1;
        for (int i = 0; i < t.Columns.Count; i++)
        {
            bool isLast = i == last && t.Columns.Count > 1;
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = t.Columns[i],
                Binding = new System.Windows.Data.Binding($"[{i}]"),
                Width = isLast ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(1, DataGridLengthUnitType.Auto),
                MinWidth = isLast ? 160 : 0,
                ElementStyle = isLast ? wrap : null,
            });
        }
        if (t.Columns.Count > 1) grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // Sem rolagem interna: repassa a roda do mouse para o ScrollViewer da aba.
        grid.PreviewMouseWheel += (s, e) =>
        {
            e.Handled = true;
            var parent = ((Control)s).Parent as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = s });
        };
        return grid;
    }
}

/// <summary>Cartão "Ao vivo": campos rótulo/valor atualizados a cada amostra.</summary>
public sealed class LiveCard : Border
{
    /// <summary>Linhas do cartão e a função que extrai o texto de cada uma a partir da amostra.</summary>
    private readonly List<(Grid Row, Func<LiveSample, string> Get)> _fields = new();
    /// <summary>Painel de conteúdo do cartão (título + linhas).</summary>
    private readonly StackPanel _body = new();

    /// <summary>Cria o cartão com os campos informados (rótulo e leitor da amostra).</summary>
    public LiveCard(params (string Label, Func<LiveSample, string> Get)[] fields)
    {
        Style = (Style)Application.Current.FindResource("Card");
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Ui.Res("OkBrush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var t = Ui.CardTitle(WinDiag.Core.Loc.T("Ao vivo", "Live"));
        t.Margin = new Thickness(0);
        title.Children.Add(t);
        _body.Children.Add(title);
        _body.Children.Add(new Border { Height = 6 });
        foreach (var (label, get) in fields)
        {
            var row = Ui.LabelValue(label, WinDiag.Core.Fmt.NA);
            _fields.Add((row, get));
            _body.Children.Add(row);
        }
        Child = _body;
    }

    /// <summary>Atualiza todos os campos com a amostra; falhas viram "n/d".</summary>
    public void Update(LiveSample s)
    {
        // Cada campo é lido isoladamente: uma falha em um não apaga os demais (vira "n/d").
        foreach (var (row, get) in _fields)
        {
            string text;
            try { text = get(s); } catch { text = WinDiag.Core.Fmt.NA; }
            Ui.ValueOf(row).Text = string.IsNullOrWhiteSpace(text) ? WinDiag.Core.Fmt.NA : text;
        }
    }

    /// <summary>Formata número no idioma atual com unidade, ou "n/d" ("n/a").</summary>
    public static string N(double? v, string unit, string fmt = "0") =>
        v.HasValue ? v.Value.ToString(fmt, WinDiag.Core.Loc.Culture) + unit : WinDiag.Core.Fmt.NA;
}
