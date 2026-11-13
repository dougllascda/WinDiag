using System.Text;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;

namespace WinDiag.Core.Rules;

/// <summary>Custo de um passo do plano.</summary>
public enum ActionCost
{
    /// <summary>Sem custo (ajuste de configuração).</summary>
    Gratis,
    /// <summary>Exige trocar ou comprar peça.</summary>
    Hardware
}

/// <summary>Passo do plano de ação.</summary>
/// <param name="Key">Chave estável usada para lembrar o que foi feito: o Id do achado, quando existir; senão seção + título normalizado.</param>
/// <param name="Order">Posição no plano (1..n).</param>
/// <param name="Title">Título do achado.</param>
/// <param name="Where">Onde está o limite.</param>
/// <param name="What">O que fazer.</param>
/// <param name="Cost">Custo.</param>
/// <param name="Gain">Ganho esperado ("" se não informado).</param>
/// <param name="Sev">Gravidade do achado.</param>
/// <param name="Impact">Impacto de 0 a 100.</param>
/// <param name="FixId">Otimização relacionada ("" se nenhuma).</param>
/// <param name="SectionKey">Seção de origem.</param>
/// <param name="FindingId">Id estável do achado principal ("" se não houver).</param>
/// <param name="GuideTopic">Nome de um BiosTopic com passo a passo, ou "".</param>
/// <param name="Simple">Explicação simples para leigos ("" se não houver).</param>
/// <param name="ToolId">Id da ferramenta (<c>ToolCatalog</c>) que resolve o achado principal, ou null.</param>
public sealed record ActionStep(string Key, int Order, string Title, string Where, string What, ActionCost Cost, string Gain, Severity Sev, int Impact, string FixId, string SectionKey,
    string FindingId = "", string GuideTopic = "", string Simple = "", string? ToolId = null);

/// <summary>Monta a lista ordenada de passos a partir dos achados.</summary>
public static partial class ActionPlan
{
    /// <summary>Marcadores (pt e en) do trecho "onde está o limite" no texto explicativo do achado.</summary>
    private static readonly string[] WherePrefixes = { "Onde está o limite:", "Where the limit is:" };
    /// <summary>Marcadores (pt e en) do trecho "o que dá para melhorar" na ação do achado.</summary>
    private static readonly string[] WhatPrefixes = { "O que dá para melhorar:", "What can be improved:" };
    /// <summary>Marcadores (pt e en) do trecho "ganho esperado" na ação do achado.</summary>
    private static readonly string[] GainPrefixes = { "Ganho esperado:", "Expected gain:" };

    /// <summary>Procura o primeiro marcador (pt ou en) no texto; devolve posição e tamanho do marcador, ou -1.</summary>
    private static (int Index, int Length) FindMarker(string text, string[] markers)
    {
        int best = -1, len = 0;
        foreach (var m in markers)
        {
            int i = text.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) { best = i; len = m.Length; }
        }
        return (best, len);
    }

    /// <summary>Reconhece o marcador de custo no texto da ação, como "(custo: grátis)" ou "(cost: hardware)".</summary>
    [GeneratedRegex(@"\((?:custo|cost):\s*(grátis|gratis|free|hardware)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CostRx();

    /// <summary>Constrói o plano: grátis antes de hardware; dentro, maior gravidade e impacto primeiro.</summary>
    /// <param name="findings">Achados da varredura.</param>
    public static List<ActionStep> Build(IEnumerable<Finding> findings) => Build(findings, UsageProfile.Gaming);

    /// <summary>Constrói o plano do perfil: só achados de escopo Both ou do perfil; grátis antes de hardware; dentro, maior gravidade e impacto primeiro.</summary>
    /// <param name="findings">Achados da varredura.</param>
    /// <param name="profile">Perfil de uso.</param>
    public static List<ActionStep> Build(IEnumerable<Finding> findings, UsageProfile profile)
    {
        var relevant = findings.Where(f => ScopeRules.Matches(f.Scope, profile) && (
            ((f.Sev == Severity.Aviso || f.Sev == Severity.Atencao) && !string.IsNullOrWhiteSpace(f.Action))
            || (f.Sev == Severity.Info && (!string.IsNullOrEmpty(f.FixId) || f.Impact >= 30))))
            .OrderByDescending(f => f.Sev).ThenByDescending(f => f.Impact).ToList();

        // Agrupa por Action idêntica (quando houver) ou por título.
        var groups = new List<List<Finding>>();
        var byKey = new Dictionary<string, List<Finding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in relevant)
        {
            string k = !string.IsNullOrWhiteSpace(f.Action) ? f.Section + "|A|" + f.Action.Trim() : f.Section + "|T|" + Norm(f.Title);
            if (!byKey.TryGetValue(k, out var g)) { g = new(); byKey[k] = g; groups.Add(g); }
            g.Add(f);
        }

        var steps = new List<ActionStep>();
        var keys = new HashSet<string>();
        foreach (var g in groups)
        {
            var main = g[0];
            var titles = g.Select(x => x.Title.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string title = titles[0];
            string baseTitle = titles[0]; // título sem a lista de itens do grupo (guardado à parte; não se corta o texto exibido)
            if (titles.Count > 1)
            {
                int c = titles[0].IndexOf(": ", StringComparison.Ordinal);
                string prefix = c > 0 ? titles[0][..c] : "";
                if (prefix.Length > 0 && titles.All(t => t.StartsWith(prefix + ": ", StringComparison.OrdinalIgnoreCase)))
                {
                    title = $"{prefix} ({string.Join(", ", titles.Select(t => t[(prefix.Length + 2)..].Trim()))})";
                    baseTitle = prefix;
                }
            }
            // Chave só de campos estáveis: Id do achado (o menor, se o grupo tiver vários, para não depender da ordem);
            // sem Id, seção + ":" + FixId; só em último caso o título normalizado (depende do idioma).
            string? firstId = g.Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            string key = !string.IsNullOrWhiteSpace(firstId)
                ? firstId
                : !string.IsNullOrWhiteSpace(main.FixId)
                    ? main.Section + ":" + main.FixId
                    : main.Section + ":" + Norm(baseTitle);
            string guide = g.Select(x => x.GuideTopic).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
            string simple = !string.IsNullOrEmpty(main.Simple) ? main.Simple : g.Select(x => x.Simple).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
            if (!keys.Add(key)) continue;
            var (where, what, cost, gain) = Parse(main);
            steps.Add(new ActionStep(key, 0, title, where, what, cost, gain, g.Max(x => x.Sev), g.Max(x => x.Impact), main.FixId ?? "", main.Section, main.Id ?? "", guide, simple, g.Select(x => x.ToolId).FirstOrDefault(x => !string.IsNullOrEmpty(x))));
        }

        return steps
            .OrderBy(s => s.Cost).ThenByDescending(s => s.Sev).ThenByDescending(s => s.Impact).ThenBy(s => s.Title, StringComparer.Ordinal)
            .Select((s, i) => s with { Order = i + 1 }).ToList();
    }

    /// <summary>Separa o texto de um achado em onde está o limite, o que fazer, o custo e o ganho. O custo vem do marcador explícito; sem ele, verbos de troca ou compra associados a uma peça indicam hardware, e o resto é grátis.</summary>
    /// <param name="f">Achado.</param>
    private static (string Where, string What, ActionCost Cost, string Gain) Parse(Finding f)
    {
        string where = "";
        var (w, wLen) = FindMarker(f.Explain, WherePrefixes);
        if (w >= 0)
        {
            string rest = f.Explain[(w + wLen)..].Trim();
            var m = Regex.Match(rest, @"\.(\s|$)");
            where = (m.Success ? rest[..m.Index] : rest).Trim();
        }
        else where = SectionName(f.Section);

        string action = f.Action ?? "";
        string what = action, gain = "";
        var cost = ActionCost.Gratis;
        var (g, gLen) = FindMarker(action, GainPrefixes);
        if (g >= 0) { gain = action[(g + gLen)..].Trim(); what = action[..g]; }
        var (p, pLen) = FindMarker(what, WhatPrefixes);
        if (p >= 0) what = what[(p + pLen)..];
        var cm = CostRx().Match(action);
        if (cm.Success) cost = cm.Groups[1].Value.Equals("hardware", StringComparison.OrdinalIgnoreCase) ? ActionCost.Hardware : ActionCost.Gratis;
        else if (Regex.IsMatch(action, @"\b(trocar|comprar|adicionar um)\b.*\b(CPU|placa|SSD|pente|memória|fonte)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(action, @"\b(replace|buy|add (?:a|an|more))\b.*\b(CPU|GPU|graphics card|SSD|RAM|memory|module|power supply|PSU)\b", RegexOptions.IgnoreCase)) cost = ActionCost.Hardware;
        what = CostRx().Replace(what, "");
        what = Regex.Replace(what, @"\s+", " ").Trim().TrimEnd('.', ' ');
        what = what.Length > 0 ? char.ToUpperInvariant(what[0]) + what[1..] + "." : what;
        if (what.Length == 0) what = f.Title;
        return (where, what, cost, gain);
    }

    /// <summary>Nome da seção no idioma atual, usado quando o achado não informa onde está o limite.</summary>
    /// <param name="s">Chave da seção.</param>
    private static string SectionName(string s) => s switch
    {
        "gpu" => Loc.T("Placa de vídeo", "Graphics card"),
        "analise" => Loc.T("Análise da plataforma", "Platform analysis"),
        "monitores" => Loc.T("Monitores", "Monitors"),
        "cpu" => Loc.T("Processador", "Processor"),
        "memoria" => Loc.T("Memória", "Memory"),
        "armazenamento" => Loc.T("Armazenamento", "Storage"),
        "sistema" => Loc.T("Sistema", "System"),
        "seguranca" => Loc.T("Segurança", "Security"),
        "eventos" => Loc.T("Eventos", "Events"),
        "sensores" => Loc.T("Sensores", "Sensors"),
        "processos" => Loc.T("Processos e inicialização", "Processes and startup"),
        "trabalho" => Loc.T("Trabalho e escritório", "Work and office"),
        _ => s
    };

    /// <summary>Normaliza um título (sem acentos, minúsculo, sem pontuação) para servir de chave de agrupamento estável entre execuções.</summary>
    /// <param name="title">Título original.</param>
    private static string Norm(string title)
    {
        var sb = new StringBuilder();
        foreach (char ch in title.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
