using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>Textos de resumo de impressoras e bateria, compartilhados pela Visão geral e pela aba Trabalho.</summary>
internal static class WorkText
{
    /// <summary>Cultura do idioma atual, para formatar números com vírgula/ponto corretos.</summary>
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Quantidade de impressoras com erro ou offline.</summary>
    public static int Problems(SystemFacts f) => f.Printers.Count(p => p.Offline || p.HasError);

    /// <summary>Total de trabalhos presos na fila (parados há mais de 10 minutos).</summary>
    public static int Stuck(SystemFacts f) => f.Printers.Sum(p => p.StuckJobs);

    /// <summary>Linhas do cartão "Impressoras" (modo Detalhado) e a frase única do modo Simples.</summary>
    public static (List<string> Lines, string Simple) Printers(SystemFacts f)
    {
        var def = f.Printers.FirstOrDefault(p => p.IsDefault);
        int bad = Problems(f), stuck = Stuck(f);
        if (f.Printers.Count == 0)
        {
            string none = f.SpoolerRunning == false
                ? Loc.T("Nenhuma impressora lida; o serviço de impressão está parado", "No printer read; the print service is stopped")
                : Loc.T("Nenhuma impressora instalada", "No printer installed");
            return (new List<string> { none }, none);
        }
        var lines = new List<string>
        {
            Loc.T("Padrão: ", "Default: ") + (def is null ? Fmt.NA : def.Name),
            Loc.T($"{f.Printers.Count} impressora(s) · {bad} com problema", $"{f.Printers.Count} printer(s) · {bad} with problems"),
            Loc.T($"Trabalhos presos na fila: {stuck}", $"Stuck jobs in the queue: {stuck}")
        };
        if (f.SpoolerRunning == false) lines.Add(Loc.T("Serviço de impressão (spooler): parado", "Print service (spooler): stopped"));
        string simple = bad == 0 && stuck == 0
            ? Loc.T($"{f.Printers.Count} impressora(s), sem problema aparente", $"{f.Printers.Count} printer(s), no apparent problem")
            : Loc.T($"{bad} impressora(s) com problema e {stuck} trabalho(s) preso(s)", $"{bad} printer(s) with problems and {stuck} stuck job(s)");
        return (lines, simple);
    }

    /// <summary>Linhas do cartão "Bateria" (modo Detalhado) e a frase única do modo Simples.</summary>
    public static (List<string> Lines, string Simple) Battery(SystemFacts f)
    {
        string wear = f.BatteryWearPct.HasValue ? f.BatteryWearPct.Value.ToString("0.#", Pt) + " %" : Fmt.NA;
        string source = f.OnAcPower is bool ac ? (ac ? Loc.T("na tomada", "plugged in") : Loc.T("na bateria", "on battery")) : Fmt.NA;
        string plan = string.IsNullOrWhiteSpace(f.ActivePowerPlanName) ? Fmt.NA : f.ActivePowerPlanName!;
        var lines = new List<string>
        {
            Loc.T("Desgaste: ", "Wear: ") + wear,
            Loc.T("Alimentação: ", "Power source: ") + source,
            Loc.T("Plano de energia: ", "Power plan: ") + plan
        };
        string simple = f.BatteryWearPct.HasValue
            ? Loc.T($"Desgaste de {wear}, {source}", $"Wear of {wear}, {source}")
            : Loc.T($"Desgaste não medido, {source}", $"Wear not measured, {source}");
        return (lines, simple);
    }
}

/// <summary>Aba "Trabalho" (perfil Trabalho): resumo amigável no modo Simples; seção completa no Detalhado; mais os programas de fábrica.</summary>
public sealed class WorkView : UserControl
{
    /// <summary>Leitor da última varredura (a aba não guarda dados próprios; sempre pergunta à janela principal).</summary>
    private readonly Func<SystemFacts?> _getFacts;
    /// <summary>Leitor da seção "trabalho" já montada, usada no modo Detalhado.</summary>
    private readonly Func<SectionResult?> _getSection;
    /// <summary>Raiz do conteúdo; reconstruída a cada <see cref="Refresh"/>.</summary>
    private readonly StackPanel _root = new();
    /// <summary>Cartão de programas de fábrica, reaproveitado entre atualizações.</summary>
    private readonly BloatCardView _bloat;

    /// <summary>Cria a aba; <paramref name="getSection"/> devolve a seção "trabalho" já lida (ou null).</summary>
    public WorkView(Func<SystemFacts?> getFacts, Func<SectionResult?> getSection, Func<Task> rescan, Action beginBusy, Action endBusy)
    {
        _getFacts = getFacts;
        _getSection = getSection;
        _bloat = new BloatCardView(getFacts, rescan, beginBusy, endBusy);
        Content = _root;
        Refresh();
    }

    /// <summary>Redesenha com a leitura mais recente e o modo atual (Simples/Detalhado).</summary>
    public void Refresh()
    {
        _root.Children.Clear();
        var facts = _getFacts();
        var section = _getSection();
        try
        {
            if (Ui.Simple)
            {
                if (facts is null) _root.Children.Add(Ui.Card(Ui.Empty(Loc.T("Aguardando a leitura...", "Waiting for the scan..."))));
                else _root.Children.Add(BuildSimple(facts));
            }
            else if (section is null) _root.Children.Add(Ui.Card(Ui.Empty(Loc.T("Aguardando a leitura...", "Waiting for the scan..."))));
            else _root.Children.Add(new SectionView(section, false));
        }
        catch (Exception ex) { _root.Children.Add(Ui.Muted(Loc.T("Falha ao exibir este bloco: ", "Failed to display this block: ") + ex.Message)); }
        try { _bloat.Refresh(); }
        catch (Exception ex) { _root.Children.Add(Ui.Muted(Loc.T("Falha ao exibir os programas de fábrica: ", "Failed to display factory-installed programs: ") + ex.Message)); }
        _root.Children.Add(_bloat);
    }

    /// <summary>Resumo do modo Simples: uma linha por tema (impressoras, programas, bateria, atualizações, inicialização).</summary>
    private static Border BuildSimple(SystemFacts f)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardTitleInfo(Loc.T("Resumo para trabalho", "Summary for work"), "Trabalho"));

        // Linha local: ícone Segoe MDL2 + rótulo esmaecido + texto; "brush" pinta de aviso quando há problema medido.
        void Line(string glyph, string title, string text, string brush = "TextBrush")
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(UiExtra.Icon(glyph, 16, "AccentBrush"));
            var box = new StackPanel();
            box.Children.Add(UiExtra.Text(title, "MutedBrush", 12));
            box.Children.Add(UiExtra.Text(text, brush, 13.5));
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            sp.Children.Add(row);
        }

        var (_, printers) = WorkText.Printers(f);
        Line("", Loc.T("Impressoras", "Printers"), printers, WorkText.Problems(f) > 0 || WorkText.Stuck(f) > 0 ? "WarnBrush" : "TextBrush");

        int remove = f.Bloat.Count(b => b.Verdict == BloatVerdict.Remove);
        int optional = f.Bloat.Count(b => b.Verdict == BloatVerdict.Optional);
        Line("", Loc.T("Programas de fábrica", "Factory-installed programs"),
            remove + optional == 0
                ? Loc.T("Nenhum programa dispensável identificado", "No dispensable program identified")
                : Loc.T($"{remove} sugerido(s) para remover e {optional} opcional(is)", $"{remove} suggested for removal and {optional} optional"),
            remove > 0 ? "WarnBrush" : "TextBrush");

        if (f.HasBattery) Line("", Loc.T("Bateria", "Battery"), WorkText.Battery(f).Simple);

        string upd = f.LastUpdateInstalled.HasValue
            ? Loc.T("Última atualização instalada em ", "Last update installed on ") + Fmt.Date(f.LastUpdateInstalled)
            : Loc.T("Data da última atualização não lida", "Date of the last update not read");
        if (f.RebootPending == true) upd += Loc.T(". Há reinício pendente.", ". A restart is pending.");
        Line("", Loc.T("Atualizações do Windows", "Windows updates"), upd, f.RebootPending == true ? "WarnBrush" : "TextBrush");

        string boot = f.BootTimeMs is > 0
            ? Loc.T("Inicialização em cerca de ", "Startup in about ") + (f.BootTimeMs.Value / 1000.0).ToString("0", Loc.Culture) + " s"
            : Loc.T("Tempo de inicialização não lido", "Startup time not read");
        Line("", Loc.T("Inicialização", "Startup"), boot);

        var btn = new Button { Content = Loc.T("Ver detalhes", "View details"), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        btn.Click += (_, _) => Ui.SetSimple(false);
        sp.Children.Add(btn);
        return Ui.Card(sp);
    }
}
