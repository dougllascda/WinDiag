using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Processos, inicialização, rede e áudio. Fontes: <see cref="System.Diagnostics.Process"/> (CPU/RAM por processo, medidos em 1 s),
/// WMI (contadores de memória da GPU, adaptadores de rede, dispositivos de áudio) e registro/pasta Inicializar (itens de inicialização).
/// O processo atual e seus filhos diretos ficam fora dos rankings, para o próprio diagnóstico não aparecer como consumidor.
/// Não exige administrador, mas processos protegidos não expõem o tempo de CPU e são ignorados. Falhas viram notas.
/// </summary>
public sealed class ProcessCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "processos";
    /// <inheritdoc/>
    public string Title => Loc.T("Processos, inicialização, rede e áudio", "Processes, startup, network and audio");

    /// <summary>Nomes de processos de aplicativos que costumam ficar em segundo plano (navegadores, launchers, mensageiros).</summary>
    public static readonly Regex BgAppRegex = new(@"^(msedge|msedgewebview2|chrome|firefox|opera|brave|Discord|steamwebhelper|OneDrive|Teams|ms-teams|Spotify|CurseForge|Overwolf|EpicGamesLauncher|EpicWebHelper|Battle\.net|EADesktop|Code|Antigravity|language_server.*|Gemini|claude|WhatsApp|Telegram)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex HeavyStartExtra = new("Discord|Spotify|Steam|CurseForge|Epic|OneDrive|Teams|Overwolf", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        try { Top(res); } catch (Exception ex) { res.Note(Loc.T("Processos: ", "Processes: ") + ex.Message); }
        try { Gpu(res); } catch (Exception ex) { res.Note(Loc.T("Memória de vídeo: ", "Video memory: ") + ex.Message); }
        try { StartupPart(res, facts); } catch (Exception ex) { res.Note(Loc.T("Inicialização: ", "Startup: ") + ex.Message); }
        try { NetAudio(res); } catch (Exception ex) { res.Note(Loc.T("Rede/áudio: ", "Network/audio: ") + ex.Message); }
        try { Background(res); } catch (Exception ex) { res.Note(Loc.T("Segundo plano: ", "Background: ") + ex.Message); }
        return res;
    }

    /// <summary>
    /// Devolve o PID informado e os de seus filhos diretos, a partir de pares (PID, PID do pai).
    /// Usado para tirar o próprio WinDiag (e processos que ele inicia, como powershell/nvidia-smi) dos rankings.
    /// </summary>
    internal static HashSet<int> SelfAndChildren(int selfPid, IEnumerable<(int Pid, int ParentPid)> processes)
    {
        var set = new HashSet<int> { selfPid };
        foreach (var (pid, parent) in processes)
            if (parent == selfPid) set.Add(pid);
        return set;
    }

    /// <summary>Lê (PID, PID do pai) via WMI; em falha devolve vazio e só o processo atual será excluído.</summary>
    private static HashSet<int> ExcludedPids()
    {
        var list = new List<(int, int)>();
        var objs = Wmi.Query("SELECT ProcessId, ParentProcessId FROM Win32_Process");
        try
        {
            foreach (var o in objs)
            {
                var pid = o.L("ProcessId"); var ppid = o.L("ParentProcessId");
                if (pid is not null && ppid is not null) list.Add(((int)pid.Value, (int)ppid.Value));
            }
        }
        finally { Wmi.DisposeAll(objs); }
        return SelfAndChildren(Environment.ProcessId, list);
    }

    static void Top(SectionResult res)
    {
        var skip = ExcludedPids();
        var first = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (p.Id != 0 && !skip.Contains(p.Id)) first[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch (Exception) { /* processo protegido ou encerrado */ }
            }
        }
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1000);
        var rows = new List<(string Name, int Id, double Pct)>();
        foreach (var kv in first)
        {
            try
            {
                using var p = Process.GetProcessById(kv.Key);
                var d = p.TotalProcessorTime - kv.Value.Cpu;
                var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                rows.Add((kv.Value.Name, kv.Key, Math.Max(0, d.TotalSeconds) / secs * 100));
            }
            catch (Exception) { /* processo encerrou durante a medição */ }
        }
        if (first.Count == 0) res.Note(Loc.T("CPU por processo: sem leitura", "CPU per process: no reading"));
        var g = res.Group(Loc.T("Top 5 processos por CPU agora (100% = 1 thread inteira)", "Top 5 processes by CPU now (100% = 1 full thread)"));
        var t = g.Table(Loc.T("Processos por CPU", "Processes by CPU"), Loc.T("Processo", "Process"), "PID", "CPU");
        var top = rows.OrderByDescending(r => r.Pct).Take(5).ToList();
        foreach (var r in top) t.AddRow(r.Name, r.Id, Fmt.Num(r.Pct, 0) + "%");
        var av = top.FirstOrDefault(r => r.Pct >= 25 && SecurityCollector.AvProcRegex.IsMatch(r.Name));
        if (av.Name != null)
            res.Find(Severity.Info, Loc.T("Antivírus usando CPU na coleta", "Antivirus using CPU during collection"), Loc.T($"{Regex.Replace(av.Name, @"#\d+$", "")} com {Fmt.Num(av.Pct, 0)}% de CPU", $"{Regex.Replace(av.Name, @"#\d+$", "")} at {Fmt.Num(av.Pct, 0)}% CPU"),
                Loc.T("O antivírus estava entre os processos mais pesados no momento da coleta. É comum ele varrer o próprio utilitário enquanto ele roda, então isso não prova consumo constante. Se aparecer também na captura durante o jogo, aí sim pesa.", "The antivirus was among the heaviest processes at the time of collection. It is common for it to scan this utility while it runs, so this does not prove constant consumption. If it also shows up in the capture during gameplay, then it matters."),
                Loc.T("Repita a coleta; se continuar alto, verifique se há varredura agendada ou atualização do antivírus em andamento.", "Repeat the collection; if it stays high, check whether a scheduled scan or an antivirus update is in progress."), id: "processos.av_cpu");

        var g2 = res.Group(Loc.T("Top 8 processos por RAM", "Top 8 processes by RAM"));
        var t2 = g2.Table(Loc.T("Processos por RAM", "Processes by RAM"), Loc.T("Processo", "Process"), "PID", "Working set");
        var ram = new List<(string Name, int Id, double MB)>();
        foreach (var p in Process.GetProcesses())
            using (p) { try { if (!skip.Contains(p.Id)) ram.Add((p.ProcessName, p.Id, p.WorkingSet64 / 1048576d)); } catch (Exception) { /* processo protegido ou encerrado */ } }
        foreach (var r in ram.OrderByDescending(r => r.MB).Take(8)) t2.AddRow(r.Name, r.Id, Fmt.Num(r.MB, 0) + " MB");
    }

    static void Gpu(SectionResult res)
    {
        var gm = Wmi.Query("SELECT DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", onError: m => res.Note(Loc.T("Contadores de memória da GPU: ", "GPU memory counters: ") + m));
        if (gm.Count == 0) return;
        double ded = gm.Sum(x => x.D("DedicatedUsage") ?? 0), shr = gm.Sum(x => x.D("SharedUsage") ?? 0);
        Wmi.DisposeAll(gm);
        res.Group(Loc.T("Memória de vídeo", "Video memory")).Add(Loc.T("Memória de vídeo agora", "Video memory now"), Loc.T($"dedicada {Fmt.Num(ded / 1048576d, 0)} MB | compartilhada (RAM) {Fmt.Num(shr / 1048576d, 0)} MB", $"dedicated {Fmt.Num(ded / 1048576d, 0)} MB | shared (RAM) {Fmt.Num(shr / 1048576d, 0)} MB"));
    }

    static void StartupPart(SectionResult res, SystemFacts f)
    {
        var items = Startup.Read();
        f.StartupItems = items;
        var g = res.Group(Loc.T("Programas de inicialização", "Startup programs"));
        if (items.Count == 0) g.Notes.Add(Loc.T("Nenhum item encontrado nas chaves Run e pastas Inicializar.", "No items found in the Run keys and Startup folders."));
        else
        {
            var t = g.Table(Loc.T("Itens", "Items"), Loc.T("Nome", "Name"), Loc.T("Local", "Location"), Loc.T("Estado", "State"));
            foreach (var it in items) t.AddRow(it.Name, it.Location, it.Enabled ? Loc.T("habilitado", "enabled") : Loc.T("desabilitado", "disabled"));
        }
        var heavy = items.Where(i => i.Enabled && (BgAppRegex.IsMatch(i.Name) || HeavyStartExtra.IsMatch(i.Name))).ToList();
        if (heavy.Count > 0)
        {
            var heavyNames = string.Join(", ", heavy.Select(h => h.Name).Distinct());
            res.Find(Severity.Info, Loc.T("Apps pesados na inicialização", "Heavy apps at startup"), Loc.T($"{heavy.Count} item(ns) habilitado(s): {heavyNames}", $"{heavy.Count} enabled item(s): {heavyNames}"),
                Loc.T("Esses programas iniciam com o Windows e ficam em segundo plano consumindo RAM, CPU e disco até você fechar. Desabilitar na inicialização não desinstala nada; você abre o app quando quiser.", "These programs start with Windows and stay in the background using RAM, CPU and disk until you close them. Disabling them at startup does not uninstall anything; you open the app whenever you want."),
                Loc.T("Use a correção automática (desabilita só na inicialização, com restauração) ou o Gerenciador de Tarefas > Aplicativos de inicialização.", "Use the automatic fix (disables only at startup, with restore) or Task Manager > Startup apps."), "STARTUP_APPS", 25, id: "processos.startup_apps",
                simple: Loc.T("Vários programas abrem junto com o Windows e ficam gastando memória e processador. Você pode impedir que abram sozinhos, sem desinstalar nada.", "Several programs open along with Windows and keep using memory and processor. You can stop them from opening on their own, without uninstalling anything."));
        }
    }

    static void NetAudio(SectionResult res)
    {
        var g = res.Group(Loc.T("Rede e áudio ativos", "Active network and audio"));
        var nics = Wmi.Query("SELECT Name, Speed FROM Win32_NetworkAdapter WHERE NetEnabled=true", onError: m => res.Note("Win32_NetworkAdapter: " + m));
        var vpn = new List<string>();
        if (nics.Count > 0)
        {
            var t = g.Table(Loc.T("Adaptadores de rede", "Network adapters"), Loc.T("Adaptador", "Adapter"), Loc.T("Velocidade", "Speed"), Loc.T("Tipo", "Type"));
            foreach (var n in nics)
            {
                var name = n.S("Name") ?? Fmt.NA;
                bool virt = Regex.IsMatch(name, "VPN|Virtual|TAP|Radmin|Hamachi|ZeroTier|Hyper-V|WAN Miniport", RegexOptions.IgnoreCase);
                var sp = n.D("Speed");
                t.AddRow(name, sp is > 0 ? Fmt.Num(sp.Value / 1e6, 0) + " Mbps" : Loc.T("velocidade n/d", "speed n/a"), virt ? "virtual" : Loc.T("físico", "physical"));
                if (Regex.IsMatch(name, "VPN|TAP|Radmin|Hamachi|ZeroTier", RegexOptions.IgnoreCase)) vpn.Add(name);
            }
        }
        else g.Notes.Add(Loc.T("Nenhum adaptador de rede ativo listado.", "No active network adapters listed."));
        var sd = Wmi.Query("SELECT Name, Status FROM Win32_SoundDevice", onError: m => res.Note("Win32_SoundDevice: " + m));
        if (sd.Count > 0)
        {
            var t = g.Table(Loc.T("Dispositivos de áudio", "Audio devices"), Loc.T("Dispositivo", "Device"), Loc.T("Estado", "Status"));
            foreach (var s in sd) t.AddRow(s.S("Name"), s.S("Status"));
        }
        if (vpn.Count > 0)
            res.Find(Severity.Info, Loc.T("Adaptador de rede virtual/VPN ativo", "Virtual network/VPN adapter active"), string.Join(", ", vpn.Distinct()),
                Loc.T("Adaptadores virtuais de VPN ou redes privadas virtuais podem, em alguns casos, adicionar latência ou carga de DPC. Nem sempre há efeito perceptível.", "Virtual VPN adapters or virtual private networks can, in some cases, add latency or DPC load. The effect is not always noticeable."),
                Loc.T("Se você não usa a VPN ou a rede virtual, desative o adaptador ou feche o aplicativo.", "If you do not use the VPN or virtual network, disable the adapter or close the application."), id: "processos.vpn_adapter");
    }

    static void Background(SectionResult res)
    {
        var heavy = new List<(string Name, double MB)>();
        foreach (var p in Process.GetProcesses())
            using (p) { try { if (BgAppRegex.IsMatch(p.ProcessName)) heavy.Add((p.ProcessName, p.WorkingSet64 / 1048576d)); } catch { } }
        if (heavy.Count == 0) return;
        var byApp = heavy.GroupBy(h => h.Name).Select(x => (Name: x.Key, MB: x.Sum(y => y.MB))).OrderByDescending(x => x.MB).ToList();
        var top5 = string.Join(", ", byApp.Take(5).Select(x => $"{x.Name} {Fmt.Num(x.MB, 0)} MB"));
        var wsTot = Fmt.Num(heavy.Sum(h => h.MB), 0);
        res.Find(Severity.Info, Loc.T("Aplicativos em segundo plano", "Background applications"),
            Loc.T($"{heavy.Count} processos de {byApp.Count} apps, ~{wsTot} MB de working set, com dupla contagem de memória compartilhada (maiores: {top5})", $"{heavy.Count} processes from {byApp.Count} apps, ~{wsTot} MB of working set, with double counting of shared memory (largest: {top5})"),
            Loc.T("Navegadores, IDEs/assistentes, Discord, Spotify e launchers usam CPU, RAM e, com aceleração de hardware, GPU. Numa CPU de 4 núcleos e com 16 GB de RAM isso compete diretamente com o jogo.", "Browsers, IDEs/assistants, Discord, Spotify and launchers use CPU, RAM and, with hardware acceleration, GPU. On a 4-core CPU with 16 GB of RAM this competes directly with the game."),
            Loc.T("Feche-os antes de jogar e compare o resultado na captura durante o jogo.", "Close them before playing and compare the result in the in-game capture."), "", 60,
            id: "processos.background_apps",
            simple: Loc.T("Há programas abertos em segundo plano (navegador, Discord, launchers) usando memória e processador. Feche-os antes de jogar para sobrar mais desempenho.", "There are programs open in the background (browser, Discord, launchers) using memory and processor. Close them before playing to free up performance."));
    }
}
