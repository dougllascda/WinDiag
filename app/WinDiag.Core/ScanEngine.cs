using System.Diagnostics;
using System.Security.Principal;
using WinDiag.Core.Collectors;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.Core;

/// <summary>
/// Executa a varredura: fase A (SystemCollector, preenche Bcd/Os/energia) e depois os demais
/// coletores em paralelo e, por fim, a análise da plataforma (fase C). Cada resultado é publicado assim que fica pronto (SectionReady),
/// para a interface preencher as abas sem esperar o conjunto todo.
/// </summary>
public sealed class ScanEngine
{
    /// <summary>Fatos compartilhados preenchidos pelos coletores.</summary>
    public SystemFacts Facts { get; } = new();
    /// <summary>Se definido antes de <see cref="RunAsync"/>, substitui o perfil das preferências em <see cref="SystemFacts.Profile"/> (usado por <c>--profile</c> da linha de comando). Não grava nada.</summary>
    public UsageProfile? ProfileOverride { get; set; }
    /// <summary>Resultados publicados (na ordem canônica após <see cref="RunAsync"/>). Acesso concorrente durante a varredura deve usar <see cref="AllFindings"/> ou o evento.</summary>
    public List<SectionResult> Results { get; } = new();
    /// <summary>Disparado (possivelmente fora do thread da interface) quando uma seção fica pronta.</summary>
    public event Action<SectionResult>? SectionReady;

    /// <summary>Ordem de exibição/relatório. O primeiro é a fase A.</summary>
    public static IReadOnlyList<ICollector> CreateCollectors() => new ICollector[]
    {
        new SystemCollector(),
        new CpuCollector(),
        new MemoryCollector(),
        new GpuCollector(),
        new DisplayCollector(),
        new StorageCollector(),
        new SecurityCollector(),
        new ProcessCollector(),
        new EventsCollector(),
        new SensorsCollector(),
        new WorkCollector(),
    };

    /// <summary>Total de seções publicadas por uma varredura: os coletores mais a "Análise da plataforma" (fase C).</summary>
    public static int SectionCount => CreateCollectors().Count + 1;

    /// <summary>true se o processo atual roda como administrador. Falhas devolvem false.</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Executa a varredura completa. Falhas de um coletor viram nota na seção; só o cancelamento propaga exceção.</summary>
    /// <param name="ct">Cancelamento.</param>
    public async Task RunAsync(CancellationToken ct = default)
    {
        Facts.IsAdmin = IsAdmin();
        try
        {
            var prefs = Settings.UserPrefs.Load();
            // Com IsFallback (arquivo ilegível) só se lê: nada aqui grava as preferências.
            if (!prefs.IsFallback)
            {
                Facts.PsuWatts = prefs.PsuWatts;
                Facts.PsuModel = prefs.PsuModel;
                Facts.Profile = Settings.UserPrefs.ProfileOf(prefs.Profile);
            }
        }
        catch (Exception) { /* preferências são opcionais */ }
        if (ProfileOverride is { } forced) Facts.Profile = forced;
        var collectors = CreateCollectors();
        var first = collectors[0];
        Publish(await Task.Run(() => RunOne(first), ct));
        var rest = collectors.Skip(1).Select(c => Task.Run(() => RunOne(c), ct)).ToList();
        while (rest.Count > 0)
        {
            var done = await Task.WhenAny(rest);
            rest.Remove(done);
            Publish(await done);
        }
        // Fase C: análise da plataforma, depois de todos os coletores (usa os fatos e os resultados).
        Publish(await Task.Run(RunInsights, ct));
        // Ordena na ordem canônica para relatório.
        var order = collectors.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);
        order[Insights.SectionKey] = collectors.Count;
        lock (Results) Results.Sort((a, b) => order.GetValueOrDefault(a.Key).CompareTo(order.GetValueOrDefault(b.Key)));
    }

    /// <summary>Adiciona o resultado à lista (sob lock, pois vários coletores terminam em threads diferentes) e avisa a interface pelo evento <see cref="SectionReady"/>.</summary>
    /// <param name="r">Seção concluída.</param>
    private void Publish(SectionResult r)
    {
        lock (Results) Results.Add(r);
        SectionReady?.Invoke(r);
    }

    /// <summary>Fase C: roda a análise da plataforma sobre uma cópia dos resultados já publicados. Falha vira nota e erro da seção, nunca exceção.</summary>
    /// <returns>Seção "Análise da plataforma", com o tempo gasto.</returns>
    private SectionResult RunInsights()
    {
        var sw = Stopwatch.StartNew();
        SectionResult r;
        try
        {
            List<SectionResult> snapshot;
            lock (Results) snapshot = Results.ToList();
            r = Insights.Analyze(Facts, snapshot);
        }
        catch (Exception ex)
        {
            r = new SectionResult { Key = Insights.SectionKey, Title = Insights.SectionTitle, Error = ex.Message };
            r.Note(Loc.T($"{Insights.SectionTitle}: falha inesperada na análise ({ex.GetType().Name}: {ex.Message})",
                         $"{Insights.SectionTitle}: unexpected analysis failure ({ex.GetType().Name}: {ex.Message})"));
        }
        EnsureIds(r);
        r.Elapsed = sw.Elapsed;
        return r;
    }

    /// <summary>Garante Section e Id: achados sem Id (criados fora de <see cref="SectionResult.Find"/>) recebem Seção + "." + slug do título.</summary>
    private static void EnsureIds(SectionResult r)
    {
        for (int i = 0; i < r.Findings.Count; i++)
        {
            var f = r.Findings[i];
            if (string.IsNullOrEmpty(f.Section)) f.Section = r.Key;
            if (string.IsNullOrEmpty(f.Id)) r.Findings[i] = f with { Id = SectionResult.DefaultId(r.Key, f.Title) };
        }
    }

    /// <summary>Executa um coletor isolando falhas: se ele lançar exceção, a seção sai vazia com o erro e uma nota explicativa, e as demais seguem normalmente.</summary>
    /// <param name="c">Coletor a executar.</param>
    /// <returns>Resultado do coletor, com Ids garantidos e tempo gasto.</returns>
    private SectionResult RunOne(ICollector c)
    {
        var sw = Stopwatch.StartNew();
        SectionResult r;
        try { r = c.Collect(Facts); }
        catch (Exception ex)
        {
            r = new SectionResult { Key = c.Key, Title = c.Title, Error = ex.Message };
            r.Note(Loc.T($"{c.Title}: falha inesperada na leitura ({ex.GetType().Name}: {ex.Message})",
                         $"{c.Title}: unexpected read failure ({ex.GetType().Name}: {ex.Message})"));
        }
        EnsureIds(r);
        r.Elapsed = sw.Elapsed;
        return r;
    }

    /// <summary>true se a seção "trabalho" publicou notas de leitura parcial (veja <see cref="GameScore.HasPartialWork"/>); repasse a <see cref="GameScore.Compute(IEnumerable{Finding}, UsageProfile, bool)"/>.</summary>
    public bool WorkReadingsPartial { get { lock (Results) return GameScore.HasPartialWork(Results); } }

    /// <summary>Cópia dos achados de todas as seções publicadas até agora (thread-safe).</summary>
    public IEnumerable<Finding> AllFindings()
    {
        lock (Results) return Results.SelectMany(r => r.Findings).ToList();
    }
}
