using System.Globalization;
using System.Net;
using System.Text;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.Core.Report;

/// <summary>
/// Exportação do relatório em um único arquivo: HTML autocontido (sem JavaScript, tema claro/escuro
/// automático e CSS de impressão) ou TXT. Nome de usuário e números de série são mascarados.
/// </summary>
public static class ReportWriter
{
    /// <summary>Aviso impresso no relatório informando que usuário e seriais foram mascarados.</summary>
    private static string MaskNotice => Loc.T("Nome de usuário e números de série foram mascarados neste arquivo.", "User name and serial numbers have been masked in this file.");

    /// <summary>Chave da seção "Análise da plataforma", exibida antes do resumo.</summary>
    public const string AnalysisKey = "analise";

    /// <summary>Título do relatório (nome e versão do aplicativo).</summary>
    private static string ReportTitle => $"{AppInfo.Title} - {Loc.T("Relatório", "Report")}";

    /// <summary>Pasta padrão para exportação (Área de Trabalho, ou a pasta temporária se indisponível).</summary>
    public static string DefaultDir()
    {
        try
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }
        catch { }
        return Path.GetTempPath();
    }

    /// <summary>Nome sugerido: WinDiag_Relatorio_yyyyMMdd_HHmm.&lt;ext&gt; (em inglês, WinDiag_Report_...).</summary>
    public static string DefaultFileName(string extension)
    {
        var ext = (extension ?? "").Trim().TrimStart('.');
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        return $"WinDiag_{Loc.T("Relatorio", "Report")}_{stamp}.{ext}";
    }

    /// <summary>Grava o relatório HTML (arquivo único, autocontido) em <paramref name="path"/>.</summary>
    public static void WriteHtml(ScanEngine engine, string path) => WriteFile(path, BuildHtml(engine));

    /// <summary>Grava o relatório TXT (UTF-8 com BOM) em <paramref name="path"/>.</summary>
    public static void WriteTxt(ScanEngine engine, string path) => WriteFile(path, BuildTxt(engine));

    /// <summary>Grava o arquivo em UTF-8 com BOM (o Bloco de Notas e navegadores antigos reconhecem os acentos) e cria a pasta de destino se preciso.</summary>
    /// <param name="path">Caminho do arquivo.</param>
    /// <param name="content">Conteúdo completo.</param>
    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(true));
    }

    /// <summary>Compatibilidade: grava TXT e HTML na pasta indicada.</summary>
    public static (string TxtPath, string HtmlPath) Write(ScanEngine engine, string dir)
    {
        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, DefaultFileName("txt"));
        var html = Path.Combine(dir, DefaultFileName("html"));
        WriteTxt(engine, txt);
        WriteHtml(engine, html);
        return (txt, html);
    }

    /// <summary>Cópia das seções publicadas, tirada sob lock para a exportação não competir com a varredura.</summary>
    /// <param name="e">Motor de varredura.</param>
    private static List<SectionResult> Snapshot(ScanEngine e)
    {
        lock (e.Results) return e.Results.ToList();
    }

    /// <summary>true se a seção é a "Análise da plataforma".</summary>
    /// <param name="r">Seção.</param>
    private static bool IsAnalysis(SectionResult r) =>
        string.Equals(r.Key, AnalysisKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Seções na ordem do relatório: análise primeiro, depois as demais.</summary>
    private static List<SectionResult> Ordered(ScanEngine e)
    {
        var all = Snapshot(e);
        return all.Where(IsAnalysis).Concat(all.Where(r => !IsAnalysis(r))).ToList();
    }

    /// <summary>Texto do modo de leitura (administrador ou usuário padrão). Importa porque a leitura sem administrador é parcial.</summary>
    /// <param name="e">Motor de varredura.</param>
    private static string Mode(ScanEngine e) =>
        e.Facts.IsAdmin ? Loc.T("Administrador (leitura completa)", "Administrator (full reading)") : Loc.T("Usuário padrão (leitura parcial)", "Standard user (partial reading)");

    /// <summary>Rótulo de gravidade do relatório TXT, com largura fixa para alinhar as linhas.</summary>
    /// <param name="s">Gravidade.</param>
    private static string Tag(Severity s) => s switch
    {
        Severity.Ok => "[  OK   ]",
        Severity.Info => "[ INFO  ]",
        Severity.Aviso => Loc.T("[ AVISO ]", "[WARNING]"),
        _ => Loc.T("[ATENÇÃO ]", "[ATTENTION]")
    };

    /// <summary>Achados de AVISO ou ATENÇÃO para o diagnóstico, do mais grave e de maior impacto para o menor.</summary>
    /// <param name="all">Todos os achados.</param>
    private static IEnumerable<Finding> Diag(IEnumerable<Finding> all) =>
        all.Where(f => f.Sev >= Severity.Aviso).OrderByDescending(f => f.Sev).ThenByDescending(f => f.Impact);

    /// <summary>Sequência de espaços para alinhamento (nunca negativa).</summary>
    /// <param name="n">Quantidade de espaços.</param>
    private static string Spaces(int n) => new(' ', Math.Max(0, n));

    // ---------------------------------------------------------------- TXT

    /// <summary>Monta o texto do relatório TXT. As quebras de linha são CRLF para abrir corretamente no Bloco de Notas. Todo texto livre passa por <see cref="ReportMask"/>.</summary>
    /// <param name="engine">Motor já executado.</param>
    internal static string BuildTxt(ScanEngine engine)
    {
        var results = Ordered(engine);
        var all = results.SelectMany(r => r.Findings).ToList();
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append("\r\n");

        var bar = new string('=', 78);
        L(bar);
        L(" " + ReportTitle.ToUpper(Fmt.Culture));
        L(bar);
        L(Loc.T("Data:   ", "Date:   ") + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", Fmt.Culture));
        L(Loc.T("Modo:   ", "Mode:   ") + Mode(engine));
        L(Loc.T("Aviso:  ", "Notice: ") + MaskNotice);
        L();

        foreach (var r in results)
        {
            L("==== " + ReportMask.Text(r.Title).ToUpper(Fmt.Culture) + " " + new string('=', Math.Max(4, 70 - r.Title.Length)));
            if (!string.IsNullOrEmpty(r.Error)) L(Loc.T("  Erro: ", "  Error: ") + ReportMask.Text(ReportMask.OneLine(r.Error)));
            foreach (var g in r.Groups)
            {
                L();
                L("[" + ReportMask.Text(g.Title) + "]");
                foreach (var row in g.Rows)
                    L("  " + ReportMask.LabelCell(ReportMask.Text(row.Label)) + ReportMask.OneLine(ReportMask.RowValue(row.Label, row.Value)));
                foreach (var t in g.Tables) WriteTable(sb, t);
                foreach (var n in g.Notes) L("  * " + ReportMask.Text(ReportMask.OneLine(n)));
            }
            if (r.Findings.Count > 0)
            {
                L();
                L(Loc.T("[Verificações]", "[Checks]"));
                foreach (var f in r.Findings)
                {
                    var first = "  " + Tag(f.Sev) + " ";
                    L(ReportMask.Wrap(ReportMask.Text(f.Title) + " -> " + ReportMask.Text(f.Evidence), 78, first, Spaces(first.Length)));
                }
            }
            L();
        }

        L(bar);
        L(Loc.T(" DIAGNÓSTICO", " DIAGNOSIS"));
        L(bar);
        var top = Bottlenecks.Top(all);
        L();
        L(Loc.T("Gargalos prováveis para jogos (por impacto estimado):", "Likely bottlenecks for gaming (by estimated impact):"));
        if (top.Count == 0) L(Loc.T("  Nenhum gargalo relevante identificado nas leituras.", "  No relevant bottleneck identified in the readings."));
        else for (int i = 0; i < top.Count; i++)
            {
                var first = $"  {i + 1}. ";
                L(ReportMask.Wrap($"[{top[i].SevLabel}] {ReportMask.Text(top[i].Title)} - {ReportMask.Text(top[i].Evidence)}", 78, first, Spaces(first.Length)));
            }
        var c = Bottlenecks.Count(all);
        L();
        L(Loc.T($"Contagem: {c.Ok} OK, {c.Info} informativo(s), {c.Aviso} aviso(s), {c.Atencao} atenção.", $"Count: {c.Ok} OK, {c.Info} informational, {c.Aviso} warning(s), {c.Atencao} attention."));
        L();

        int n2 = 0;
        foreach (var f in Diag(all))
        {
            n2++;
            var first = $"{n2}. ";
            L(ReportMask.Wrap($"[{f.SevLabel}] {ReportMask.Text(f.Title)}", 78, first, Spaces(first.Length)));
            L(ReportMask.Wrap(Loc.T("Evidência: ", "Evidence: ") + ReportMask.Text(f.Evidence), 78, "   ", "   "));
            if (!string.IsNullOrWhiteSpace(f.Explain)) L(ReportMask.Wrap(Loc.T("O que significa: ", "What it means: ") + ReportMask.Text(f.Explain), 78, "   ", "   "));
            if (!string.IsNullOrWhiteSpace(f.Action)) L(ReportMask.Wrap(Loc.T("O que fazer: ", "What to do: ") + ReportMask.Text(f.Action), 78, "   ", "   "));
            L();
        }
        if (n2 == 0) { L(Loc.T("Nenhum aviso ou ponto de atenção.", "No warnings or attention points.")); L(); }

        var infos = all.Where(f => f.Sev == Severity.Info).ToList();
        if (infos.Count > 0)
        {
            L(Loc.T("Notas informativas:", "Informational notes:"));
            foreach (var f in infos)
            {
                L(ReportMask.Wrap(ReportMask.Text(f.Title) + ": " + ReportMask.Text(f.Evidence), 78, "  - ", "    "));
                if (!string.IsNullOrWhiteSpace(f.Explain)) L(ReportMask.Wrap(Loc.T("Significado: ", "Meaning: ") + ReportMask.Text(f.Explain), 78, "      ", "      "));
                if (!string.IsNullOrWhiteSpace(f.Action)) L(ReportMask.Wrap(Loc.T("Ação: ", "Action: ") + ReportMask.Text(f.Action), 78, "      ", "      "));
            }
            L();
        }

        var notes = results.SelectMany(r => r.Notes).Distinct().ToList();
        L(Loc.T("Leituras parciais:", "Partial readings:"));
        if (notes.Count == 0) L(Loc.T("  Nenhuma. Todas as leituras foram concluídas.", "  None. All readings were completed."));
        else foreach (var n in notes) L(ReportMask.Wrap(ReportMask.Text(n), 78, "  - ", "    "));
        L();
        return sb.ToString();
    }

    /// <summary>Escreve uma tabela com colunas alinhadas em texto simples, com as células mascaradas e em uma linha cada.</summary>
    /// <param name="sb">Destino.</param>
    /// <param name="t">Tabela.</param>
    private static void WriteTable(StringBuilder sb, InfoTable t)
    {
        void L(string s) => sb.Append(s).Append("\r\n");
        if (t.Columns.Count == 0) return;
        var rows = t.Rows.Select(r => ReportMask.Cells(t, r).Select(ReportMask.OneLine).ToList()).ToList();
        var w = new int[t.Columns.Count];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = t.Columns[i].Length;
            foreach (var r in rows) if (i < r.Count) w[i] = Math.Max(w[i], r[i].Length);
        }
        string Line(IList<string> cells)
        {
            var s = new StringBuilder("    ");
            for (int i = 0; i < w.Length; i++)
            {
                var v = i < cells.Count ? cells[i] : "";
                s.Append(i == w.Length - 1 ? v : v.PadRight(w[i] + 2));
            }
            return s.ToString().TrimEnd();
        }
        if (!string.IsNullOrWhiteSpace(t.Title)) L("  " + ReportMask.Text(t.Title) + ":");
        L(Line(t.Columns));
        L("    " + new string('-', Math.Min(74, w.Sum() + 2 * (w.Length - 1))));
        foreach (var r in rows) L(Line(r));
    }

    // ---------------------------------------------------------------- HTML

    /// <summary>Codifica texto para HTML (evita que nomes de arquivo ou mensagens do sistema quebrem ou injetem marcação no relatório).</summary>
    /// <param name="s">Texto original; null vira vazio.</param>
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>Classe CSS correspondente à gravidade.</summary>
    /// <param name="s">Gravidade.</param>
    private static string SevClass(Severity s) => s switch
    {
        Severity.Ok => "ok",
        Severity.Info => "info",
        Severity.Aviso => "warn",
        _ => "bad"
    };

    /// <summary>Monta o HTML autocontido: estilo embutido, abas feitas só com CSS (sem JavaScript) e conteúdo mascarado. Autocontido para poder ser enviado como um arquivo único.</summary>
    /// <param name="engine">Motor já executado.</param>
    internal static string BuildHtml(ScanEngine engine)
    {
        var results = Ordered(engine);
        var all = results.SelectMany(r => r.Findings).ToList();
        var analysis = results.FirstOrDefault(IsAnalysis);
        var rest = results.Where(r => !IsAnalysis(r)).ToList();

        // Painéis, na ordem das abas: [Análise] Resumo, seções..., Diagnóstico.
        var labels = new List<string>();
        if (analysis is not null) labels.Add(ReportMask.Text(analysis.Title));
        labels.Add(Loc.T("Resumo", "Summary"));
        labels.AddRange(rest.Select(r => ReportMask.Text(r.Title)));
        labels.Add(Loc.T("Diagnóstico", "Diagnosis"));
        int tabs = labels.Count;

        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append("\r\n");

        L("<!DOCTYPE html>");
        L($"<html lang=\"{Loc.Language}\">");
        L("<head>");
        L("<meta charset=\"utf-8\">");
        L("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        L("<meta name=\"color-scheme\" content=\"light dark\">");
        L($"<title>{E(ReportTitle)}</title>");
        L("<style>");
        L(Css(tabs));
        L("</style>");
        L("</head>");
        L("<body>");
        L($"<header><h1>{E(ReportTitle)}</h1>");
        L($"<p class=\"meta\"><span>{E(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", Fmt.Culture))}</span><span>{E(Mode(engine))}</span><span>{E(AppInfo.Title)}</span></p>");
        L($"<p class=\"muted\">{E(MaskNotice)}</p></header>");

        for (int i = 0; i < tabs; i++)
            L($"<input type=\"radio\" name=\"tabs\" id=\"t{i}\" class=\"tabradio\"{(i == 0 ? " checked" : "")}>");

        L("<nav class=\"tabbar\">");
        for (int i = 0; i < tabs; i++) L($"<label for=\"t{i}\">{E(labels[i])}</label>");
        L("</nav>");
        L("<main>");

        int p = 0;
        if (analysis is not null) HtmlSection(sb, analysis, p++, "analysis");

        // Resumo
        L($"<section id=\"p{p++}\" class=\"panel\"><h2>{E(Loc.T("Resumo", "Summary"))}</h2>");
        var top = Bottlenecks.Top(all);
        L($"<h3>{E(Loc.T("Gargalos prováveis para jogos", "Likely bottlenecks for gaming"))}</h3>");
        if (top.Count == 0) L($"<p>{E(Loc.T("Nenhum gargalo relevante identificado nas leituras.", "No relevant bottleneck identified in the readings."))}</p>");
        else
        {
            L("<ol>");
            foreach (var f in top)
                L($"<li><span class=\"badge {SevClass(f.Sev)}\">{E(f.SevLabel)}</span> <b>{E(ReportMask.Text(f.Title))}</b> &ndash; {E(ReportMask.Text(f.Evidence))}</li>");
            L("</ol>");
        }
        var c = Bottlenecks.Count(all);
        L($"<p class=\"counts\"><span class=\"badge ok\">{c.Ok} OK</span> <span class=\"badge info\">{c.Info} {Loc.T("informativo(s)", "informational")}</span> <span class=\"badge warn\">{c.Aviso} {Loc.T("aviso(s)", "warning(s)")}</span> <span class=\"badge bad\">{c.Atencao} {Loc.T("atenção", "attention")}</span></p>");
        L($"<h3>{E(Loc.T("Verificações", "Checks"))}</h3>");
        L($"<div class=\"tw\"><table><thead><tr><th>{E(Loc.T("Seção", "Section"))}</th><th>{E(Loc.T("Gravidade", "Severity"))}</th><th>{E(Loc.T("Verificação", "Check"))}</th><th>{E(Loc.T("Evidência", "Evidence"))}</th></tr></thead><tbody>");
        foreach (var r in results)
            foreach (var f in r.Findings)
                L($"<tr><td>{E(ReportMask.Text(r.Title))}</td><td><span class=\"badge {SevClass(f.Sev)}\">{E(f.SevLabel)}</span></td><td>{E(ReportMask.Text(f.Title))}</td><td>{E(ReportMask.Text(f.Evidence))}</td></tr>");
        L("</tbody></table></div></section>");

        foreach (var r in rest) HtmlSection(sb, r, p++, "");

        // Diagnóstico
        L($"<section id=\"p{p}\" class=\"panel\"><h2>{E(Loc.T("Diagnóstico", "Diagnosis"))}</h2>");
        int k = 0;
        foreach (var f in Diag(all))
        {
            k++;
            L($"<div class=\"card {SevClass(f.Sev)}\"><h3>{k}. <span class=\"badge {SevClass(f.Sev)}\">{E(f.SevLabel)}</span> {E(ReportMask.Text(f.Title))}</h3>");
            L($"<p><b>{Loc.T("Evidência:", "Evidence:")}</b> {E(ReportMask.Text(f.Evidence))}</p>");
            if (!string.IsNullOrWhiteSpace(f.Explain)) L($"<p><b>{Loc.T("O que significa:", "What it means:")}</b> {E(ReportMask.Text(f.Explain))}</p>");
            if (!string.IsNullOrWhiteSpace(f.Action)) L($"<p><b>{Loc.T("O que fazer:", "What to do:")}</b> {E(ReportMask.Text(f.Action))}</p>");
            L("</div>");
        }
        if (k == 0) L($"<p>{E(Loc.T("Nenhum aviso ou ponto de atenção.", "No warnings or attention points."))}</p>");
        var infos = all.Where(f => f.Sev == Severity.Info).ToList();
        if (infos.Count > 0)
        {
            L($"<h3>{E(Loc.T("Notas informativas", "Informational notes"))}</h3><ul>");
            foreach (var f in infos)
            {
                L($"<li><b>{E(ReportMask.Text(f.Title))}</b>: {E(ReportMask.Text(f.Evidence))}");
                if (!string.IsNullOrWhiteSpace(f.Explain)) L($"<br><span class=\"muted\">{Loc.T("Significado:", "Meaning:")} {E(ReportMask.Text(f.Explain))}</span>");
                if (!string.IsNullOrWhiteSpace(f.Action)) L($"<br><span class=\"muted\">{Loc.T("Ação:", "Action:")} {E(ReportMask.Text(f.Action))}</span>");
                L("</li>");
            }
            L("</ul>");
        }
        var notes = results.SelectMany(r => r.Notes).Distinct().ToList();
        L($"<h3>{E(Loc.T("Leituras parciais", "Partial readings"))}</h3>");
        if (notes.Count == 0) L($"<p>{E(Loc.T("Nenhuma. Todas as leituras foram concluídas.", "None. All readings were completed."))}</p>");
        else { L("<ul>"); foreach (var n in notes) L($"<li>{E(ReportMask.Text(n))}</li>"); L("</ul>"); }
        L("</section>");

        L("</main>");
        L($"<footer>{E(AppInfo.Title)} &middot; {E(Loc.T("relatório gerado localmente; nenhum dado foi enviado a terceiros.", "report generated locally; no data was sent to third parties."))}</footer>");
        L("</body>");
        L("</html>");
        return sb.ToString();
    }

    /// <summary>Escreve uma seção (grupos, tabelas, verificações e leituras parciais) como um painel HTML.</summary>
    /// <param name="sb">Destino.</param>
    /// <param name="r">Seção.</param>
    /// <param name="index">Índice do painel, usado no id.</param>
    /// <param name="extraClass">Classes CSS adicionais.</param>
    private static void HtmlSection(StringBuilder sb, SectionResult r, int index, string extraClass)
    {
        void L(string s = "") => sb.Append(s).Append("\r\n");
        L($"<section id=\"p{index}\" class=\"panel {extraClass}\"><h2>{E(ReportMask.Text(r.Title))}</h2>");
        if (!string.IsNullOrEmpty(r.Error)) L($"<p class=\"badge bad\">{E(ReportMask.Text(r.Error))}</p>");
        foreach (var g in r.Groups)
        {
            L($"<h3>{E(ReportMask.Text(g.Title))}</h3>");
            if (g.Rows.Count > 0)
            {
                L("<div class=\"tw\"><table class=\"kv\"><tbody>");
                foreach (var row in g.Rows)
                {
                    var cls = row.Sev is { } sv ? $" class=\"v-{SevClass(sv)}\"" : "";
                    L($"<tr><th>{E(ReportMask.Text(row.Label))}</th><td{cls}>{E(ReportMask.RowValue(row.Label, row.Value))}</td></tr>");
                }
                L("</tbody></table></div>");
            }
            foreach (var t in g.Tables)
            {
                if (!string.IsNullOrWhiteSpace(t.Title)) L($"<h4>{E(ReportMask.Text(t.Title))}</h4>");
                L("<div class=\"tw\"><table><thead><tr>" + string.Concat(t.Columns.Select(x => $"<th>{E(x)}</th>")) + "</tr></thead><tbody>");
                foreach (var row in t.Rows)
                    L("<tr>" + string.Concat(ReportMask.Cells(t, row).Select(x => $"<td>{E(x)}</td>")) + "</tr>");
                L("</tbody></table></div>");
            }
            foreach (var n in g.Notes) L($"<p class=\"muted\">{E(ReportMask.Text(n))}</p>");
        }
        if (r.Findings.Count > 0)
        {
            L($"<h3>{E(Loc.T("Verificações", "Checks"))}</h3><ul class=\"fl\">");
            foreach (var f in r.Findings)
            {
                L($"<li><span class=\"badge {SevClass(f.Sev)}\">{E(f.SevLabel)}</span> <b>{E(ReportMask.Text(f.Title))}</b> &ndash; {E(ReportMask.Text(f.Evidence))}");
                if (!string.IsNullOrWhiteSpace(f.Explain)) L($"<br><span class=\"muted\">{E(ReportMask.Text(f.Explain))}</span>");
                if (!string.IsNullOrWhiteSpace(f.Action)) L($"<br><span class=\"muted\"><b>{Loc.T("O que fazer:", "What to do:")}</b> {E(ReportMask.Text(f.Action))}</span>");
                L("</li>");
            }
            L("</ul>");
        }
        if (r.Notes.Count > 0)
        {
            L($"<h3>{E(Loc.T("Leituras parciais", "Partial readings"))}</h3><ul>");
            foreach (var n in r.Notes) L($"<li>{E(ReportMask.Text(n))}</li>");
            L("</ul>");
        }
        L("</section>");
    }

    /// <summary>CSS embutido do relatório, incluindo as regras que alternam os painéis conforme o botão de aba marcado e o estilo de impressão.</summary>
    /// <param name="tabs">Quantidade de abas.</param>
    private static string Css(int tabs)
    {
        var sb = new StringBuilder();
        sb.Append(@":root{color-scheme:light dark;--bg:#f3f5f9;--fg:#1d2330;--card:#fff;--bd:#d6dae2;--mut:#606a7c;--ok:#1b7f3b;--info:#2563a8;--warn:#a86400;--bad:#b3261e;--acc:#2563a8;--hd1:#1f3a5f;--hd2:#2b5d8f}
@media (prefers-color-scheme:dark){:root{--bg:#14171d;--fg:#e4e7ee;--card:#1d222b;--bd:#323946;--mut:#9aa3b5;--ok:#4cc277;--info:#6aa9ec;--warn:#e5a23a;--bad:#f0746b;--acc:#6aa9ec;--hd1:#18253a;--hd2:#1f3a5f}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 'Segoe UI',system-ui,sans-serif}
header{padding:18px 24px;background:linear-gradient(120deg,var(--hd1),var(--hd2));color:#fff}
header .muted{color:#c9d4e4}
h1{margin:0 0 6px;font-size:22px;letter-spacing:.2px}h2{margin:0 0 12px}h3{margin:18px 0 8px;font-size:16px}h4{margin:12px 0 6px}
p{margin:6px 0}.muted{color:var(--mut)}
.meta{display:flex;flex-wrap:wrap;gap:8px;margin:4px 0}
.meta span{background:rgba(255,255,255,.15);border-radius:10px;padding:0 10px;font-size:12px}
.tabradio{position:absolute;opacity:0;pointer-events:none}
.tabbar{display:flex;flex-wrap:wrap;gap:4px;padding:10px 24px 0;border-bottom:1px solid var(--bd)}
.tabbar label{padding:8px 14px;border:1px solid var(--bd);border-bottom:0;border-radius:8px 8px 0 0;cursor:pointer;background:var(--card);color:var(--mut)}
.tabbar label:hover{color:var(--fg)}
main{padding:16px 24px;max-width:1200px}
.panel{display:none}
.analysis{border-left:4px solid var(--acc);padding-left:14px}
.tw{overflow-x:auto;margin:6px 0}
table{border-collapse:collapse;width:100%;background:var(--card);border-radius:6px}
th,td{border:1px solid var(--bd);padding:5px 9px;text-align:left;vertical-align:top;overflow-wrap:anywhere}
thead th{background:var(--bg)}
table.kv th{width:30%;font-weight:600;background:var(--bg)}
.badge{display:inline-block;padding:0 8px;border-radius:10px;font-size:12px;font-weight:600;border:1px solid currentColor}
.ok{color:var(--ok)}.info{color:var(--info)}.warn{color:var(--warn)}.bad{color:var(--bad)}
td.v-ok{color:var(--ok)}td.v-info{color:var(--info)}td.v-warn{color:var(--warn)}td.v-bad{color:var(--bad)}
.card{background:var(--card);border:1px solid var(--bd);border-left:5px solid currentColor;border-radius:6px;padding:4px 14px;margin:12px 0}
.card h3{margin:8px 0;color:var(--fg)}.card p{color:var(--fg)}
.fl{list-style:none;padding:0}.fl li{padding:4px 0}
footer{padding:12px 24px;color:var(--mut);font-size:12px;border-top:1px solid var(--bd)}
@media (max-width:640px){main,header{padding:10px}.tabbar{padding:6px 10px 0}.tabbar label{padding:6px 9px}}
@media print{body{background:#fff;color:#000}header{background:none;color:#000;border-bottom:2px solid #000}header .muted{color:#444}.meta span{background:none;padding:0}.panel{display:block!important;page-break-inside:auto}.tabbar,.tabradio{display:none}h2{page-break-after:avoid;border-bottom:1px solid #999}table,.card{page-break-inside:avoid}main{max-width:none;padding:0}}
");
        for (int i = 0; i < tabs; i++)
        {
            sb.Append($"#t{i}:checked ~ main #p{i}{{display:block}}\r\n");
            sb.Append($"#t{i}:checked ~ .tabbar label:nth-child({i + 1}){{background:var(--bg);color:var(--fg);border-color:var(--acc);font-weight:600}}\r\n");
            sb.Append($"#t{i}:focus-visible ~ .tabbar label:nth-child({i + 1}){{outline:2px solid var(--acc)}}\r\n");
        }
        return sb.ToString();
    }
}
