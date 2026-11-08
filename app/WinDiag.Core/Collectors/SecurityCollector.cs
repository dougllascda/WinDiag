using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Virtualização/VBS/HVCI, antivírus, minifilters, serviços (SysMain, WSearch) e arquivos temporários.
/// Fontes: WMI (Win32_DeviceGuard, SecurityCenter2, Defender, Win32_Service, Win32_OptionalFeature), registro, fltmc e sistema de arquivos.
/// Minifilters e recursos opcionais costumam exigir administrador; sem ele os campos ficam "n/d" com nota.
/// </summary>
public sealed class SecurityCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "seguranca";
    /// <inheritdoc/>
    public string Title => Loc.T("Virtualização, antivírus e serviços", "Virtualization, antivirus and services");

    /// <summary>Nomes de processos dos motores de antivírus conhecidos (com sufixo "#n" opcional de instâncias).</summary>
    public static readonly Regex AvProcRegex = new(@"^(avp|avpui|MsMpEng|MpDefenderCoreService|AvastSvc|AVGSvc|ekrn|bdservicehost|vsserv|NortonSecurity|mcshield|mfemms)(#\d+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Decodifica productState do SecurityCenter2: bit 0x1000 = ativo, bit 0x10 = assinaturas desatualizadas.</summary>
    /// <param name="state">Valor bruto de productState.</param>
    public static (bool Active, bool Outdated) DecodeProductState(long state)
        => ((state & 0x1000) != 0, (state & 0x10) != 0);

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        try { Virtualization(res, facts); } catch (Exception ex) { res.Note(Loc.T("Virtualização: ", "Virtualization: ") + ex.Message); }
        try { Security(res, facts); } catch (Exception ex) { res.Note(Loc.T("Antivírus/serviços: ", "Antivirus/services: ") + ex.Message); }
        return res;
    }

    static Dictionary<string, (string State, string Mode)> Services(SectionResult res)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Wmi.Query("SELECT Name, State, StartMode FROM Win32_Service", onError: m => res.Note(Loc.T("Serviços: ", "Services: ") + m)))
        {
            var n = s.S("Name");
            if (n != null) d[n] = (s.S("State") ?? Fmt.NA, s.S("StartMode") ?? Fmt.NA);
        }
        return d;
    }

    static string StateTxt(string s) => s switch { "Running" => Loc.T("Em execução", "Running"), "Stopped" => Loc.T("Parado", "Stopped"), _ => s };
    static string StartTxt(string s) => s switch { "Auto" => Loc.T("Automático", "Automatic"), "Manual" => Loc.T("Manual", "Manual"), "Disabled" => Loc.T("Desativado", "Disabled"), "Boot" => "Boot", "System" => Loc.T("Sistema", "System"), _ => s };

    static void Virtualization(SectionResult res, SystemFacts f)
    {
        var g = res.Group(Loc.T("Virtualização e isolamento de núcleo (VBS / HVCI)", "Virtualization and core isolation (VBS / HVCI)"));
        bool hv = false;
        try { hv = Wmi.First("SELECT HypervisorPresent FROM Win32_ComputerSystem", onError: m => res.Note("Hypervisor: " + m))?.B("HypervisorPresent") ?? false; } catch { }
        var dg = Wmi.First("SELECT * FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard", m => res.Note("VBS (Win32_DeviceGuard): " + m));
        var running = new List<int>();
        try { if (dg?.P("SecurityServicesRunning") is Array a) foreach (var x in a) running.Add(Convert.ToInt32(x)); } catch { }
        var names = new Dictionary<int, string> { [1] = "Credential Guard", [2] = Loc.T("Integridade de Memória (HVCI)", "Memory Integrity (HVCI)"), [3] = "System Guard", [4] = Loc.T("Medição de firmware SMM", "SMM firmware measurement") };
        var runNames = string.Join(", ", running.Where(x => x > 0).Select(x => names.TryGetValue(x, out var n) ? n : Loc.T("serviço ", "service ") + x));
        var hvciReg = Reg.Get(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        f.HypervisorPresent = hv;
        f.HvciRunning = running.Contains(2);

        g.Add(Loc.T("Hypervisor presente", "Hypervisor present"), hv);
        var vbs = dg?.P("VirtualizationBasedSecurityStatus");
        g.Add("VBS (status)", vbs is null ? null : Loc.T($"{vbs} (0 = desligado, 1 = habilitado, 2 = em execução)", $"{vbs} (0 = off, 1 = enabled, 2 = running)"));
        g.Add(Loc.T("Serviços de segurança ativos", "Active security services"), runNames.Length > 0 ? runNames : (dg is null ? null : Loc.T("nenhum", "none")));
        g.Add(Loc.T("HVCI no registro (Enabled)", "HVCI in the registry (Enabled)"), hvciReg is null ? Loc.T("não definido", "not set") : hvciReg);

        var svc = Services(res);
        var found = new List<string>();
        foreach (var n in new[] { "vmcompute", "vmms", "HvHost", "WslService", "LxssManager", "CoworkVMService" })
            if (svc.TryGetValue(n, out var s)) found.Add($"{n}={StateTxt(s.State)}");
        g.Add(Loc.T("Serviços que usam o hypervisor", "Services that use the hypervisor"), found.Count > 0 ? string.Join(", ", found) : Loc.T("nenhum encontrado", "none found"));

        var feats = new List<string>();
        var want = new[] { "VirtualMachinePlatform", "HypervisorPlatform", "Microsoft-Hyper-V-All", "Containers-DisposableClientVM" };
        foreach (var o in Wmi.Query("SELECT Name, InstallState FROM Win32_OptionalFeature", onError: m => res.Note(Loc.T("Recursos opcionais: ", "Optional features: ") + m)))
        {
            var n = o.S("Name");
            if (n != null && want.Contains(n, StringComparer.OrdinalIgnoreCase))
                feats.Add($"{n}={(o.L("InstallState") switch { 1 => Loc.T("Habilitado", "Enabled"), 2 => Loc.T("Desabilitado", "Disabled"), 3 => Loc.T("Ausente", "Absent"), _ => Fmt.NA })}");
        }
        g.Add(Loc.T("Recursos opcionais", "Optional features"), feats.Count > 0 ? string.Join(", ", feats) : null);
        if (feats.Count == 0) res.Note(Loc.T("Recursos opcionais: não listados (pode exigir administrador)", "Optional features: not listed (may require administrator)"));

        // Donos do hypervisor
        var owners = new List<string>();
        var ownerMap = new (string Svc, string Name)[] { ("CoworkVMService", Loc.T("Claude Desktop/Cowork (provável)", "Claude Desktop/Cowork (likely)")), ("WslService", "WSL"), ("LxssManager", "WSL"), ("vmms", "Hyper-V"), ("vmcompute", "containers/Windows Sandbox") };
        foreach (var (sv, nm) in ownerMap)
            if (svc.TryGetValue(sv, out var s) && s.State == "Running" && !owners.Contains(nm)) owners.Add(nm);
        f.HypervisorOwners = owners;

        var vm = new List<(string Name, double MB)>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (!p.ProcessName.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)) continue;
                    double mb = 0; try { mb = p.WorkingSet64 / 1048576d; } catch { }
                    vm.Add((p.ProcessName, Math.Round(mb)));
                }
            }
        }
        catch (Exception ex) { res.Note(Loc.T("Processos vmmem: ", "vmmem processes: ") + ex.Message); }
        double vmMB = vm.Sum(v => v.MB);
        if (vm.Count > 0)
        {
            g.Add(Loc.T("Processos de VM", "VM processes"), string.Join(", ", vm.Select(v => $"{v.Name} {Fmt.Num(v.MB, 0)} MB")));
            if (vmMB <= 0 && !f.IsAdmin) res.Note(Loc.T("RAM de vmmem/vmmemWSL: leitura pode vir zerada sem administrador", "vmmem/vmmemWSL RAM: reading may be zero without administrator"));
        }
        g.Add(Loc.T("Quem usa o hypervisor", "Who uses the hypervisor"), owners.Count > 0 ? string.Join(", ", owners) : vm.Count > 0 ? Loc.T("VM em execução (dono não identificado pelos serviços)", "VM running (owner not identified from services)") : Loc.T("nenhum serviço de VM em execução", "no VM service running"));

        if (vm.Count > 0)
        {
            var dono = owners.Count > 0 ? Loc.T("Serviços em execução: ", "Running services: ") + string.Join(", ", owners) + ". " : "";
            var vmMBt = Fmt.Num(vmMB, 0);
            res.Find(Severity.Info, Loc.T("Máquina virtual em execução", "Virtual machine running"), Loc.T($"{vm.Count} processo(s) vmmem usando ~{vmMBt} MB de RAM", $"{vm.Count} vmmem process(es) using ~{vmMBt} MB of RAM"),
                dono + Loc.T("O processo vmmem representa a memória de uma máquina virtual (WSL, Hyper-V, Claude Desktop/Cowork, Sandbox ou container). Essa RAM fica reservada para a VM enquanto ela existir, e fechar o aplicativo dono libera essa memória.", "The vmmem process represents the memory of a virtual machine (WSL, Hyper-V, Claude Desktop/Cowork, Sandbox or container). This RAM stays reserved for the VM while it exists, and closing the owning application releases that memory."),
                Loc.T("Se não estiver usando, feche o aplicativo dono da VM antes de jogar. Este utilitário não para serviços.", "If you are not using it, close the application that owns the VM before playing. This utility does not stop services."), "", 35, id: "seguranca.vm_running");
        }

        if (f.HypervisorPresent && f.HypervisorBcdOff)
        {
            var quem = owners.Count > 0 ? Loc.T(" Depois do reinício, param de funcionar: ", " After the restart, these stop working: ") + string.Join(", ", owners) + "." : "";
            res.Find(Severity.Info, Loc.T("Hypervisor desligado, reinício pendente", "Hypervisor turned off, restart pending"), Loc.T("hypervisorlaunchtype=Off no BCD, mas o hypervisor ainda está carregado", "hypervisorlaunchtype=Off in the BCD, but the hypervisor is still loaded"),
                Loc.T("A mudança só vale no próximo boot.", "The change only takes effect on the next boot.") + quem + Loc.T(" Para voltar atrás, use a restauração de alterações.", " To undo it, use the change restore."), Loc.T("Reinicie o PC para concluir.", "Restart the PC to finish."), id: "seguranca.hypervisor_pending");
        }
        else if (f.HvciRunning)
            res.Find(Severity.Aviso, Loc.T("Integridade de Memória (HVCI) ativa", "Memory Integrity (HVCI) enabled"), Loc.T("HVCI em execução", "HVCI running"),
                Loc.T("O HVCI verifica cada driver em modo isolado pelo hypervisor. É o recurso de VBS que mais custa desempenho, principalmente em CPUs modestas, e pode aumentar engasgos. Em compensação, protege contra drivers maliciosos.", "HVCI checks each driver in a mode isolated by the hypervisor. It is the VBS feature that costs the most performance, especially on modest CPUs, and can increase stutters. In return, it protects against malicious drivers."),
                Loc.T("Se a prioridade é desempenho em jogos, desative em Segurança do Windows > Isolamento do núcleo (ou use a correção automática).", "If the priority is gaming performance, disable it in Windows Security > Core isolation (or use the automatic fix)."), "HVCI_OFF", id: "seguranca.hvci",
                simple: Loc.T("Um recurso de segurança do Windows (Integridade de Memória) protege o núcleo do sistema, mas pode custar um pouco de desempenho em jogos. Desligar reduz a proteção.", "A Windows security feature (Memory Integrity) protects the system core, but may cost a little performance in games. Turning it off reduces protection."));
        else if (hv)
            res.Find(Severity.Info, Loc.T("Hypervisor ativo, sem HVCI", "Hypervisor active, no HVCI"), Loc.T($"Hypervisor presente; VBS={(vbs is null ? "n/d" : vbs.ToString())}; Integridade de Memória (HVCI) não está em execução", $"Hypervisor present; VBS={(vbs is null ? "n/a" : vbs.ToString())}; Memory Integrity (HVCI) is not running"),
                Loc.T("O Windows está rodando sobre o hypervisor (por recursos de virtualização como Plataforma de Máquina Virtual, WSL ou apps que usam VM), mas a Integridade de Memória não está ativa. O custo medido costuma ser pequeno e varia por jogo.", "Windows is running on top of the hypervisor (through virtualization features such as Virtual Machine Platform, WSL or apps that use VMs), but Memory Integrity is not active. The measured cost is usually small and varies by game."),
                Loc.T("Só desative se precisar do último pouco de desempenho E não usar WSL, VMs ou apps que dependam do hypervisor.", "Only disable it if you need the last bit of performance AND do not use WSL, VMs or apps that depend on the hypervisor."), hv ? "HYPERVISOR_OFF" : "", id: "seguranca.hypervisor");
        else
            res.Find(Severity.Ok, Loc.T("Virtualização (VBS/hypervisor)", "Virtualization (VBS/hypervisor)"), Loc.T("Hypervisor ausente", "Hypervisor absent"), id: "seguranca.hypervisor");
    }

    static void Security(SectionResult res, SystemFacts f)
    {
        var g = res.Group(Loc.T("Antivírus", "Antivirus"));
        var third = new List<(string Name, bool On)>();
        foreach (var a in Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2", m => res.Note(Loc.T("Central de Segurança: ", "Security Center: ") + m)))
        {
            var name = a.S("displayName") ?? Fmt.NA;
            var (on, old) = DecodeProductState(a.L("productState") ?? 0);
            g.Add(name, Loc.T($"{(on ? "ATIVO" : "inativo/passivo")} | assinaturas {(old ? "desatualizadas" : "em dia")}", $"{(on ? "ACTIVE" : "inactive/passive")} | signatures {(old ? "out of date" : "up to date")}"), on ? Severity.Ok : null);
            if (!name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) third.Add((name, on));
        }
        var mp = Wmi.First("SELECT * FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender", m => res.Note("Defender (MSFT_MpComputerStatus): " + m));
        bool rt = mp?.B("RealTimeProtectionEnabled") ?? false;
        string mode = mp?.S("AMRunningMode") ?? Fmt.NA;
        if (mp != null)
        {
            g.Add(Loc.T("Defender: modo / tempo real", "Defender: mode / real-time"), $"{mode} / {(rt ? Loc.T("sim", "yes") : Loc.T("não", "no"))}");
            var thirdOn = third.Where(t => t.On).ToList();
            if (rt && thirdOn.Count > 0)
                res.Find(Severity.Atencao, Loc.T("Dois antivírus em tempo real", "Two real-time antiviruses"), Loc.T($"Defender ativo + {thirdOn[0].Name}", $"Defender active + {thirdOn[0].Name}"),
                    Loc.T("Dois motores varrendo cada arquivo em tempo real duplicam o trabalho de I/O e podem causar travadas e conflitos.", "Two engines scanning each file in real time duplicate the I/O work and can cause freezes and conflicts."),
                    Loc.T("Normalmente o Windows coloca o Defender em modo passivo quando há outro antivírus. Verifique em Segurança do Windows.", "Windows normally puts Defender in passive mode when another antivirus is present. Check in Windows Security."), id: "seguranca.two_av",
                    simple: Loc.T("Dois antivírus parecem estar vigiando o PC ao mesmo tempo, o que deixa tudo mais lento. Mantenha apenas um.", "Two antiviruses seem to be watching the PC at the same time, which slows everything down. Keep only one."));
            else if (thirdOn.Count > 0)
                res.Find(Severity.Ok, Loc.T("Antivírus em tempo real", "Real-time antivirus"), Loc.T($"Somente {thirdOn[0].Name} (Defender: {mode})", $"Only {thirdOn[0].Name} (Defender: {mode})"),
                    Loc.T("O serviço WinDefend aparece como \"Running\" mesmo em modo passivo; isso é normal e não significa varredura dupla.", "The WinDefend service appears as \"Running\" even in passive mode; this is normal and does not mean double scanning."), id: "seguranca.av_realtime");
            else if (rt)
                res.Find(Severity.Ok, Loc.T("Antivírus em tempo real", "Real-time antivirus"), "Microsoft Defender", id: "seguranca.av_realtime");
            else
            {
                var avRun = new List<string>();
                try { foreach (var p in Process.GetProcesses()) using (p) if (AvProcRegex.IsMatch(p.ProcessName) && !Regex.IsMatch(p.ProcessName, "^(MsMpEng|MpDefenderCoreService)$", RegexOptions.IgnoreCase) && !avRun.Contains(p.ProcessName)) avRun.Add(p.ProcessName); } catch { }
                if (avRun.Count > 0)
                    res.Find(Severity.Info, Loc.T("Estado do antivírus não confirmado", "Antivirus state not confirmed"), Loc.T("Central de Segurança indica inativo, mas o processo " + string.Join(", ", avRun) + " está em execução", "Security Center reports inactive, but the process " + string.Join(", ", avRun) + " is running"),
                        Loc.T("Logo após o boot o antivírus pode ainda não ter se registrado na Central de Segurança, então o estado lido pode estar desatualizado. O processo do motor está rodando, mas isso sozinho não confirma que a proteção em tempo real está ligada.", "Right after boot the antivirus may not yet have registered with the Security Center, so the state read may be outdated. The engine process is running, but that alone does not confirm that real-time protection is on."),
                        Loc.T("Confira no ícone do antivírus que a proteção está ligada; rode o relatório de novo.", "Check in the antivirus icon that protection is on; run the report again."), id: "seguranca.av_unconfirmed");
                else
                    res.Find(Severity.Atencao, Loc.T("Sem antivírus em tempo real", "No real-time antivirus"), Loc.T("Nenhum motor ativo", "No active engine"), Loc.T("O computador está sem proteção em tempo real.", "The computer has no real-time protection."), Loc.T("Ative o Defender ou um antivírus de terceiros.", "Enable Defender or a third-party antivirus."), id: "seguranca.no_av",
                        simple: Loc.T("O computador parece estar sem antivírus ativo. Ative o Microsoft Defender ou outro antivírus.", "The computer seems to have no active antivirus. Enable Microsoft Defender or another antivirus."));
            }
        }
        else res.Note(Loc.T("Estado do Defender: n/d", "Defender state: n/a"));

        // Minifilters
        var fg = res.Group(Loc.T("Minifilters de sistema de arquivos (fltmc filters)", "File system minifilters (fltmc filters)"));
        var fl = new List<FilterInfo>();
        try { fl = Filters.Parse(Proc.Run(Proc.Sys32("fltmc.exe"), "filters", 10000).StdOut); } catch { }
        if (fl.Count == 0) res.Note(Loc.T("Minifilters (fltmc filters): não listados; exige administrador", "Minifilters (fltmc filters): not listed; requires administrator"));
        else
        {
            var t = fg.Table(Loc.T("Filtros carregados", "Loaded filters"), Loc.T("Filtro", "Filter"), Loc.T("Instâncias", "Instances"), Loc.T("Altitude", "Altitude"), Loc.T("Descrição", "Description"));
            foreach (var x in fl) t.AddRow(x.Name, x.Instances, x.Altitude, Filters.Describe(x.Name));
            var avF = fl.Where(x => Filters.IsAntivirus(x.Name)).ToList();
            var vendors = avF.Select(x => Filters.Describe(x.Name)).Distinct().ToList();
            var avNames = string.Join(" + ", avF.Select(x => x.Name));
            var avTxt = Loc.T($"drivers de {vendors.Count} antivírus ({avF.Count} filtros): {avNames}", $"drivers from {vendors.Count} antiviruses ({avF.Count} filters): {avNames}");
            bool inactiveDef = mp != null && Regex.IsMatch(mode, "Passive", RegexOptions.IgnoreCase) && !rt;
            if (vendors.Count >= 2 && inactiveDef)
                res.Find(Severity.Info, Loc.T("Filtros de antivírus empilhados", "Stacked antivirus filters"), avTxt, Loc.T("Dois drivers de antivírus ficam carregados no sistema de arquivos, mas o do Defender está passivo (sem varredura em tempo real). O custo do filtro passivo é mínimo; o que pesa é o motor ativo.", "Two antivirus drivers are loaded in the file system, but the Defender one is passive (no real-time scanning). The cost of the passive filter is minimal; what weighs is the active engine."), id: "seguranca.av_filters");
            else if (vendors.Count >= 2)
                res.Find(Severity.Aviso, Loc.T("Filtros de antivírus empilhados", "Stacked antivirus filters"), avTxt, Loc.T("Mais de um driver de antivírus intercepta o I/O de arquivos. Isso soma latência em leituras e gravações.", "More than one antivirus driver intercepts file I/O. This adds latency to reads and writes."), Loc.T("Mantenha apenas um antivírus ativo.", "Keep only one antivirus active."), id: "seguranca.av_filters",
                    simple: Loc.T("Mais de um antivírus está instalado no sistema de arquivos, o que soma pequenos atrasos. Mantenha apenas um.", "More than one antivirus is installed in the file system, which adds small delays. Keep only one."));
        }

        // Serviços e temporários
        var sg = res.Group(Loc.T("Serviços e temporários", "Services and temporary files"));
        var svc = Services(res);
        foreach (var (n, set) in new (string, Action<string?>)[] { ("SysMain", v => f.SysMainStart = v), ("WSearch", v => f.WSearchStart = v) })
        {
            if (svc.TryGetValue(n, out var s)) { set(s.Mode switch { "Auto" => "Automatic", var m => m }); sg.Add(Loc.T("Serviço ", "Service ") + n, $"{StateTxt(s.State)} | {StartTxt(s.Mode)}"); }
            else sg.Add(Loc.T("Serviço ", "Service ") + n, null);
        }
        double tmp = DirMB(Path.GetTempPath()) + DirMB(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Temp"));
        tmp = Math.Round(tmp, 1);
        double wu = DirMB(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", @"SoftwareDistribution\Download"));
        f.TempMB = tmp;
        sg.Add(Loc.T("Pastas temporárias", "Temporary folders"), Loc.T($"{Fmt.Num(tmp, 1)} MB (Temp do usuário + Windows)", $"{Fmt.Num(tmp, 1)} MB (user Temp + Windows)"));
        sg.Add(Loc.T("Cache do Windows Update", "Windows Update cache"), $"{Fmt.Num(wu, 1)} MB");
        if (tmp > 500) res.Find(Severity.Aviso, Loc.T("Temporários acumulados", "Accumulated temporary files"), $"{Fmt.Num(tmp, 1)} MB", Loc.T("Arquivos temporários acumulados ocupam espaço sem utilidade.", "Accumulated temporary files take up space needlessly."), "", "TEMP", id: "seguranca.temp",
            simple: Loc.T("Há muitos arquivos temporários acumulados ocupando espaço. Limpar libera espaço sem afetar seus arquivos pessoais.", "Many temporary files have accumulated and take up space. Cleaning them frees space without affecting your personal files."));
        else res.Find(Severity.Ok, Loc.T("Temporários", "Temporary files"), $"{Fmt.Num(tmp, 1)} MB", id: "seguranca.temp");
    }

    /// <summary>Tamanho total, em MB, dos arquivos de uma pasta (recursivo, ignora pontos de reanálise e itens inacessíveis). Devolve 0 se a pasta não existir ou em falha.</summary>
    public static double DirMB(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long sum = 0;
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", opt))
                try { sum += fi.Length; } catch { }
            return Math.Round(sum / 1048576d, 1);
        }
        catch { return 0; }
    }
}
