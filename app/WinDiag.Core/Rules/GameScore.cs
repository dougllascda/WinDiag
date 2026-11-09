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

/// <summary>Nota de configuração (para jogos ou para trabalho), calculada a partir dos achados. Não mede desempenho.</summary>
public static class GameScore
{
    /// <summary>Perda máxima de pontos por área (35). Evita que uma única área concentre a perda e derrube a nota sozinha: o desconto de cada área é limitado a 35 pontos antes de somar.</summary>
    private const double AreaCap = 35;

    /// <summary>Nota máxima do perfil Trabalho quando há leituras parciais na seção "trabalho" (a nota não pode chegar a 100 sem ter lido tudo).</summary>
    public const int PartialMaxScore = 95;

    /// <summary>true se a seção "trabalho" tem notas de leitura (alguma leitura falhou ou ficou parcial).</summary>
    /// <param name="sections">Seções da varredura.</param>
    public static bool HasPartialWork(IEnumerable<SectionResult> sections) =>
        sections.Any(s => s.Key == "trabalho" && s.Notes.Count > 0);

    /// <summary>Nota para jogos (perfil <see cref="UsageProfile.Gaming"/>).</summary>
    /// <param name="findings">Achados da varredura.</param>
    public static ScoreResult Compute(IEnumerable<Finding> findings) => Compute(findings, UsageProfile.Gaming);

    /// <summary>
    /// Calcula a nota: parte de 100 e desconta por achado AVISO/ATENÇÃO (e INFO de impacto alto), com teto por área.
    /// Só entram achados de escopo Both ou do perfil informado.
    /// </summary>
    /// <param name="findings">Achados da varredura.</param>
    /// <param name="profile">Perfil de uso: muda o filtro, as áreas e o texto do resumo.</param>
    /// <param name="partialReadings">true se a seção "trabalho" teve leituras parciais (notas de falha, ex.: exige administrador). No perfil Trabalho a nota máxima passa a ser <see cref="PartialMaxScore"/> e o resumo avisa que a nota é parcial; veja <see cref="HasPartialWork"/>.</param>
    public static ScoreResult Compute(IEnumerable<Finding> findings, UsageProfile profile, bool partialReadings = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var areas = new Dictionary<string, (double Lost, List<(double L, string T)> Items)>();
        foreach (var f in findings.Where(x => ScopeRules.Matches(x.Scope, profile)).OrderByDescending(x => x.Sev).ThenByDescending(x => x.Impact))
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
            string area = AreaOf(f, profile);
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
        bool partial = partialReadings && profile == UsageProfile.Work;
        int score = Math.Clamp(100 - parts.Sum(p => p.Lost), 0, partial ? PartialMaxScore : 100);
        string grade = score >= 90 ? Loc.T("Excelente", "Excellent") : score >= 75 ? Loc.T("Boa", "Good") : score >= 55 ? Loc.T("Regular", "Fair") : Loc.T("Limitada", "Limited");
        bool work = profile == UsageProfile.Work;
        string forPt = work ? "trabalho" : "jogos";
        string forEn = work ? "work" : "gaming";
        string summary = Loc.T($"Configuração {grade.ToLowerInvariant()} para {forPt} ({score}/100). " +
            "Esta é uma nota de configuração, não é benchmark de desempenho" +
            (parts.Count > 0 ? $"; a maior perda está em {parts[0].Area}." : "; nenhum ponto relevante foi encontrado."),
            $"{grade} configuration for {forEn} ({score}/100). " +
            "This is a configuration score, not a performance benchmark" +
            (parts.Count > 0 ? $"; the biggest loss is in {parts[0].Area}." : "; no relevant issues were found."));
        if (partial)
            summary += Loc.T(" Nota parcial: algumas leituras não foram possíveis (ex.: exige administrador).", " Partial score: some readings were not possible (e.g. requires administrator).");
        return new ScoreResult(score, grade, summary, parts);
    }

    /// <summary>Título da nota no perfil: "Configuração para jogos" ou "Configuração para trabalho" (idioma atual).</summary>
    /// <param name="profile">Perfil de uso.</param>
    public static string TitleOf(UsageProfile profile) => profile == UsageProfile.Work
        ? Loc.T("Configuração para trabalho", "Configuration for work")
        : Loc.T("Configuração para jogos", "Configuration for gaming");

    /// <summary>Área da nota (perfil Jogos) a que uma seção pertence (mesmo mapeamento usado no cálculo). O nome devolvido está no idioma atual (<see cref="Loc"/>), igual ao de <see cref="ScorePart.Area"/> calculado no mesmo idioma; quem compara strings deve usar o mesmo idioma nas duas chamadas.</summary>
    /// <param name="sectionKey">Chave da seção (ex.: "gpu", "memoria").</param>
    public static string AreaOf(string sectionKey) => AreaOfCore(sectionKey);

    /// <summary>
    /// Área da nota de uma seção em um perfil. No perfil Trabalho a seção "trabalho" devolve a área "Desempenho e inicialização";
    /// para a área exata de um achado dessa seção use <see cref="AreaOf(Finding, UsageProfile)"/> (é o que o cálculo usa).
    /// </summary>
    /// <param name="sectionKey">Chave da seção.</param>
    /// <param name="profile">Perfil de uso.</param>
    public static string AreaOf(string sectionKey, UsageProfile profile) =>
        profile == UsageProfile.Work ? WorkAreaOfSection(sectionKey) : AreaOfCore(sectionKey);

    /// <summary>Área da nota de um achado em um perfil (mesmo mapeamento do cálculo). No perfil Trabalho os achados da seção "trabalho" são separados por assunto pelo Id.</summary>
    /// <param name="finding">Achado.</param>
    /// <param name="profile">Perfil de uso.</param>
    public static string AreaOf(Finding finding, UsageProfile profile)
    {
        if (profile != UsageProfile.Work) return AreaOfCore(finding.Section);
        if (finding.Section == "trabalho")
        {
            var id = finding.Id ?? "";
            if (id.StartsWith("trabalho.print", StringComparison.Ordinal) || id.StartsWith("trabalho.spooler", StringComparison.Ordinal)) return WorkAreaPrint();
            if (id.StartsWith("trabalho.battery", StringComparison.Ordinal) || id.StartsWith("trabalho.power", StringComparison.Ordinal)) return WorkAreaBattery();
            if (id.StartsWith("trabalho.update", StringComparison.Ordinal) || id.StartsWith("trabalho.reboot", StringComparison.Ordinal)) return WorkAreaSecurity();
            if (id.StartsWith("trabalho.low_ram", StringComparison.Ordinal) || id.StartsWith("trabalho.system_hdd", StringComparison.Ordinal)) return WorkAreaStorage();
        }
        return WorkAreaOfSection(finding.Section);
    }

    /// <summary>Área de desempenho e inicialização do perfil Trabalho.</summary>
    private static string WorkAreaPerf() => Loc.T("Desempenho e inicialização", "Performance and startup");
    /// <summary>Área de armazenamento e memória do perfil Trabalho.</summary>
    private static string WorkAreaStorage() => Loc.T("Armazenamento e memória", "Storage and memory");
    /// <summary>Área de impressão do perfil Trabalho.</summary>
    private static string WorkAreaPrint() => Loc.T("Impressão", "Printing");
    /// <summary>Área de atualizações e segurança do perfil Trabalho.</summary>
    private static string WorkAreaSecurity() => Loc.T("Atualizações e segurança", "Updates and security");
    /// <summary>Área de bateria e energia do perfil Trabalho.</summary>
    private static string WorkAreaBattery() => Loc.T("Bateria e energia", "Battery and power");

    /// <summary>Área de uma seção no perfil Trabalho. Seção desconhecida cai em atualizações e segurança.</summary>
    /// <param name="section">Chave da seção.</param>
    private static string WorkAreaOfSection(string section) => section switch
    {
        "memoria" or "armazenamento" => WorkAreaStorage(),
        "seguranca" or "eventos" => WorkAreaSecurity(),
        "cpu" or "processos" or "sensores" or "gpu" or "analise" or "monitores" or "sistema" or "trabalho" => WorkAreaPerf(),
        _ => WorkAreaSecurity()
    };

    /// <summary>Área de uma seção no perfil Jogos. Seção desconhecida cai em sistema e segurança.</summary>
    /// <param name="section">Chave da seção.</param>
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
