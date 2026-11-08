using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Processador: modelo, núcleos/threads, caches, virtualização e codinome aproximado.
/// Fonte: WMI (Win32_Processor, Win32_CacheMemory, Win32_ComputerSystem). Não exige administrador.
/// Se o processador não puder ser lido, a seção mostra "n/d" e registra uma nota. Clock instantâneo e carga ficam em <see cref="Live.CpuLive"/>.
/// </summary>
public sealed class CpuCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "cpu";
    /// <inheritdoc/>
    public string Title => Loc.T("Processador", "Processor");

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        void Err(string s) => res.Note(s);

        var cpu = Wmi.First("SELECT * FROM Win32_Processor", onError: Err);
        if (cpu is null)
        {
            res.Note(Loc.T("Não foi possível ler o processador (Win32_Processor)", "Could not read the processor (Win32_Processor)"));
            res.Group(Loc.T("Processador", "Processor")).Add(Loc.T("Modelo", "Model"), null);
            return res;
        }

        var desc = cpu.S("Description");
        var manufacturer = cpu.S("Manufacturer");
        int cores = (int)(cpu.L("NumberOfCores") ?? 0);
        int threads = (int)(cpu.L("NumberOfLogicalProcessors") ?? 0);
        int baseMHz = (int)(cpu.L("MaxClockSpeed") ?? 0);
        facts.CpuCores = cores;
        facts.CpuThreads = threads;
        facts.CpuBaseMHz = baseMHz;
        facts.CpuName = cpu.S("Name");
        try { facts.CpuTdpW = Knowledge.PlatformDb.ResolveCpu(facts.CpuName)?.TdpW; } catch { facts.CpuTdpW = null; }

        string? codename = null;
        try { codename = CpuCodename.FromDescription(desc, manufacturer); } catch { }

        bool hvPresent = facts.HypervisorPresent;
        try
        {
            var cs = Wmi.First("SELECT HypervisorPresent FROM Win32_ComputerSystem", onError: Err);
            if (cs?.B("HypervisorPresent") == true) hvPresent = true;
        }
        catch { }

        var g = res.Group(Loc.T("Processador", "Processor"));
        g.Add(Loc.T("Modelo", "Model"), cpu.S("Name"));
        g.Add(Loc.T("Família / Modelo / Stepping", "Family / Model / Stepping"), desc);
        g.Add(Loc.T("Codinome / litografia", "Codename / lithography"), codename is null ? Fmt.NA : Loc.T($"{codename} (aprox., por tabela)", $"{codename} (approx., from lookup table)"));
        g.Add(Loc.T("Soquete", "Socket"), cpu.S("SocketDesignation"));
        g.Add(Loc.T("Núcleos / Threads", "Cores / Threads"), $"{cores} / {threads}");
        g.Add(Loc.T("Clock nominal (base)", "Nominal clock (base)"), baseMHz > 0 ? $"{baseMHz} MHz" : null);

        var l2 = cpu.L("L2CacheSize"); var l3 = cpu.L("L3CacheSize");
        g.Add(Loc.T("Cache L2 / L3 (total)", "L2 / L3 cache (total)"), $"{(l2?.ToString() ?? Fmt.NA)} KB / {(l3?.ToString() ?? Fmt.NA)} KB");
        try
        {
            var caches = Wmi.Query("SELECT Purpose, InstalledSize FROM Win32_CacheMemory", onError: Err);
            var l1 = caches.Where(c => (c.S("Purpose") ?? "").Contains("L1", StringComparison.OrdinalIgnoreCase))
                           .Select(c => $"{c.L("InstalledSize")} KB").ToList();
            if (l1.Count > 0) g.Add(Loc.T("Cache L1 (blocos)", "L1 cache (blocks)"), string.Join(" + ", l1));
        }
        catch { res.Note(Loc.T("Cache L1 não lido", "L1 cache not read")); }

        g.Add(Loc.T("Virtualização no firmware", "Firmware virtualization"), hvPresent
            ? Loc.T("ativa (o hypervisor está rodando; o Windows oculta este campo)", "enabled (the hypervisor is running; Windows hides this field)")
            : (object?)cpu.B("VirtualizationFirmwareEnabled"));
        g.Add(Loc.T("Temperatura da CPU", "CPU temperature"), Loc.T("n/d (o Windows não expõe o sensor do núcleo; use HWiNFO)", "n/a (Windows does not expose the core sensor; use HWiNFO)"));
        g.Notes.Add(Loc.T("Clock efetivo, carga e DPC são lidos ao vivo na aba de monitoramento; leituras em repouso não mostram turbo nem throttling.", "Effective clock, load and DPC are read live in the monitoring tab; idle readings do not show turbo or throttling."));

        if (cores > 0 && cores <= 4)
            res.Find(Severity.Info, Loc.T($"CPU de {cores} núcleos", $"{cores}-core CPU"), $"{cores}C/{threads}T",
                Loc.T("Em jogos modernos e com Discord, navegador e launcher abertos, 4 núcleos chegam facilmente ao limite. Quando a CPU está saturada, o jogo sofre engasgos mesmo com GPU folgada. A captura de stutter mostra se este é o seu caso.",
                    "In modern games with Discord, a browser and a launcher open, 4 cores easily reach their limit. When the CPU is saturated, the game stutters even with GPU headroom. The stutter capture shows whether this is your case."),
                Loc.T("Feche navegador e overlays antes de jogar e compare.", "Close the browser and overlays before playing and compare."),
                id: "cpu.few_cores",
                simple: Loc.T("Seu processador tem poucos núcleos. Em jogos modernos, com outros programas abertos, ele pode ser o primeiro a chegar no limite.", "Your processor has few cores. In modern games, with other programs open, it may be the first to reach its limit."));
        return res;
    }
}
