using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WinDiag.App.Views;

/// <summary>Pequenos construtores visuais compartilhados pelas abas "Visão geral" e "Plano de ação".</summary>
internal static class UiExtra
{
    /// <summary>Ícone da fonte Segoe Fluent/MDL2 com cor vinculada ao tema.</summary>
    public static TextBlock Icon(string glyph, double size, string brushKey)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    /// <summary>Selo arredondado com texto de bom contraste nos dois temas.</summary>
    public static Border Pill(string text, string brushKey)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold }
        };
        b.SetResourceReference(Border.BackgroundProperty, brushKey);
        ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, "BadgeTextBrush");
        return b;
    }

    /// <summary>Parágrafo "Rótulo: texto" com o rótulo em destaque.</summary>
    public static TextBlock Detail(string label, string body)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 12.5 };
        var r = new Run(label + ": ") { FontWeight = FontWeights.SemiBold };
        r.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        t.Inlines.Add(r);
        t.Inlines.Add(new Run(body));
        return t;
    }

    /// <summary>Texto com cor do tema vinculada.</summary>
    public static TextBlock Text(string text, string brushKey, double size = 13, FontWeight? weight = null, bool wrap = true)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (weight.HasValue) t.FontWeight = weight.Value;
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }
}
