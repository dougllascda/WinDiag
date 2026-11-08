using System.Globalization;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Placa de vídeo e subsistema gráfico: driver, VRAM, dados do nvidia-smi (VBIOS, link PCIe, BAR1, limitações de clock),
/// HAGS, Game DVR, Modo de Jogo, MPO e otimizações para jogos em janela.
/// Fontes: WMI (Win32_VideoController), registro, nvidia-smi (processo externo com timeout). Não exige administrador.
/// Sem nvidia-smi ou sem GPU NVIDIA, os campos do driver ficam "n/d" e uma nota é registrada.
/// </summary>
public sealed class GpuCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "gpu";
    /// <inheritdoc/>
    public string Title => Loc.T("Placa de vídeo", "Graphics card");

    private const string ClassRoot = @"HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] SmiFields =
    {
        "driver_version", "vbios_version", "memory.total", "memory.used", "pcie.link.gen.current", "pcie.link.gen.max",
        "pcie.link.gen.gpumax", "pcie.link.gen.hostmax", "pcie.link.width.current", "pcie.link.width.max", "temperature.gpu",
        "power.draw", "power.limit", "clocks.current.graphics", "clocks.max.graphics", "clocks.current.memory", "pstate",
        "utilization.gpu", "pci.sub_device_id", "temperature.memory", "fan.speed", "power.default_limit", "power.min_limit", "power.max_limit",
    };

    private static double? Dbl(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int Int(string? s) => Dbl(s) is { } d ? (int)d : 0;

    /// <summary>Valor numérico do nvidia-smi (ponto decimal) formatado em pt-BR com unidade; "n/d" se ausente.</summary>
    private static string U(string? s, string unit) => Dbl(s) is { } d ? Fmt.Num(d) + " " + unit : Fmt.NA;

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        facts.GpuModern = false;
        facts.IsNvidia = false;

        // --- GPUs (Win32_VideoController) ---
        List<(string Desc, long Bytes)> regVram = new();
        try { regVram = ReadClassVram(); } catch (Exception ex) { res.Note(Loc.T("VRAM no registro (Control\\Class\\{4d36e968...}): ", "VRAM in the registry (Control\\Class\\{4d36e968...}): ") + ex.Message); }

        var gpus = Wmi.Query("SELECT * FROM Win32_VideoController", onError: e => res.Note("Win32_VideoController: " + e));
        foreach (var g in gpus)
        {
            try
            {
                var name = g.S("Name") ?? "";
                if (Regex.IsMatch(name, "Basic|Remote")) continue;
                var drvVer = g.S("DriverVersion");
                bool nv = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                if (nv) facts.IsNvidia = true;
                var drv = nv ? Loc.T($"{drvVer} (versão NVIDIA {NvidiaSmi.FormatNvidiaDriver(drvVer)})", $"{drvVer} (NVIDIA version {NvidiaSmi.FormatNvidiaDriver(drvVer)})") : drvVer;
                if (Regex.IsMatch(name, "RTX|GTX 16|RX 6|RX 7|RX 8|Arc")) facts.GpuModern = true;
                // GPU principal para a análise: prefere a dedicada (NVIDIA, Radeon RX, Arc) à integrada.
                bool dedicated = Regex.IsMatch(name, @"NVIDIA|Radeon\s+RX|\bArc\b", RegexOptions.IgnoreCase);
                if (facts.GpuName is null || (dedicated && !Regex.IsMatch(facts.GpuName, @"NVIDIA|Radeon\s+RX|\bArc\b", RegexOptions.IgnoreCase)))
                {
                    facts.GpuName = name;
                    var rv = regVram.FirstOrDefault(r => string.Equals(r.Desc, name, StringComparison.OrdinalIgnoreCase));
                    if (rv.Bytes > 0) facts.GpuVramMiB = Math.Round(rv.Bytes / 1048576d);
                }

                var grp = res.Group(name);
                grp.Add("Driver", drv);
                grp.Add(Loc.T("Data do driver", "Driver date"), Fmt.Date(Fmt.ParseWmiDate(g.P("DriverDate"))));
                grp.Add(Loc.T("Fabricante do chip", "Chip vendor"), g.S("AdapterCompatibility"));
                var reg = regVram.FirstOrDefault(r => string.Equals(r.Desc, name, StringComparison.OrdinalIgnoreCase));
                grp.Add(Loc.T("VRAM (registro)", "VRAM (registry)"), reg.Bytes > 0 ? Fmt.Num(Math.Round(reg.Bytes / 1048576d)) + " MiB" : null);
            }
            catch (Exception ex) { res.Note(Loc.T("Placa de vídeo: leitura parcial (" + ex.Message + ")", "Graphics card: partial reading (" + ex.Message + ")")); }
        }
        if (gpus.Count == 0 && regVram.Count > 0)
        {
            foreach (var r in regVram.Where(r => !Regex.IsMatch(r.Desc, "Basic|Remote")))
                res.Group(r.Desc).Add(Loc.T("VRAM (registro)", "VRAM (registry)"), r.Bytes > 0 ? Fmt.Num(Math.Round(r.Bytes / 1048576d)) + " MiB" : null);
        }

        // --- nvidia-smi ---
        try { CollectSmi(res, facts); } catch (Exception ex) { res.Note(Loc.T("nvidia-smi: leitura parcial (" + ex.Message + ")", "nvidia-smi: partial reading (" + ex.Message + ")")); }

        // --- Subsistema gráfico ---
        try { CollectSubsystem(res, facts); } catch (Exception ex) { res.Note(Loc.T("Subsistema gráfico: leitura parcial (" + ex.Message + ")", "Graphics subsystem: partial reading (" + ex.Message + ")")); }

        return res;
    }

    private static List<(string Desc, long Bytes)> ReadClassVram()
    {
        var list = new List<(string, long)>();
        for (int n = 0; n < 10; n++)
        {
            var p = $"{ClassRoot}\\{n:D4}";
            var desc = Reg.GetString(p, "DriverDesc");
            if (string.IsNullOrEmpty(desc)) continue;
            long bytes = 0;
            var mem = Reg.Get(p, "HardwareInformation.qwMemorySize");
            try
            {
                if (mem is byte[] b && b.Length >= 8) bytes = BitConverter.ToInt64(b, 0);
                else if (mem is not null) bytes = Convert.ToInt64(mem, CultureInfo.InvariantCulture);
            }
            catch (Exception) { /* valor de VRAM ilegível: fica 0 */ }
            list.Add((desc, bytes));
        }
        return list;
    }

    private static void CollectSmi(SectionResult res, SystemFacts facts)
    {
        var smi = NvidiaSmi.Path;
        if (smi is null)
        {
            if (facts.IsNvidia) res.Note(Loc.T("nvidia-smi não encontrado: dados do driver NVIDIA (VBIOS, link PCIe, BAR1) indisponíveis", "nvidia-smi not found: NVIDIA driver data (VBIOS, PCIe link, BAR1) unavailable"));
            return;
        }
        var f = NvidiaSmi.QueryFields(smi, SmiFields);
        if (f.Values.All(v => v is null))
        {
            // nvidia-smi existe, mas não respondeu (sem GPU NVIDIA, driver parado ou tempo esgotado).
            res.Note(Loc.T("nvidia-smi não retornou dados (GPU não NVIDIA, driver indisponível ou tempo esgotado)", "nvidia-smi returned no data (non-NVIDIA GPU, driver unavailable or timed out)"));
            return;
        }
        string? F(string k) => f.TryGetValue(k, out var v) ? v : null;
        double? vramMiB = Dbl(F("memory.total"));
        if (vramMiB is > 0) facts.GpuVramMiB = vramMiB;

        var g = res.Group(Loc.T("Dados do driver NVIDIA (nvidia-smi)", "NVIDIA driver data (nvidia-smi)"));
        g.Add("VBIOS", F("vbios_version"));
        g.Add(Loc.T("VRAM total / em uso", "VRAM total / used"), $"{U(F("memory.total"), "MiB")} / {U(F("memory.used"), "MiB")}");
        g.Add(Loc.T("Clock GPU atual / máx.", "GPU clock current / max."), Loc.T($"{U(F("clocks.current.graphics"), "MHz")} / {U(F("clocks.max.graphics"), "MHz")} | memória {U(F("clocks.current.memory"), "MHz")}", $"{U(F("clocks.current.graphics"), "MHz")} / {U(F("clocks.max.graphics"), "MHz")} | memory {U(F("clocks.current.memory"), "MHz")}"));
        g.Add(Loc.T("Temp. / Potência / Estado", "Temp. / Power / State"), Loc.T($"{U(F("temperature.gpu"), "°C")} | {U(F("power.draw"), "W")} de {U(F("power.limit"), "W")} | {F("pstate") ?? Fmt.NA}", $"{U(F("temperature.gpu"), "°C")} | {U(F("power.draw"), "W")} of {U(F("power.limit"), "W")} | {F("pstate") ?? Fmt.NA}"));
        g.Add(Loc.T("Fabricante da placa", "Board vendor"), NvidiaSmi.BoardVendor(F("pci.sub_device_id")));
        if (F("temperature.memory") is not null || F("fan.speed") is not null)
            g.Add(Loc.T("Temp. memória / Ventoinha", "Memory temp. / Fan"), $"{U(F("temperature.memory"), "°C")} | {U(F("fan.speed"), "%")}");
        if (F("power.default_limit") is not null || F("power.min_limit") is not null || F("power.max_limit") is not null)
            g.Add(Loc.T("Limite de potência (padrão / mín. / máx.)", "Power limit (default / min. / max.)"), $"{U(F("power.default_limit"), "W")} / {U(F("power.min_limit"), "W")} / {U(F("power.max_limit"), "W")}");
        var thr = NvidiaSmi.ThrottleReasons(smi);
        if (thr is not null) g.Add(Loc.T("Limitação de clock (ativa)", "Clock throttling (active)"), thr.Count > 0 ? string.Join(", ", thr) : Loc.T("nenhuma", "none"));

        int genMax = Int(F("pcie.link.gen.max")), genHost = Int(F("pcie.link.gen.hostmax")), genGpu = Int(F("pcie.link.gen.gpumax"));
        int genCur = Int(F("pcie.link.gen.current")), wCur = Int(F("pcie.link.width.current")), wMax = Int(F("pcie.link.width.max"));
        facts.GpuPcieGenMax = genGpu > 0 ? genGpu : null;
        facts.GpuPcieHostMax = genHost > 0 ? genHost : null;
        facts.GpuPcieGenCurrent = genCur > 0 ? genCur : null;
        facts.GpuPcieWidthCurrent = wCur > 0 ? wCur : null;
        facts.GpuPcieWidthMax = wMax > 0 ? wMax : null;
        // Limite de potência máximo medido (usado na estimativa de fonte); senão o limite aplicado.
        facts.GpuPowerLimitW = Dbl(F("power.max_limit")) is > 0 and var pmax ? pmax : Dbl(F("power.limit")) is > 0 and var plim ? plim : null;
        bool atMax = genCur > 0 && genCur == genMax;
        string curNote = atMax ? Loc.T("no máximo disponível", "at the maximum available") : Loc.T("abaixo do máximo: o link cai em repouso e sobe sob carga", "below the maximum: the link drops at idle and rises under load");
        string Gen(int n) => n > 0 ? n.ToString() : Fmt.NA;
        g.Add(Loc.T("Interface", "Interface"), $"PCIe {Gen(genGpu)}.0 x{wCur} @ {Gen(genCur)}.0 x{wCur}");
        g.Add(Loc.T("Link PCIe atual", "Current PCIe link"), $"Gen {Gen(genCur)} x{wCur} ({curNote})");
        g.Add(Loc.T("Link PCIe máximo", "Maximum PCIe link"), Loc.T($"GPU: Gen {Gen(genGpu)} | Host: Gen {Gen(genHost)} | Efetivo: Gen {Gen(genMax)} | Largura máx. x{wMax}", $"GPU: Gen {Gen(genGpu)} | Host: Gen {Gen(genHost)} | Effective: Gen {Gen(genMax)} | Max. width x{wMax}"));

        int lanes = wCur > 0 ? wCur : wMax;
        var bwNow = Pcie.Bandwidth(genMax, lanes);
        if (bwNow is not null)
        {
            var bwG3 = Pcie.Bandwidth(3, lanes);
            var bwGpu = Pcie.Bandwidth(genGpu, lanes);
            g.Add(Loc.T("Banda PCIe (aprox.)", "PCIe bandwidth (approx.)"), Loc.T($"{Fmt.Num(bwNow.Value)} GB/s agora (Gen {genMax} x{lanes}) | {Fmt.Num(bwG3 ?? 0)} GB/s em Gen 3 | {(bwGpu is null ? Fmt.NA : Fmt.Num(bwGpu.Value))} GB/s no máximo da GPU", $"{Fmt.Num(bwNow.Value)} GB/s now (Gen {genMax} x{lanes}) | {Fmt.Num(bwG3 ?? 0)} GB/s at Gen 3 | {(bwGpu is null ? Fmt.NA : Fmt.Num(bwGpu.Value))} GB/s at the GPU maximum"));
            if (wMax > lanes)
                g.Notes.Add(Loc.T($"Largura x{wMax} é o tamanho do slot; a placa usa x{lanes} linhas elétricas (normal em GPUs como a RTX 5060).", $"Width x{wMax} is the slot size; the card uses x{lanes} electrical lanes (normal for GPUs such as the RTX 5060)."));
        }

        if (genHost > 0 && genHost < 3)
        {
            // Só o dado medido. A gravidade e a causa (BIOS, placa, CPU ou GPU) ficam na "Análise da plataforma",
            // que cruza esta medição com CPU e chipset; assim o mesmo problema não aparece duas vezes como ATENÇÃO.
            var bwTxt = bwNow is null ? "?" : Fmt.Num(bwNow.Value);
            var ev = Loc.T($"Gen {genMax} x{lanes} (~{bwTxt} GB/s); host reporta no máximo Gen {genHost}", $"Gen {genMax} x{lanes} (~{bwTxt} GB/s); host reports at most Gen {genHost}");
            res.Find(Severity.Info, Loc.T("Link PCIe em geração antiga", "PCIe link on an older generation"), ev,
                Loc.T("A placa de vídeo está operando numa geração de PCIe antiga, com uma fração da banda que suporta. Quando a VRAM enche e texturas precisam vir da RAM, banda reduzida pode provocar engasgos.",
                    "The graphics card is running on an older PCIe generation, with a fraction of the bandwidth it supports. When VRAM fills up and textures must come from RAM, reduced bandwidth can cause stutters."),
                Loc.T("Veja a Análise da plataforma (no Resumo): ela mostra onde está o limite e o que dá para melhorar.",
                    "See the Platform analysis (in the Summary): it shows where the limit is and what can be improved."),
                "", 0, id: "pcie.old_generation",
                simple: Loc.T("A placa de vídeo está conectada por um caminho mais lento do que ela suporta. Em jogos pesados isso pode causar engasgos.",
                    "The graphics card is connected through a slower path than it supports. In heavy games this can cause stutters."), guide: "PcieSlot");
        }
        else if (genMax > 0)
        {
            res.Find(Severity.Ok, Loc.T("Link PCIe", "PCIe link"), Loc.T($"Efetivo Gen {genMax}, largura x{lanes}", $"Effective Gen {genMax}, width x{lanes}"), id: "pcie.link");
        }

        var bar = NvidiaSmi.Bar1TotalMiB(smi);
        if (bar is not null && vramMiB is > 0)
        {
            facts.Bar1MiB = bar;
            facts.ReBarActive = bar >= vramMiB * 0.9 ? true : bar <= 512 ? false : null;
            g.Add("BAR1 (Resizable BAR)", $"{Fmt.Num(bar.Value)} MiB (VRAM {Fmt.Num(vramMiB.Value)} MiB)");
            if (bar >= vramMiB * 0.9)
                res.Find(Severity.Ok, "Resizable BAR", Loc.T($"Ativo (BAR1 {Fmt.Num(bar.Value)} MiB cobre a VRAM)", $"Enabled (BAR1 {Fmt.Num(bar.Value)} MiB covers the VRAM)"), id: "gpu.rebar_on");
            // Sem texto simples de propósito: a mensagem para leigos sobre o Resizable BAR fica na Análise da plataforma
            // (analise.rebar), que também explica a relação com o link PCIe. Assim o assunto não aparece duas vezes.
            else if (bar <= 512)
                res.Find(Severity.Aviso, Loc.T("Resizable BAR desligado", "Resizable BAR off"), $"BAR1 = {Fmt.Num(bar.Value)} MiB",
                    Loc.T("Sem Resizable BAR a CPU enxerga a VRAM em janelas pequenas. Em alguns jogos isso custa desempenho.",
                        "Without Resizable BAR the CPU sees the VRAM in small windows. In some games this costs performance."),
                    Loc.T("Na BIOS, ative \"Above 4G Decoding\" e \"Resizable BAR\" (se a placa-mãe suportar na sua versão de BIOS). A Análise da plataforma verifica os pré-requisitos.",
                        "In the BIOS, enable \"Above 4G Decoding\" and \"Resizable BAR\" (if the motherboard supports it in your BIOS version). The Platform analysis checks the prerequisites."), "", 0,
                    id: "gpu.rebar_off", simple: Loc.T("Um recurso da BIOS que ajuda o processador a falar com a placa de vídeo está desligado. Ligá-lo pode dar um pequeno ganho em alguns jogos.",
                        "A BIOS feature that helps the processor talk to the graphics card is off. Enabling it may give a small gain in some games."), guide: "ReBar");
        }
    }

    private static void CollectSubsystem(SectionResult res, SystemFacts facts)
    {
        int? hags = Reg.GetInt(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        int? dvr = Reg.GetInt(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled");
        int? gm = Reg.GetInt(@"HKCU:\Software\Microsoft\GameBar", "AutoGameModeEnabled");
        int? mpo = Reg.GetInt(@"HKLM:\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
        facts.HAGS = hags; facts.GameDVR = dvr; facts.MPO = mpo;
        facts.GameModeOff = gm == 0;
        var ugs = Reg.GetString(@"HKCU:\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
        string? wopt = null;
        if (!string.IsNullOrEmpty(ugs))
        {
            var m = Regex.Match(ugs, "SwapEffectUpgradeEnable=([01])", RegexOptions.IgnoreCase);
            if (m.Success) wopt = m.Groups[1].Value;
        }
        facts.WindowedOpt = wopt;

        var g = res.Group(Loc.T("Subsistema gráfico", "Graphics subsystem"));
        g.Add("HAGS (HwSchMode)", hags is null ? Loc.T("não definido (padrão do driver)", "not set (driver default)") : hags.ToString());
        g.Add("Game DVR (GameDVR_Enabled)", dvr is null ? Loc.T("não definido", "not set") : dvr.ToString());
        g.Add(Loc.T("Modo Jogo (AutoGameMode)", "Game Mode (AutoGameMode)"), gm is null ? Loc.T("não definido (padrão: ligado)", "not set (default: on)") : gm.ToString());
        g.Add("MPO (OverlayTestMode)", mpo is null ? Loc.T("não definido (MPO ativo)", "not set (MPO active)") : mpo == 5 ? Loc.T("5 (MPO desativado)", "5 (MPO disabled)") : mpo.ToString());
        g.Add(Loc.T("Otimizações para jogos em janela", "Optimizations for windowed games"), wopt is null ? Loc.T("não definido (padrão do Windows)", "not set (Windows default)") : $"SwapEffectUpgradeEnable={wopt}");

        var hagsT = Loc.T("Agendamento de GPU (HAGS)", "GPU scheduling (HAGS)");
        if (hags == 2) res.Find(Severity.Ok, hagsT, Loc.T("Ativo", "Enabled"), id: "gpu.hags");
        else if (hags is null) res.Find(Severity.Info, hagsT, Loc.T("Sem valor definido: o driver decide", "No value set: the driver decides"), Loc.T("O driver escolhe o modo. Não há problema.", "The driver picks the mode. This is not a problem."), id: "gpu.hags");
        else res.Find(Severity.Info, hagsT, Loc.T($"HwSchMode = {hags} (desligado)", $"HwSchMode = {hags} (off)"), Loc.T("HAGS pode ajudar ou atrapalhar conforme o jogo e o driver. Não é correção universal.", "HAGS can help or hurt depending on the game and driver. It is not a universal fix."), "", "HAGS", id: "gpu.hags",
            simple: Loc.T("Um recurso do Windows que deixa a placa de vídeo organizar o próprio trabalho está desligado. Pode ajudar ou atrapalhar, dependendo do jogo.", "A Windows feature that lets the graphics card schedule its own work is off. It can help or hurt, depending on the game."));

        var dvrT = Loc.T("Captura Xbox Game DVR", "Xbox Game DVR capture");
        if (dvr == 0) res.Find(Severity.Ok, dvrT, Loc.T("Desativada", "Disabled"), id: "gpu.gamedvr");
        else res.Find(Severity.Aviso, dvrT, Loc.T("Ativa ou não definida", "Enabled or not set"), Loc.T("A captura em segundo plano consome GPU e disco e pode gerar engasgos.", "Background capture uses GPU and disk and can cause stutters."), Loc.T("Desative se você não grava clipes pela Barra de Jogos.", "Disable it if you do not record clips with the Game Bar."), "GAMEDVR", id: "gpu.gamedvr",
            simple: Loc.T("O Windows pode estar gravando o jogo em segundo plano, o que gasta desempenho. Se você não grava clipes, vale desligar.", "Windows may be recording the game in the background, which costs performance. If you do not record clips, it is worth turning off."));

        if (facts.GameModeOff)
            res.Find(Severity.Aviso, Loc.T("Modo de Jogo", "Game Mode"), Loc.T("Desligado (AutoGameModeEnabled = 0)", "Off (AutoGameModeEnabled = 0)"), Loc.T("O Modo de Jogo do Windows prioriza o jogo e reduz atividade de segundo plano durante a partida. Desligado, essa priorização não ocorre.", "Windows Game Mode prioritizes the game and reduces background activity during play. When off, this prioritization does not happen."), Loc.T("Ative em Configurações > Jogos > Modo de Jogo.", "Enable it in Settings > Gaming > Game Mode."), "GAMEMODE_ON", 30, id: "gpu.game_mode",
                simple: Loc.T("O Modo de Jogo ajuda o Windows a dar prioridade ao jogo. Está desligado; ligar costuma ser inofensivo.", "Game Mode helps Windows prioritize the game. It is off; turning it on is usually harmless."));

        var woT = Loc.T("Otimizações para jogos em janela", "Optimizations for windowed games");
        if (wopt == "1") res.Find(Severity.Ok, woT, Loc.T("Ativadas (SwapEffectUpgradeEnable=1)", "Enabled (SwapEffectUpgradeEnable=1)"), id: "gpu.windowed_opt");
        else if (wopt == "0") res.Find(Severity.Aviso, woT, Loc.T("Desativadas (SwapEffectUpgradeEnable=0)", "Disabled (SwapEffectUpgradeEnable=0)"), Loc.T("Sem essa opção, jogos DX10/11 em janela ou sem borda usam o modelo de apresentação antigo, o que piora latência e frame pacing. O efeito é mais notável com dois monitores.", "Without this option, windowed or borderless DX10/11 games use the old presentation model, which worsens latency and frame pacing. The effect is more noticeable with two displays."), Loc.T("Ative em Configurações > Sistema > Tela > Gráfico > Alterar configurações gráficas padrão > Otimizações para jogos em janela.", "Enable it in Settings > System > Display > Graphics > Change default graphics settings > Optimizations for windowed games."), "WINDOWED_OPT", 45, id: "gpu.windowed_opt");
        else res.Find(Severity.Info, woT, Loc.T("não definido (padrão do Windows)", "not set (Windows default)"), Loc.T("Quando ativa, melhora latência e frame pacing de jogos DX10/11 em janela ou sem borda, relevante com dois monitores. Não é causa comprovada de problemas.", "When enabled, it improves latency and frame pacing of windowed or borderless DX10/11 games, relevant with two displays. It is not a proven cause of problems."), Loc.T("Pode ser ativada em Configurações > Sistema > Tela > Gráfico.", "It can be enabled in Settings > System > Display > Graphics."), "WINDOWED_OPT", 30, id: "gpu.windowed_opt");

        int displays = 0;
        try { displays = DisplayApi.EnumActiveMonitors().Count; } catch { }
        if (mpo == 5) res.Find(Severity.Info, "Multiplane Overlay (MPO)", Loc.T("Desativado por registro", "Disabled via registry"), Loc.T("Você já desativou o MPO. Se os engasgos persistem, ele não era a causa.", "You have already disabled MPO. If the stutters persist, it was not the cause."), id: "gpu.mpo");
        else if (displays >= 2) res.Find(Severity.Info, "Multiplane Overlay (MPO)", Loc.T("Ativo com mais de um monitor", "Enabled with more than one display"), Loc.T("Hipótese de teste: MPO com vários monitores e taxas diferentes já foi associado a flicker e engasgos. Não é causa comprovada; vale um teste A/B.", "Test hypothesis: MPO with several displays and different rates has been linked to flicker and stutters. It is not a proven cause; an A/B test is worthwhile."), "", "MPO_OFF", id: "gpu.mpo");
    }
}
