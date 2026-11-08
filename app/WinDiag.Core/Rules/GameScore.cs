using WinDiag.Core.Model;

namespace WinDiag.Core.Rules;

/// <summary>Perda de pontos de uma área da nota.</summary>
/// <param name="Area">Nome da área (ex.: "Plataforma e GPU").</param>
/// <param name="Lost">Pontos descontados.</param>
/// <param name="Reason">Achados que motivaram o desconto.</param>
public sealed record ScorePart(string Area, int Lost, string Reason);

/// <summary>Resultado da nota de configuração.</summary>
/// <param name="Score">Nota de 0 a 100.</param>
/// <param name="Grade">Classificação (Excelente, Boa, Regular, Limitada).</param>
/// <param name="Summary">Resumo em texto.</param>
/// <param name="Parts">Perdas por área, da maior para a menor.</param>
public sealed record ScoreResult(int Score, string Grade, string Summary, IReadOnlyList<ScorePart> Parts);

/// <summary>Nota de configuração para jogos, calculada a partir dos achados. Não mede desempenho.</summary>
public static class GameScore
{
    private const double AreaCap = 35;

    /// <summary>Calcula a nota: parte de 100 e desconta por achado AVISO/ATENÇÃO (e INFO de impacto alto), com teto por área.</summary>
    /// <param name="findings">Achados da varredura.</param>
    public static ScoreResult Compute(IEnumerable<Finding> findings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var areas = new Dictionary<string, (double Lost, List<(double L, string T)> Items)>();
        foreach (var f in findings.OrderByDescending(x => x.Sev).ThenByDescending(x => x.Impact))
        {
            double w = f.Sev switch
            {
                Severity.Atencao => 0.20,
                Severity.Aviso => 0.12,
                Severity.Info when f.Impact >= 30 => 0.04,
                _ => 0
            };
            if (w <= 0) continue;
            if (!seen.Add(f.Section + "|T|" + f.Title.Trim())) continue;
            if (!string.IsNullOrWhiteSpace(f.Action) && !seen.Add(f.Section + "|A|" + f.Action.Trim())) continue;
            double lost = f.Impact * w;
            if (lost <= 0) continue;
            string area = AreaOf(f.Section);
            if (!areas.TryGetValue(area, out var a)) a = (0, new());
            a.Items.Add((lost, f.Title));
            areas[area] = (a.Lost + lost, a.Items);
        }

        var parts = areas
            .Select(kv => new ScorePart(kv.Key, (int)Math.Round(Math.Min(AreaCap, kv.Value.Lost)),
                string.Join("; ", kv.Value.Items.OrderByDescending(i => i.L).Take(3).Select(i => i.T))))
            .Where(p => p.Lost > 0)
            .OrderByDescending(p => p.Lost).ThenBy(p => p.Area, StringComparer.Ordinal)
            .ToList();
        int score = Math.Clamp(100 - parts.Sum(p => p.Lost), 0, 100);
        string grade = score >= 90 ? Loc.T("Excelente", "Excellent") : score >= 75 ? Loc.T("Boa", "Good") : score >= 55 ? Loc.T("Regular", "Fair") : Loc.T("Limitada", "Limited");
        string summary = Loc.T($"Configuração {grade.ToLowerInvariant()} para jogos ({score}/100). " +
            "Esta é uma nota de configuração, não é benchmark de desempenho" +
            (parts.Count > 0 ? $"; a maior perda está em {parts[0].Area}." : "; nenhum ponto relevante foi encontrado."),
            $"{grade} gaming configuration ({score}/100). " +
            "This is a configuration score, not a performance benchmark" +
            (parts.Count > 0 ? $"; the biggest loss is in {parts[0].Area}." : "; no relevant issues were found."));
        return new ScoreResult(score, grade, summary, parts);
    }

    /// <summary>Área da nota a que uma seção pertence (mesmo mapeamento usado no cálculo). O nome devolvido está no idioma atual (<see cref="Loc"/>), igual ao de <see cref="ScorePart.Area"/> calculado no mesmo idioma; quem compara strings deve usar o mesmo idioma nas duas chamadas.</summary>
    /// <param name="sectionKey">Chave da seção (ex.: "gpu", "memoria").</param>
    public static string AreaOf(string sectionKey) => AreaOfCore(sectionKey);

    private static string AreaOfCore(string section) => section switch
    {
        "gpu" or "analise" or "monitores" => Loc.T("Plataforma e GPU", "Platform and GPU"),
        // Temperaturas (seção "sensores") ficam na área do processador e da memória; não há área separada.
        "cpu" or "memoria" or "sensores" => Loc.T("Processador e memória", "Processor and memory"),
        "armazenamento" => Loc.T("Armazenamento", "Storage"),
        "sistema" or "seguranca" or "eventos" => Loc.T("Sistema e segurança", "System and security"),
        "processos" => Loc.T("Processos e inicialização", "Processes and startup"),
        _ => Loc.T("Sistema e segurança", "System and security")
    };
}
