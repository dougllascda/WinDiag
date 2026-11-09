using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Coletor da fase A: sistema operacional, placa-mãe/BIOS, energia e timers do BCD.
/// Fontes: WMI (Win32_OperatingSystem, ComputerSystem, BaseBoard, BIOS, Battery, PnPEntity), registro, powercfg e bcdedit.
/// Roda antes dos demais e preenche os fatos de plataforma em <see cref="SystemFacts"/>.
/// Os timers do BCD exigem administrador; sem ele a seção mostra "n/d" e registra uma nota. Somente leitura.
/// </summary>
public sealed class SystemCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "sistema";
    /// <inheritdoc/>
    public string Title => Loc.T("Sistema", "System");

    /// <summary>GUID do subgrupo de processador do powercfg (estado mín./máx., EPP, parking).</summary>
    private const string PwrSet = PowerCfg.SubProcessor;

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        void Err(string s) => res.Note(s);

        System.Management.ManagementBaseObject? os = null, cs = null, mb = null, bios = null;
        bool hasBattery = false;
        try { os = Wmi.First("SELECT * FROM Win32_OperatingSystem", onError: Err); } catch { }
        try { cs = Wmi.First("SELECT * FROM Win32_ComputerSystem", onError: Err); } catch { }
        try { mb = Wmi.First("SELECT * FROM Win32_BaseBoard", onError: Err); } catch { }
        try { bios = Wmi.First("SELECT * FROM Win32_BIOS", onError: Err); } catch { }
        try { hasBattery = Wmi.Query("SELECT * FROM Win32_Battery", onError: Err).Count > 0; } catch { }

        // Versão de exibição (ex.: 23H2) e UBR (revisão de atualização cumulativa) só existem no registro, não no WMI.
        const string cvPath = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? displayVersion = null; int? ubr = null;
        try { displayVersion = Reg.GetString(cvPath, "DisplayVersion"); ubr = Reg.GetInt(cvPath, "UBR"); } catch { res.Note(Loc.T("Registro CurrentVersion não lido", "CurrentVersion registry not read")); }

        // BCD (exige administrador)
        var bcd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { bcd = Bcd.Read(); } catch { }
        if (bcd.Count == 0) res.Note(Loc.T("Timers do BCD: exige administrador", "BCD timers: requires administrator"));
        // description do BCD: o AtlasOS grava seu nome/versão nela, o que permite reconhecê-lo quando há administrador.
        bcd.TryGetValue("description", out var bcdDesc);

        // Fatos
        try
        {
            facts.Bcd = bcd;
            facts.OsCaption = os?.S("Caption");
            facts.OsBuild = int.TryParse(os?.S("BuildNumber"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0;
            var kb = os?.L("TotalVisibleMemorySize");
            if (kb is > 0) facts.RamGB = Math.Round(kb.Value / 1048576d, 2);
            facts.IsLaptop = hasBattery;
        }
        catch { }

        // AtlasOS: descrição do BCD; sem admin, procura vestígios do Atlas (plano de energia / pasta de módulos)
        var planName = SafeStr(PowerCfg.ActiveSchemeName);
        bool atlasByBcd = !string.IsNullOrEmpty(bcdDesc) && bcdDesc.Contains("Atlas", StringComparison.OrdinalIgnoreCase);
        bool atlasByPlan = !string.IsNullOrEmpty(planName) && planName.Contains("Atlas", StringComparison.OrdinalIgnoreCase);
        bool atlasByDir = false;
        try { atlasByDir = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "AtlasModules")); } catch { }
        facts.IsAtlas = atlasByBcd || (bcd.Count == 0 && (atlasByPlan || atlasByDir));
        if (facts.IsAtlas)
        {
            var mv = Regex.Match(bcdDesc ?? "", @"v?\d+(\.\d+)+");
            facts.AtlasVersion = mv.Success ? mv.Value : null;
        }

        // Energia / hibernação / Fast Startup
        var fast = Reg.State(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
        var hib = Reg.State(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
        bool fastOn = fast.Exists && Convert.ToInt32(fast.Value ?? 0) == 1;
        facts.HibEnabled = hib.Exists && Convert.ToInt32(hib.Value ?? 0) == 1;

        double uptimeH = 0;
        DateTime? lastBoot = null, installed = null;
        try
        {
            lastBoot = Fmt.ParseWmiDate(os?.P("LastBootUpTime"));
            installed = Fmt.ParseWmiDate(os?.P("InstallDate"));
            if (lastBoot is not null) uptimeH = Math.Round((DateTime.Now - lastBoot.Value).TotalHours, 1);
        }
        catch { }

        // Chipset via PnP
        string? chipset = null;
        try
        {
            // O WMI não expõe o chipset diretamente; o nome do controlador LPC/eSPI no PnP traz o modelo entre parênteses (ignora siglas soltas como (R), (TM), (C)).
            var pnp = Wmi.Query("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%LPC Controller%' OR Name LIKE '%eSPI Controller%'", onError: Err);
            if (pnp.Count > 0)
            {
                var grp = Regex.Matches(pnp[0].S("Name") ?? "", @"\(([^()]+)\)")
                    .Select(m => m.Groups[1].Value.Trim())
                    .Where(g => !Regex.IsMatch(g, @"^(R|TM|C)$", RegexOptions.IgnoreCase)).ToList();
                if (grp.Count > 0) chipset = grp[^1];
            }
        }
        catch { res.Note(Loc.T("Chipset (PnP) não lido", "Chipset (PnP) not read")); }

        // BIOS
        string biosAge = "";
        DateTime? biosDate = null;
        try
        {
            biosDate = Fmt.ParseWmiDate(bios?.P("ReleaseDate"));
            if (biosDate is not null)
            {
                // Idade em meses aproximados (30,44 dias por mês).
                int mo = (int)Math.Floor((DateTime.Now - biosDate.Value).TotalDays / 30.44);
                biosAge = Loc.T($" | {mo} meses de idade", $" | {mo} months old");
            }
        }
        catch { }

        // Fatos de plataforma (usados pela Análise da plataforma)
        bool? uefi = Firmware.IsUefi();
        bool? secureBoot = uefi == true ? Firmware.IsSecureBootEnabled() : null;
        try
        {
            facts.BoardModel = mb is null ? null : $"{mb.S("Manufacturer")} {mb.S("Product")}".Trim();
            facts.Chipset = chipset;
            facts.BiosVersion = bios?.S("SMBIOSBIOSVersion");
            facts.FirmwareUefi = uefi;
            facts.SecureBoot = secureBoot;
        }
        catch { }

        // --- Grupo: Sistema operacional ---
        var g1 = res.Group(Loc.T("Sistema operacional", "Operating system"));
        g1.Add(Loc.T("Sistema", "System"), os is null ? null : $"{os.S("Caption")} ({displayVersion ?? Fmt.NA}, build {os.S("BuildNumber")}.{ubr?.ToString() ?? Fmt.NA})");
        g1.Add(Loc.T("Imagem de boot (BCD)", "Boot image (BCD)"), bcdDesc);
        g1.Add(Loc.T("Instalado em", "Installed on"), Fmt.DateTime(installed));
        g1.Add(Loc.T("Último boot", "Last boot"), lastBoot is null ? null : Loc.T($"{Fmt.DateTime(lastBoot)} ({Fmt.Num(uptimeH, 1)} h atrás)", $"{Fmt.DateTime(lastBoot)} ({Fmt.Num(uptimeH, 1)} h ago)"));
        g1.Add(Loc.T("Tipo de máquina", "Machine type"), facts.IsLaptop ? Loc.T("Notebook (bateria detectada)", "Laptop (battery detected)") : "Desktop");

        // --- Grupo: Placa-mãe e BIOS ---
        var g2 = res.Group(Loc.T("Placa-mãe e BIOS", "Motherboard and BIOS"));
        g2.Add(Loc.T("Placa-mãe", "Motherboard"), mb is null ? null : $"{mb.S("Manufacturer")} {mb.S("Product")}".Trim());
        g2.Add("Chipset", chipset);
        g2.Add("BIOS", bios is null ? null : Loc.T($"{bios.S("SMBIOSBIOSVersion")} (data: {Fmt.Date(biosDate)}){biosAge}", $"{bios.S("SMBIOSBIOSVersion")} (date: {Fmt.Date(biosDate)}){biosAge}"));
        g2.Add(Loc.T("Modo de boot", "Boot mode"), uefi is null ? null : uefi.Value ? "UEFI" : Loc.T("BIOS legado (CSM)", "Legacy BIOS (CSM)"));
        g2.Add("Secure Boot", secureBoot is null ? null : secureBoot.Value ? Loc.T("ligado", "on") : Loc.T("desligado", "off"));

        // --- Grupo: Energia ---
        long? pmin = null, pmax = null, epp = null, park = null, usbAC = null, aspmAC = null;
        try
        {
            // GUIDs de configuração do powercfg: 893dee8e = estado mínimo do processador, bc5038f7 = estado máximo, 36687f9e = EPP, 0cc5b647 = núcleos ativos mínimos (parking), 48e6b7a6 = suspensão seletiva USB, ee12f906 = ASPM do PCIe. Todos lidos no esquema ativo, em CA (tomada).
            pmin = PowerCfg.Get(PwrSet, "893dee8e-2bef-41e0-89c6-b55d0929964c");
            pmax = PowerCfg.Get(PwrSet, "bc5038f7-23e0-4960-96da-33abaf5935ec");
            epp = PowerCfg.Get(PwrSet, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
            park = PowerCfg.Get(PwrSet, "0cc5b647-c1df-4637-891a-dec35c318583");
            usbAC = PowerCfg.Get(PowerCfg.SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
            aspmAC = PowerCfg.Get(PowerCfg.SubPcie, "ee12f906-d277-404b-b6da-e5fa1a576df5");
        }
        catch { }
        facts.UsbSuspendAC = usbAC is null ? null : (int)usbAC;
        facts.AspmAC = aspmAC is null ? null : (int)aspmAC;
        if (usbAC is null || aspmAC is null) res.Note(Loc.T("powercfg: suspensão seletiva USB ou ASPM não lidos", "powercfg: USB selective suspend or ASPM not read"));

        var g3 = res.Group(Loc.T("Energia", "Power"));
        g3.Add(Loc.T("Plano de energia", "Power plan"), planName);
        g3.Add(Loc.T("Fast Startup / Hibernação", "Fast Startup / Hibernation"), Loc.T($"{(fastOn ? "ligado" : "desligado")} / {(facts.HibEnabled ? "ligada" : "desligada")}", $"{(fastOn ? "on" : "off")} / {(facts.HibEnabled ? "on" : "off")}"));
        g3.Add(Loc.T("CPU estado mín / máx (CA)", "CPU min / max state (AC)"), pmin is null && pmax is null ? null : $"{(pmin?.ToString() ?? Fmt.NA)}% / {(pmax?.ToString() ?? Fmt.NA)}%");
        g3.Add(Loc.T("EPP (CA)", "EPP (AC)"), epp is null ? null : epp == 0 ? Loc.T("0 (desempenho máximo)", "0 (maximum performance)") : epp >= 80 ? Loc.T($"{epp} (prioriza economia)", $"{epp} (favors power saving)") : Loc.T($"{epp} (0=desempenho, 100=economia)", $"{epp} (0=performance, 100=power saving)"));
        g3.Add(Loc.T("Núcleos ativos mín (CA)", "Min active cores (AC)"), park is null ? null : park >= 100 ? Loc.T("100% (parking desligado)", "100% (parking off)") : Loc.T($"{park}% (parking ativo)", $"{park}% (parking active)"));
        g3.Add(Loc.T("Suspensão seletiva USB (CA)", "USB selective suspend (AC)"), usbAC is null ? null : usbAC == 0 ? Loc.T("0 (desativada)", "0 (disabled)") : Loc.T($"{usbAC} (ativada)", $"{usbAC} (enabled)"));
        g3.Add(Loc.T("ASPM do PCIe (CA)", "PCIe ASPM (AC)"), aspmAC is null ? null : aspmAC == 0 ? Loc.T("0 (desligado)", "0 (off)") : Loc.T($"{aspmAC} (economia de energia no link PCIe)", $"{aspmAC} (power saving on the PCIe link)"));

        // --- Grupo: Timers (BCD) ---
        var g4 = res.Group(Loc.T("Timers do sistema (BCD)", "System timers (BCD)"));
        if (bcd.Count == 0)
        {
            g4.Add("BCD", Loc.T("n/d (exige administrador)", "n/a (requires administrator)"));
        }
        else
        {
            foreach (var k in new[] { "useplatformclock", "useplatformtick", "disabledynamictick", "tscsyncpolicy", "hypervisorlaunchtype", "x2apicpolicy", "nx" })
                g4.Add(k, bcd.TryGetValue(k, out var v) ? v : Loc.T("não definido (padrão do Windows)", "not set (Windows default)"));
        }

        // --- Verificações ---
        try { Checks(res, facts, bcd, bcdDesc, pmax, usbAC, aspmAC, fastOn, uptimeH); }
        catch (Exception ex) { res.Note(Loc.T("Verificações de sistema: ", "System checks: ") + ex.Message); }
        return res;
    }

    /// <summary>Achados do coletor: AtlasOS, limite máximo do processador (abaixo de 100%), Fast Startup com uptime acima de 48 h, suspensão seletiva USB, ASPM e timers do BCD (HPET forçado, ajustes de tick). Só informa; correções ficam com o FixEngine.</summary>
    private static void Checks(SectionResult res, SystemFacts facts, Dictionary<string, string> bcd, string? bcdDesc,
        long? pmax, long? usbAC, long? aspmAC, bool fastOn, double uptimeH)
    {
        if (facts.IsAtlas)
        {
            var off = new List<string>();
            foreach (var (svc, nome) in new[] { ("SysMain", "SysMain"), ("WSearch", "Windows Search") })
            {
                var start = Reg.GetInt($@"HKLM:\SYSTEM\CurrentControlSet\Services\{svc}", "Start");
                if (start is null) res.Note(Loc.T($"Serviço {svc} não lido - texto do achado AtlasOS sem essa informação", $"Service {svc} not read - AtlasOS finding text lacks this information"));
                else if (start == 4) off.Add(nome);
            }
            var extra = off.Count > 0 ? Loc.T($" Isso explica itens como {string.Join(" e ", off)} desativado(s).", $" This explains items such as {string.Join(" and ", off)} being disabled.") : "";
            res.Find(Severity.Info, Loc.T("AtlasOS detectado", "AtlasOS detected"), string.IsNullOrEmpty(bcdDesc) ? Loc.T("AtlasOS (detectado por plano de energia/pasta de módulos)", "AtlasOS (detected by power plan/modules folder)") : bcdDesc,
                Loc.T("O AtlasOS já aplica ajustes por conta própria (serviços, plano de energia, telemetria).", "AtlasOS already applies tweaks on its own (services, power plan, telemetry).") + extra +
                Loc.T(" Reaplicar ajustes por cima pode conflitar com o que o Atlas configura; prefira mexer só no que este relatório apontar com evidência.", " Reapplying tweaks on top may conflict with what Atlas configures; prefer changing only what this report points out with evidence."), id: "sistema.atlasos");
        }

        if (pmax is not null && pmax < 100)
            res.Find(Severity.Aviso, Loc.T("Limite máximo do processador", "Processor maximum limit"), Loc.T($"Plano limita a CPU a {pmax}%", $"Plan limits the CPU to {pmax}%"),
                Loc.T("O plano de energia impede a CPU de usar todo o clock/turbo. Isso reduz desempenho em jogos.", "The power plan prevents the CPU from using its full clock/turbo. This reduces performance in games."),
                Loc.T("Ajuste \"Estado máximo do processador\" para 100% nas opções de energia avançadas.", "Set \"Maximum processor state\" to 100% in the advanced power options."),
                id: "sistema.cpu_max_limited",
                simple: Loc.T("O plano de energia do Windows está impedindo o processador de chegar à velocidade máxima. Ajustar para 100% devolve o desempenho.", "The Windows power plan is preventing the processor from reaching its maximum speed. Setting it to 100% restores performance."));
        else if (pmax is not null)
            res.Find(Severity.Ok, Loc.T("Limite máximo do processador", "Processor maximum limit"), Loc.T($"{pmax}% (sem limitação)", $"{pmax}% (no limitation)"), id: "sistema.cpu_max_limit");

        // A correção HIBER desliga hibernação e Inicialização Rápida juntas; em notebook não é oferecida (quem depende de hibernar perderia o recurso).
        string hiberFix = facts.IsLaptop ? "" : "HIBER";
        // 48 h: limiar prático do WinDiag para considerar o uptime longo com Fast Startup (o kernel não é reiniciado por "Desligar").
        if (fastOn && uptimeH > 48)
            res.Find(Severity.Aviso, Loc.T("Fast Startup com uptime longo", "Fast Startup with long uptime"), Loc.T($"{Fmt.Num(uptimeH, 1)} h sem reinício real", $"{Fmt.Num(uptimeH, 1)} h without a real restart"),
                Loc.T("Com Fast Startup, \"Desligar\" não reinicia o kernel; drivers e serviços acumulam estado por dias. Isso pode causar degradação gradual.", "With Fast Startup, \"Shut down\" does not restart the kernel; drivers and services accumulate state for days. This can cause gradual degradation."),
                facts.IsLaptop
                    ? Loc.T("Use \"Reiniciar\" em vez de \"Desligar\". Em notebook o WinDiag não oferece correção automática, pois ela também desligaria a hibernação.", "Use \"Restart\" instead of \"Shut down\". On a laptop WinDiag does not offer an automatic fix, since it would also turn off hibernation.")
                    : Loc.T("Use \"Reiniciar\" em vez de \"Desligar\", ou aplique a correção: ela desliga a hibernação e a Inicialização Rápida juntas.", "Use \"Restart\" instead of \"Shut down\", or apply the fix: it turns off hibernation and Fast Startup together."), hiberFix,
                id: "sistema.fast_startup",
                simple: Loc.T("O PC está ligado há dias sem reiniciar de verdade, porque \"Desligar\" não reinicia tudo. Use \"Reiniciar\" de vez em quando para manter o Windows estável.", "The PC has been on for days without a real restart, because \"Shut down\" does not restart everything. Use \"Restart\" now and then to keep Windows stable."));
        else if (fastOn)
            res.Find(Severity.Info, Loc.T("Fast Startup ligado", "Fast Startup on"), Loc.T("Desligar não reinicia o kernel por completo", "Shut down does not fully restart the kernel"),
                facts.IsLaptop
                    ? Loc.T("Use Reiniciar quando quiser um boot realmente limpo.", "Use Restart when you want a truly clean boot.")
                    : Loc.T("Use Reiniciar quando quiser um boot realmente limpo. A correção disponível desliga hibernação e Inicialização Rápida juntas.", "Use Restart when you want a truly clean boot. The available fix turns off hibernation and Fast Startup together."), "", hiberFix, id: "sistema.fast_startup");
        else
            res.Find(Severity.Ok, "Fast Startup", Loc.T("Desligado (boot sempre limpo)", "Off (boot is always clean)"), id: "sistema.fast_startup");

        if (usbAC is not null)
        {
            if (usbAC == 1)
                res.Find(Severity.Info, Loc.T("Suspensão seletiva USB ativa", "USB selective suspend enabled"), Loc.T("Plano em CA: ativada", "Plan on AC: enabled"),
                    Loc.T("O Windows pode suspender portas USB ociosas e reativá-las sob demanda. Em alguns periféricos (mouse, teclado, headset) isso causa atraso ou desconexões momentâneas.", "Windows can suspend idle USB ports and reactivate them on demand. On some peripherals (mouse, keyboard, headset) this causes delays or momentary disconnections."),
                    Loc.T("Desative a suspensão seletiva USB no plano de energia, se notar falhas em periféricos.", "Disable USB selective suspend in the power plan if you notice peripheral glitches."), "USB_SUSPEND_OFF", 20, id: "sistema.usb_suspend",
                    simple: Loc.T("O Windows pode desligar portas USB paradas para economizar energia, e isso às vezes faz mouse ou teclado falhar. Desligar a função costuma ser inofensivo.", "Windows may turn off idle USB ports to save power, and this sometimes makes the mouse or keyboard fail. Turning the feature off is usually harmless."));
            else
                res.Find(Severity.Ok, Loc.T("Suspensão seletiva USB", "USB selective suspend"), Loc.T("Desativada (CA)", "Disabled (AC)"), id: "sistema.usb_suspend");
        }
        if (aspmAC is not null)
        {
            if (aspmAC > 0)
                res.Find(Severity.Info, Loc.T("ASPM do PCIe ativo", "PCIe ASPM enabled"), Loc.T($"Plano em CA: nível {aspmAC}", $"Plan on AC: level {aspmAC}"),
                    Loc.T("O gerenciamento de energia do link PCIe pode adicionar latência ao sair do estado de economia (GPU, NVMe). O efeito costuma ser pequeno e depende do hardware.", "PCIe link power management can add latency when leaving the power-saving state (GPU, NVMe). The effect is usually small and depends on the hardware."),
                    Loc.T("Defina \"Gerenciamento de estado do link\" como Desligado no plano de energia, se quiser eliminar essa variável.", "Set \"Link State Power Management\" to Off in the power plan if you want to eliminate this variable."), "ASPM_OFF", 25, id: "sistema.aspm",
                    simple: Loc.T("O Windows reduz a energia da conexão da placa de vídeo e do SSD quando ociosos, o que pode somar um pequeno atraso. O efeito costuma ser pequeno.", "Windows reduces power to the graphics card and SSD connection when idle, which may add a small delay. The effect is usually small."));
            else
                res.Find(Severity.Ok, Loc.T("ASPM do PCIe", "PCIe ASPM"), Loc.T("Desligado (CA)", "Off (AC)"), id: "sistema.aspm");
        }

        // Timers (Audit-Timers)
        if (bcd.Count == 0) return;
        bcd.TryGetValue("useplatformclock", out var upc);
        if (upc is not null && Regex.IsMatch(upc, @"^(yes|true|on)$", RegexOptions.IgnoreCase))
            res.Find(Severity.Aviso, Loc.T("HPET forçado", "HPET forced"), "useplatformclock = Yes",
                Loc.T("Forçar o HPET faz cada leitura de tempo custar mais que no TSC padrão, o que aumenta a latência em jogos.", "Forcing HPET makes each time read cost more than with the default TSC, which increases latency in games."),
                Loc.T("Remova o valor (correção automática).", "Remove the value (automatic fix)."), "HPET_OFF", id: "sistema.hpet",
                simple: Loc.T("Uma configuração antiga força o Windows a usar um relógio mais lento, o que pode aumentar a latência em jogos. Dá para remover com a correção automática.", "An old setting forces Windows to use a slower clock, which can increase latency in games. It can be removed with the automatic fix."));
        else
            res.Find(Severity.Ok, Loc.T("Relógio do sistema", "System clock"), Loc.T("Padrão do Windows (sem HPET forçado)", "Windows default (no forced HPET)"), id: "sistema.hpet");

        if (bcd.ContainsKey("disabledynamictick") || bcd.ContainsKey("useplatformtick"))
        {
            bcd.TryGetValue("disabledynamictick", out var ddt);
            bcd.TryGetValue("useplatformtick", out var upt);
            res.Find(Severity.Info, Loc.T("Ajustes de tick", "Tick settings"), $"disabledynamictick={ddt ?? Fmt.NA}, useplatformtick={upt ?? Fmt.NA}",
                Loc.T("Esses ajustes circulam como dicas de desempenho, mas os resultados em testes são inconsistentes e podem aumentar consumo. Este utilitário só informa, não altera.", "These settings circulate as performance tips, but test results are inconsistent and they may increase power consumption. This utility only reports and does not change anything."), id: "sistema.tick");
        }
    }

    /// <summary>Executa a leitura e devolve null em vez de propagar exceção.</summary>
    private static string? SafeStr(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }
}
