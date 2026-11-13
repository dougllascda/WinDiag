using Microsoft.Win32;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Fixes;

/// <summary>Catálogo de otimizações: cada item traz o que alterar, a regra de aplicabilidade e o teste de "já em vigor".</summary>
public static class FixCatalog
{
    /// <summary>Chave de preferências gráficas do DirectX do usuário.</summary>
    public const string DxPrefPath = @"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences";
    /// <summary>Valor com as opções gráficas do DirectX (texto "Chave=Valor;").</summary>
    public const string DxPrefName = "DirectXUserGlobalSettings";
    /// <summary>Chave do registro que controla a Integridade de Memória (HVCI).</summary>
    public const string HvciPath = @"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    /// <summary>GUID do subgrupo de energia "USB settings" (powercfg).</summary>
    private const string UsbSub = "2a737441-1930-4402-8d77-b2bebba308a3";
    /// <summary>GUID da configuração "USB selective suspend" dentro do subgrupo USB.</summary>
    private const string UsbSet = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    /// <summary>GUID do subgrupo de energia "PCI Express".</summary>
    private const string PcieSub = "501a4d13-42af-4429-9fd1-a8218c268e20";
    /// <summary>GUID da configuração "Link State Power Management" (ASPM) dentro do subgrupo PCI Express.</summary>
    private const string AspmSet = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>true se o tipo de início de um serviço é "Disabled" (comparação sem diferenciar maiúsculas); null conta como não desativado.</summary>
    /// <param name="startType">Tipo de início lido do serviço.</param>
    private static bool Disabled(string? startType) => string.Equals(startType, "Disabled", StringComparison.OrdinalIgnoreCase);
    /// <summary>true se o BCD força o timer HPET (<c>useplatformclock</c> = yes/true/on/sim). Só nesse caso a otimização que remove o valor faz sentido.</summary>
    /// <param name="f">Fatos coletados (usa o BCD lido).</param>
    private static bool HpetForced(SystemFacts f) =>
        f.Bcd.TryGetValue("useplatformclock", out var v) && System.Text.RegularExpressions.Regex.IsMatch(v, "^(yes|true|on|sim)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Todos os itens do catálogo, na ordem de exibição.</summary>
    public static IReadOnlyList<FixDefinition> All { get; } = new List<FixDefinition>
    {
        new() { Id = "GAMEDVR", Scope = Scope.Gaming,
            NameText = ("Desativar captura em segundo plano do Xbox Game DVR", "Turn off Xbox Game DVR background capture"),
            ShortNameText = ("Game DVR desativado", "Game DVR off"),
            Reg = { new(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
                    new(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord) },
            ExplainText = ("O Game DVR grava o jogo em segundo plano e pode consumir GPU e disco. Se você usa a Barra de Jogo para gravar clipes, não aplique.",
                "Game DVR records the game in the background and can use GPU and disk. If you use the Game Bar to record clips, do not apply this."),
            Applicable = f => f.GameDVR != 0, IsActive = f => f.GameDVR == 0 },

        new() { Id = "HAGS", Scope = Scope.Gaming,
            NameText = ("Habilitar agendamento de GPU acelerado por hardware (HAGS)", "Enable hardware-accelerated GPU scheduling (HAGS)"),
            ShortNameText = ("HAGS ativado", "HAGS on"), Reboot = true,
            Reg = { new(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord) },
            ExplainText = ("Passa parte do agendamento de quadros da CPU para a GPU. Pode reduzir a carga de CPU em alguns jogos, mas o ganho não é garantido e em alguns drivers ou jogos piora. Teste e compare.",
                "Moves part of frame scheduling from the CPU to the GPU. It can lower CPU load in some games, but the gain is not guaranteed and with some drivers or games it makes things worse. Test and compare."),
            Applicable = f => f.HAGS != 2 && f.GpuModern, IsActive = f => f.HAGS == 2 },

        new() { Id = "MMC",
            NameText = ("Desativar compressão de memória (MMAgent)", "Turn off memory compression (MMAgent)"),
            ShortNameText = ("Compressão de memória desligada", "Memory compression off"), Reboot = true, Mmc = true,
            ExplainText = ("Com bastante RAM (32 GB ou mais) e boa folga livre, a compressão gasta ciclos de CPU sem necessidade. Benefício pequeno; só vale em CPUs mais modestas.",
                "With plenty of RAM (32 GB or more) and good free headroom, compression spends CPU cycles unnecessarily. Small benefit; only worthwhile on more modest CPUs."),
            Applicable = f => f.MemCompression == true && f.RamForThresholdGB >= 32 && f.AvailPct >= 40,
            IsActive = f => f.MemCompression == false && f.RamForThresholdGB >= 32 },

        new() { Id = "MMC_ON",
            NameText = ("Ligar compressão de memória (MMAgent)", "Turn on memory compression (MMAgent)"),
            ShortNameText = ("Compressão de memória ligada", "Memory compression on"), Reboot = true, Mmc = true, MmcOn = true,
            ExplainText = ("Com pouca RAM livre, a compressão mantém mais dados na memória em vez de paginar no disco, o que costuma causar menos engasgos do que ler e gravar o arquivo de paginação. Custa um pouco de CPU.",
                "With little free RAM, compression keeps more data in memory instead of paging to disk, which usually causes fewer stutters than reading and writing the page file. It costs some CPU."),
            Applicable = f => f.MemCompression == false && (f.RamForThresholdGB <= 16 || f.AvailGB < 3 || f.AvailPct < 20),
            IsActive = f => f.MemCompression == true && f.RamForThresholdGB < 32 },

        new() { Id = "WINDOWED_OPT", Scope = Scope.Gaming,
            NameText = ("Ativar otimizações para jogos em janela", "Enable optimizations for windowed games"),
            ShortNameText = ("Otimizações para jogos em janela", "Windowed game optimizations"), DxString = true,
            ExplainText = ("Define SwapEffectUpgradeEnable=1 nas preferências gráficas do DirectX do usuário, o que permite ao Windows apresentar jogos em janela ou sem borda com menos latência. As demais opções dessa configuração são preservadas.",
                "Sets SwapEffectUpgradeEnable=1 in the user's DirectX graphics preferences, which lets Windows present windowed or borderless games with less latency. The other options in that setting are preserved."),
            Applicable = f => f.WindowedOpt != "1", IsActive = f => f.WindowedOpt == "1" },

        new() { Id = "USB_SUSPEND_OFF", Scope = Scope.Gaming,
            NameText = ("Desativar suspensão seletiva de USB (na tomada)", "Turn off USB selective suspend (plugged in)"),
            ShortNameText = ("Suspensão seletiva de USB desligada", "USB selective suspend off"),
            Power = { new(UsbSub, UsbSet, 0) },
            ExplainText = ("Impede o Windows de suspender portas USB ociosas. Evita a latência de reativar mouse, teclado e headset, ao custo de um pequeno aumento de consumo.",
                "Stops Windows from suspending idle USB ports. Avoids the latency of waking the mouse, keyboard and headset, at the cost of slightly higher power use."),
            Applicable = f => f.UsbSuspendAC == 1, IsActive = f => f.UsbSuspendAC == 0 },

        new() { Id = "ASPM_OFF", Scope = Scope.Gaming,
            NameText = ("Desativar economia de energia do PCIe (ASPM, na tomada)", "Turn off PCIe power saving (ASPM, plugged in)"),
            ShortNameText = ("ASPM do PCIe desligado", "PCIe ASPM off"),
            Power = { new(PcieSub, AspmSet, 0) },
            ExplainText = ("Mantém os links PCIe (GPU, NVMe) sempre ativos, evitando a latência de acordar o link. Aumenta um pouco o consumo e o calor em ociosidade.",
                "Keeps PCIe links (GPU, NVMe) always active, avoiding the latency of waking the link. Slightly increases power use and heat at idle."),
            Applicable = f => f.AspmAC > 0, IsActive = f => f.AspmAC == 0 },

        new() { Id = "GAMEMODE_ON", Scope = Scope.Gaming,
            NameText = ("Ligar o Modo de Jogo do Windows", "Turn on Windows Game Mode"),
            ShortNameText = ("Modo de Jogo ligado", "Game Mode on"),
            Reg = { new(@"HKCU:\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord) },
            ExplainText = ("O Modo de Jogo prioriza o jogo em primeiro plano e reduz a interferência de tarefas do Windows (ex.: Windows Update). O efeito costuma ser pequeno.",
                "Game Mode prioritizes the foreground game and reduces interference from Windows tasks (e.g. Windows Update). The effect is usually small."),
            Applicable = f => f.GameModeOff, IsActive = f => !f.GameModeOff },

        new() { Id = "STARTUP_APPS",
            NameText = ("Desativar apps na inicialização (você escolhe quais)", "Turn off startup apps (you choose which)"),
            ShortNameText = ("Apps desativados na inicialização", "Startup apps turned off"), StartupApps = true,
            ExplainText = ("Os programas marcados deixam de iniciar com o Windows (mesmo efeito do Gerenciador de Tarefas > Aplicativos de inicialização). Libera RAM e CPU logo após o boot. Nada é desinstalado.",
                "The selected programs no longer start with Windows (same effect as Task Manager > Startup apps). Frees RAM and CPU right after boot. Nothing is uninstalled."),
            Applicable = f => f.StartupItems.Any(i => i.Enabled && i.ApprovedPath is not null && i.ValueName is not null) },

        new() { Id = "SYSMAIN",
            NameText = ("Desativar SysMain (Superfetch) com sistema em SSD", "Turn off SysMain (Superfetch) with the system on an SSD"),
            ShortNameText = ("SysMain desativado", "SysMain off"), Svc = { "SysMain" },
            ExplainText = ("Em SSD o pré-carregamento adianta pouco. A Microsoft mantém o serviço ligado por padrão; com o sistema em HDD, deixe ligado. Com jogos instalados em HDD (biblioteca Steam em HDD) ele ajuda a pré-carregar e deve ficar ligado.",
                "On an SSD, preloading helps little. Microsoft keeps the service on by default; with the system on an HDD, leave it on. With games installed on an HDD (Steam library on an HDD) it helps preload and should stay on."),
            Applicable = f => f.IsSystemSSD && !f.SteamOnHdd && !Disabled(f.SysMainStart), IsActive = f => Disabled(f.SysMainStart) },

        new() { Id = "WSEARCH",
            NameText = ("Desativar indexação de busca (Windows Search)", "Turn off search indexing (Windows Search)"),
            ShortNameText = ("Indexação de busca desativada", "Search indexing off"), Risk = FixRisk.Medio, Svc = { "WSearch" },
            ExplainText = ("Reduz I/O em segundo plano, mas a busca do Explorador e do Outlook fica lenta ou incompleta.",
                "Reduces background I/O, but File Explorer and Outlook search become slow or incomplete."),
            Applicable = f => f.WSearchStart is not null && !Disabled(f.WSearchStart), IsActive = f => Disabled(f.WSearchStart) },

        new() { Id = "HIBER",
            NameText = ("Desativar hibernação e Inicialização Rápida (desktop)", "Turn off hibernation and Fast Startup (desktop)"),
            ShortNameText = ("Hibernação desligada", "Hibernation off"), Hib = true,
            ExplainText = ("Desligar passa a reiniciar o kernel de verdade (boot limpo) e libera o hiberfil.sys. Não use em notebook se você depende de hibernar.",
                "Shutting down then restarts the kernel for real (clean boot) and frees hiberfil.sys. Do not use on a laptop if you rely on hibernating."),
            Applicable = f => f.HibEnabled && !f.IsLaptop, IsActive = f => !f.HibEnabled && !f.IsLaptop },

        new() { Id = "HVCI_OFF", Scope = Scope.Gaming,
            NameText = ("Desativar Integridade de Memória (HVCI)", "Turn off Memory Integrity (HVCI)"),
            ShortNameText = ("Integridade de Memória desligada", "Memory Integrity off"), Risk = FixRisk.Medio, Reboot = true,
            Reg = { new(HvciPath, "Enabled", 0, RegistryValueKind.DWord) },
            ExplainText = ("O HVCI é o recurso de VBS que mais custa desempenho. Desligar reduz a proteção contra drivers maliciosos. O caminho oficial é Segurança do Windows > Isolamento de núcleo.",
                "HVCI is the VBS feature that costs the most performance. Turning it off reduces protection against malicious drivers. The official path is Windows Security > Core isolation."),
            Applicable = f => f.HvciRunning && Reg.GetInt(HvciPath, "Enabled") != 0,
            IsActive = f => Reg.GetInt(HvciPath, "Enabled") == 0 },

        new() { Id = "HYPERVISOR_OFF", Scope = Scope.Gaming,
            NameText = ("Desativar o hypervisor do Windows (bcdedit hypervisorlaunchtype off)", "Turn off the Windows hypervisor (bcdedit hypervisorlaunchtype off)"),
            ShortNameText = ("Hypervisor desligado", "Hypervisor off"),
            Risk = FixRisk.Alto, Reboot = true,
            Bcd = { new("hypervisorlaunchtype", "off") },
            ExplainText = ("Remove a camada de virtualização sob o Windows. QUEBRA tudo o que depende dela: WSL2, Hyper-V, Windows Sandbox, máquinas virtuais e apps ou serviços que usam VM (ex.: WSL2, Docker Desktop). O ganho de desempenho costuma ser pequeno.",
                "Removes the virtualization layer under Windows. It BREAKS everything that depends on it: WSL2, Hyper-V, Windows Sandbox, virtual machines and apps or services that use a VM (e.g. WSL2, Docker Desktop). The performance gain is usually small."),
            Applicable = f => f.HypervisorPresent && !f.HypervisorBcdOff, IsActive = f => f.HypervisorBcdOff },

        new() { Id = "HPET_OFF", Scope = Scope.Gaming,
            NameText = ("Remover HPET forçado (bcdedit useplatformclock)", "Remove forced HPET (bcdedit useplatformclock)"),
            ShortNameText = ("HPET no padrão do Windows", "HPET at the Windows default"), Reboot = true,
            Bcd = { new("useplatformclock", null) },
            ExplainText = ("Forçar o HPET como relógio do sistema aumenta a latência de leitura de tempo. O padrão do Windows (TSC) é melhor na maioria dos PCs.",
                "Forcing HPET as the system clock increases the latency of reading the time. The Windows default (TSC) is better on most PCs."),
            Applicable = HpetForced, IsActive = f => f.Bcd.Count > 0 && !HpetForced(f) },

        new() { Id = "MPO_OFF", Scope = Scope.Gaming,
            NameText = ("[EXPERIMENTAL] Desativar Multiplane Overlay (MPO) no DWM", "[EXPERIMENTAL] Turn off Multiplane Overlay (MPO) in DWM"),
            ShortNameText = ("MPO desativado", "MPO off"), Reboot = true,
            Reg = { new(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord) },
            ExplainText = ("O MPO é um modo de composição de vídeo que, em alguns setups com vários monitores ou taxas de atualização diferentes, está ligado a flicker e engasgos. NÃO é causa comprovada: aplique como teste A/B e desfaça se não ajudar.",
                "MPO is a video composition mode that, on some setups with multiple monitors or different refresh rates, has been linked to flicker and stutter. It is NOT a proven cause: apply it as an A/B test and undo it if it does not help."),
            Applicable = f => f.MPO != 5 && f.DisplayCount >= 2, IsActive = f => f.MPO == 5 },

        new() { Id = "POWER_PLAN_WORK", Scope = Scope.Work,
            NameText = ("Usar o plano de energia recomendado ao trabalho (Equilibrado no notebook, Alto desempenho no desktop)", "Use the power plan recommended for work (Balanced on a laptop, High performance on a desktop)"),
            ShortNameText = ("Plano de energia recomendado", "Recommended power plan"), PowerPlan = true,
            ExplainText = ("Troca o plano de energia ativo, só quando o atual destoa do uso de escritório: em notebook, de Alto desempenho/Desempenho máximo para Equilibrado; em desktop, de Economia de energia para Alto desempenho (Equilibrado e Alto desempenho já são aceitáveis). O plano alvo precisa existir em powercfg /list. O GUID do plano atual fica salvo no snapshot e o desfazer volta a ele. Se o plano recomendado não existir neste PC (alguns notebooks o escondem), a troca falha sem alterar nada.",
                "Switches the active power plan only when the current one does not suit office use: on a laptop, from High/Ultimate performance to Balanced; on a desktop, from Power saver to High performance (Balanced and High performance are already acceptable). The target plan must exist in powercfg /list. The current plan's GUID is saved in the snapshot and undo returns to it. If the recommended plan does not exist on this PC (some laptops hide it), the switch fails without changing anything."),
            Applicable = PowerPlanWorkApplicable, IsActive = PowerPlanWorkActive },

        new() { Id = "POWER_PLAN_GAMING", Scope = Scope.Gaming,
            NameText = ("Usar o plano de energia Alto desempenho (desktop)", "Use the High performance power plan (desktop)"),
            ShortNameText = ("Plano Alto desempenho", "High performance plan"), PowerPlan = true, PowerPlanTarget = HighPerformancePlanGuid,
            ExplainText = ("Só em desktop: se o plano atual for Equilibrado ou Economia de energia e o Alto desempenho existir em powercfg /list, troca para o Alto desempenho, que deixa o processador menos propenso a baixar o clock. O ganho costuma ser pequeno e não é garantido; o consumo em ociosidade sobe um pouco. O GUID do plano atual fica salvo no snapshot e o desfazer volta a ele. Se o plano não existir neste PC, a troca falha sem alterar nada.",
                "Desktop only: if the current plan is Balanced or Power saver and High performance exists in powercfg /list, switches to High performance, which makes the processor less likely to lower its clock. The gain is usually small and not guaranteed; idle power use goes up a little. The current plan's GUID is saved in the snapshot and undo returns to it. If the plan does not exist on this PC, the switch fails without changing anything."),
            Applicable = PowerPlanGamingApplicable, IsActive = PowerPlanGamingActive },

        new() { Id = "MOUSE_ACCEL_OFF", Scope = Scope.Gaming, MouseApply = true,
            NameText = ("Desativar a aceleração do ponteiro do mouse", "Turn off mouse pointer acceleration"),
            ShortNameText = ("Aceleração do mouse desligada", "Mouse acceleration off"),
            Reg = { new(MouseApi.MousePath, "MouseSpeed", "0", RegistryValueKind.String),
                    new(MouseApi.MousePath, "MouseThreshold1", "0", RegistryValueKind.String),
                    new(MouseApi.MousePath, "MouseThreshold2", "0", RegistryValueKind.String) },
            ExplainText = ("Desliga \"Aprimorar a precisão do ponteiro\": o ponteiro passa a andar sempre a mesma distância para o mesmo movimento da mão, o que muita gente prefere em jogos de mira. É questão de preferência, não de FPS. Vale na hora e o desfazer restaura os valores anteriores.",
                "Turns off \"Enhance pointer precision\": the pointer always moves the same distance for the same hand movement, which many people prefer in aiming games. It is a matter of preference, not FPS. It takes effect immediately and undo restores the previous values."),
            Applicable = f => !MouseAccelOff(), IsActive = f => MouseAccelOff() },

        new() { Id = "STICKY_KEYS_OFF", Scope = Scope.Gaming, AccessibilityApply = true,
            NameText = ("Desativar os atalhos de Teclas de Aderência, Filtro e Alternância", "Turn off the Sticky Keys, Filter Keys and Toggle Keys shortcuts"),
            ShortNameText = ("Atalhos de acessibilidade do teclado desligados", "Keyboard accessibility shortcuts off"),
            Reg = { new(StickyPath, "Flags", "506", RegistryValueKind.String),
                    new(FilterPath, "Flags", "122", RegistryValueKind.String),
                    new(TogglePath, "Flags", "58", RegistryValueKind.String) },
            ExplainText = ("Impede que apertar Shift cinco vezes, segurar Shift por 8 segundos ou segurar Num Lock por 5 segundos abra o aviso de acessibilidade no meio do jogo. Os recursos continuam disponíveis em Configurações > Acessibilidade; só o atalho do teclado deixa de abrir o aviso. Se você depende deles, não aplique.",
                "Stops pressing Shift five times, holding Shift for 8 seconds or holding Num Lock for 5 seconds from opening the accessibility prompt in the middle of a game. The features remain available in Settings > Accessibility; only the keyboard shortcut stops opening the prompt. If you rely on them, do not apply this."),
            Applicable = f => !StickyOff(), IsActive = f => StickyOff() },

        new() { Id = "PRINTER_DEFAULT_LOCK", Scope = Scope.Work,
            NameText = ("Impedir que o Windows troque a impressora padrão sozinho", "Stop Windows from changing the default printer by itself"),
            ShortNameText = ("Impressora padrão fixa", "Default printer locked"),
            Reg = { new(@"HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord) },
            ExplainText = ("Por padrão o Windows define como padrão a última impressora usada, o que faz documentos saírem na impressora errada em quem usa mais de uma. Com este ajuste a impressora padrão só muda quando você escolher. Vale para o seu usuário e o desfazer restaura o valor anterior.",
                "By default Windows sets the last printer used as the default, which sends documents to the wrong printer for people who use more than one. With this setting the default printer only changes when you choose. It applies to your user and undo restores the previous value."),
            Applicable = f => f.Printers.Count >= 2 && Reg.GetInt(PrinterModePath, "LegacyDefaultPrinterMode") != 1,
            IsActive = f => Reg.GetInt(PrinterModePath, "LegacyDefaultPrinterMode") == 1 },
    };

    /// <summary>Chave do registro com o parâmetro que fixa a impressora padrão do usuário.</summary>
    private const string PrinterModePath = @"HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows";
    /// <summary>Chave do registro das Teclas de Aderência.</summary>
    private const string StickyPath = @"HKCU:\Control Panel\Accessibility\StickyKeys";
    /// <summary>Chave do registro das Teclas de Filtro.</summary>
    private const string FilterPath = @"HKCU:\Control Panel\Accessibility\Keyboard Response";
    /// <summary>Chave do registro das Teclas de Alternância.</summary>
    private const string TogglePath = @"HKCU:\Control Panel\Accessibility\ToggleKeys";

    /// <summary>true se a aceleração do mouse está desligada no registro (MouseSpeed, MouseThreshold1 e MouseThreshold2 iguais a "0").</summary>
    private static bool MouseAccelOff() =>
        Reg.GetString(MouseApi.MousePath, "MouseSpeed") == "0" && Reg.GetString(MouseApi.MousePath, "MouseThreshold1") == "0" && Reg.GetString(MouseApi.MousePath, "MouseThreshold2") == "0";

    /// <summary>true se os três atalhos de acessibilidade do teclado estão com os Flags que desligam o aviso (506, 122 e 58).</summary>
    private static bool StickyOff() =>
        Reg.GetString(StickyPath, "Flags") == "506" && Reg.GetString(FilterPath, "Flags") == "122" && Reg.GetString(TogglePath, "Flags") == "58";

    /// <summary>
    /// Aplicabilidade de POWER_PLAN_GAMING: só desktop (fora do AtlasOS), plano atual Equilibrado ou Economia de energia e Alto desempenho listado pelo powercfg.
    /// </summary>
    /// <param name="f">Fatos coletados.</param>
    private static bool PowerPlanGamingApplicable(SystemFacts f) =>
        !f.IsLaptop && !f.IsAtlas && f.ActivePowerPlanGuid is not null
        && (SameGuid(f.ActivePowerPlanGuid, BalancedPlanGuid) || SameGuid(f.ActivePowerPlanGuid, PowerSaverPlanGuid))
        && PlanExists(f, HighPerformancePlanGuid);

    /// <summary>Estado "já em vigor" de POWER_PLAN_GAMING: desktop já em Alto desempenho ou Desempenho máximo.</summary>
    /// <param name="f">Fatos coletados.</param>
    private static bool PowerPlanGamingActive(SystemFacts f) =>
        !f.IsLaptop && f.ActivePowerPlanGuid is not null && (SameGuid(f.ActivePowerPlanGuid, HighPerformancePlanGuid) || SameGuid(f.ActivePowerPlanGuid, UltimatePlanGuid));

    /// <summary>GUID do plano "Equilibrado" do Windows.</summary>
    public const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    /// <summary>GUID do plano "Alto desempenho" do Windows.</summary>
    public const string HighPerformancePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <summary>Plano de energia recomendado ao trabalho: Equilibrado em notebook, Alto desempenho em desktop.</summary>
    /// <param name="f">Fatos coletados (usa <see cref="SystemFacts.IsLaptop"/>).</param>
    public static string WorkPlanGuid(SystemFacts f) => f.IsLaptop ? BalancedPlanGuid : HighPerformancePlanGuid;

    /// <summary>GUID do plano "Economia de energia" do Windows.</summary>
    public const string PowerSaverPlanGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    /// <summary>GUID do plano "Desempenho máximo" (Ultimate Performance) do Windows.</summary>
    public const string UltimatePlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>true se <c>powercfg /list</c> listou o plano (<see cref="SystemFacts.PowerPlanGuids"/>); lista vazia (não lida) conta como inexistente.</summary>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="guid">GUID do plano.</param>
    private static bool PlanExists(SystemFacts f, string guid) => f.PowerPlanGuids.Any(g => SameGuid(g, guid));

    /// <summary>
    /// Aplicabilidade de POWER_PLAN_WORK. Desktop: só sugere trocar se o plano atual for Economia de energia e o Alto desempenho existir na lista do powercfg
    /// (Equilibrado e Alto desempenho são aceitáveis). Notebook: só sugere o Equilibrado se o plano atual for Alto desempenho ou Desempenho máximo e o Equilibrado existir.
    /// Nunca em sistema Atlas nem com o plano ativo desconhecido.
    /// </summary>
    /// <param name="f">Fatos coletados.</param>
    private static bool PowerPlanWorkApplicable(SystemFacts f)
    {
        if (f.IsAtlas || f.ActivePowerPlanGuid is null) return false;
        if (f.IsLaptop)
            return (SameGuid(f.ActivePowerPlanGuid, HighPerformancePlanGuid) || SameGuid(f.ActivePowerPlanGuid, UltimatePlanGuid)) && PlanExists(f, BalancedPlanGuid);
        return SameGuid(f.ActivePowerPlanGuid, PowerSaverPlanGuid) && PlanExists(f, HighPerformancePlanGuid);
    }

    /// <summary>Estado "já em vigor" de POWER_PLAN_WORK: desktop com Equilibrado ou Alto desempenho; notebook com Equilibrado.</summary>
    /// <param name="f">Fatos coletados.</param>
    private static bool PowerPlanWorkActive(SystemFacts f)
    {
        if (f.ActivePowerPlanGuid is null) return false;
        if (SameGuid(f.ActivePowerPlanGuid, BalancedPlanGuid)) return true;
        return !f.IsLaptop && SameGuid(f.ActivePowerPlanGuid, HighPerformancePlanGuid);
    }

    /// <summary>Compara dois GUIDs como texto, ignorando espaços nas pontas e diferença entre maiúsculas e minúsculas.</summary>
    /// <param name="a">Primeiro GUID.</param>
    /// <param name="b">Segundo GUID.</param>
    private static bool SameGuid(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    /// <summary>Busca um item pelo Id (sem diferenciar maiúsculas); null se não existir.</summary>
    public static FixDefinition? Get(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
