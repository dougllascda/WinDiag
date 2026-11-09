using System.Text.RegularExpressions;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Util;
using static WinDiag.Core.Knowledge.PlatformDb;

namespace WinDiag.Core.Rules;

/// <summary>
/// "Análise da plataforma": cruza os fatos medidos (PCIe, RAM, BAR1, monitores) com as tabelas de
/// <see cref="PlatformDb"/> para dizer ONDE está o limite (configuração/BIOS, placa/chipset, CPU ou GPU),
/// O QUE dá para melhorar, o CUSTO (grátis = ajuste/BIOS; hardware = troca de peça) e o GANHO em números.
/// Regra de ouro: só afirma limite quando os dois lados são conhecidos (medição + tabela); caso contrário
/// descreve apenas o que foi medido. Cada tópico é isolado em try/catch. Os textos seguem o idioma de <see cref="Loc"/>.
/// </summary>
public static class Insights
{
    /// <summary>Chave da seção de análise.</summary>
    public const string SectionKey = "analise";
    /// <summary>Título da seção de análise (no idioma atual).</summary>
    public static string SectionTitle => Loc.T("Análise da plataforma", "Platform analysis");

    /// <summary>Resultado da análise do link PCIe usado pelas etapas seguintes (Resizable BAR e upgrade).</summary>
    /// <param name="ConfigLimited">true se o link atual está abaixo do que o host e a placa de vídeo permitem (configuração, energia ou BIOS).</param>
    /// <param name="PlatformLimited">true se o limite vem da plataforma (CPU, chipset ou placa).</param>
    /// <param name="HostMax">Geração máxima que o host informa, se lida.</param>
    /// <param name="Lanes">Largura do link (número de pistas) considerada.</param>
    private sealed record LinkState(bool ConfigLimited, bool PlatformLimited, int? HostMax, int Lanes);

    /// <summary>Gera a seção a partir dos fatos e dos resultados dos coletores. Cada tópico é isolado: falhas viram notas.</summary>
    public static SectionResult Analyze(SystemFacts f, IReadOnlyList<SectionResult> results)
    {
        var res = new SectionResult { Key = SectionKey, Title = SectionTitle };

        CpuSpec? cpu = null; ChipsetSpec? chip = null; GpuSpec? gpu = null; UpgradeSuggestion? up = null;
        try { cpu = ResolveCpu(f.CpuName); } catch { }
        try { chip = ResolveChipset(f.Chipset) ?? ResolveChipset(f.BoardModel); } catch { }
        try { gpu = ResolveGpu(f.GpuName, f.IsLaptop); } catch { }

        if (string.IsNullOrWhiteSpace(f.CpuName)) res.Note(Loc.T("Análise: nome da CPU não lido", "Analysis: CPU name not read"));
        else if (cpu is null) res.Note(Loc.T("Análise: CPU não reconhecida na tabela interna; limites de PCIe e memória não são inferidos", "Analysis: CPU not recognized in the internal table; PCIe and memory limits are not inferred"));
        if (chip is null) res.Note(Loc.T("Análise: chipset da placa não identificado; limites da placa não são inferidos", "Analysis: board chipset not identified; board limits are not inferred"));
        if (!string.IsNullOrWhiteSpace(f.GpuName) && gpu is null) res.Note(Loc.T("Análise: GPU não reconhecida na tabela interna; só valem os dados medidos pelo driver", "Analysis: GPU not recognized in the internal table; only the data measured by the driver applies"));
        if (cpu is not null && chip is not null && chip.X16Gen(cpu) is null)
            res.Note(Loc.T($"Análise: combinação {Short(f.CpuName)} + {chip.Name} fora da tabela; geração PCIe da placa não inferida",
                $"Analysis: combination {Short(f.CpuName)} + {chip.Name} not in the table; board PCIe generation not inferred"));

        try { up = UpgradePath(cpu, chip, f.CpuCores, f.CpuThreads, Pos(f.GpuPcieGenMax) ?? gpu?.Gen); } catch { }

        LinkState link = new(false, false, null, 0);
        try { link = PcieChain(res, f, cpu, chip, gpu, up); } catch (Exception ex) { res.Note(Loc.T("Análise PCIe: ", "PCIe analysis: ") + ex.Message); }
        try { Memory(res, f, cpu, chip, up); } catch (Exception ex) { res.Note(Loc.T("Análise de memória: ", "Memory analysis: ") + ex.Message); }
        try { ReBar(res, f, cpu, gpu, link); } catch (Exception ex) { res.Note(Loc.T("Análise de Resizable BAR: ", "Resizable BAR analysis: ") + ex.Message); }
        try { Balance(res, f, cpu, gpu); } catch (Exception ex) { res.Note(Loc.T("Análise de equilíbrio: ", "Balance analysis: ") + ex.Message); }
        try { Upgrade(res, f, cpu, gpu, up, link); } catch (Exception ex) { res.Note(Loc.T("Análise de upgrade: ", "Upgrade analysis: ") + ex.Message); }
        try { Psu(res, f); } catch (Exception ex) { res.Note(Loc.T("Análise da fonte: ", "Power supply analysis: ") + ex.Message); }
        return res;
    }

    // ------------------------------------------------------------------ utilidades

    /// <summary>Devolve o valor se for positivo; zero, negativo e null viram null (o driver costuma devolver 0 para "não lido").</summary>
    /// <param name="v">Valor lido.</param>
    private static int? Pos(int? v) => v is > 0 ? v : null;

    /// <summary>Texto "Gen N" de uma geração PCIe.</summary>
    /// <param name="g">Geração.</param>
    private static string GenS(int g) => "Gen " + g;

    /// <summary>Largura de banda do link formatada em GB/s, ou "n/d" se a geração/pistas não constam da tabela.</summary>
    /// <param name="gen">Geração PCIe.</param>
    /// <param name="lanes">Número de pistas.</param>
    private static string Bw(int gen, int lanes) => Util.Pcie.Bandwidth(gen, lanes) is { } b ? Fmt.Num(b) + " GB/s" : Fmt.NA;

    /// <summary>Largura de banda como número, sem unidade ("?" se desconhecida), para compor frases do tipo "A para B GB/s".</summary>
    /// <param name="gen">Geração PCIe.</param>
    /// <param name="lanes">Número de pistas.</param>
    private static string BwNum(int gen, int lanes) => Util.Pcie.Bandwidth(gen, lanes) is { } b ? Fmt.Num(b) : "?";

    /// <summary>Nome curto da CPU: remove marcas registradas, frequência e sufixos de núcleos.</summary>
    private static string Short(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName)) return Fmt.NA;
        var s = Regex.Replace(cpuName, @"\((R|TM|C)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+CPU\s*@.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+-Core Processor.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+with Radeon.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^(Intel|AMD)\s+", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Nome curto da GPU: remove "NVIDIA" e "GeForce" do início.</summary>
    /// <param name="gpuName">Nome completo.</param>
    private static string GpuShort(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return Fmt.NA;
        return Regex.Replace(gpuName, @"^(NVIDIA\s+)?(GeForce\s+)?", "", RegexOptions.IgnoreCase).Trim();
    }

    /// <summary>Prefixo padrão "Onde está o limite:" do texto explicativo, lido depois pelo <see cref="ActionPlan"/>.</summary>
    /// <param name="element">Elemento que limita (processador, placa, slot...).</param>
    private static string Lim(string element) => Loc.T("Onde está o limite: ", "Where the limit is: ") + element;

    /// <summary>Monta a ação no formato "O que dá para melhorar: ... (custo: ...). Ganho esperado: ..." (ou o equivalente em inglês).</summary>
    private static string Fix(string what, bool free, string gain) =>
        Loc.T($"O que dá para melhorar: {what} (custo: {(free ? "grátis" : "hardware")}). Ganho esperado: {gain}",
              $"What can be improved: {what} (cost: {(free ? "free" : "hardware")}). Expected gain: {gain}");

    // ------------------------------------------------------------------ PCIe

    /// <summary>Analisa a cadeia PCIe da placa de vídeo: GPU, slot, chipset e CPU. Só afirma limite quando a medição do driver concorda com a tabela interna; sem tabela, descreve apenas o que foi medido.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="cpu">CPU resolvida (ou null).</param>
    /// <param name="chip">Chipset resolvido (ou null).</param>
    /// <param name="gpu">GPU resolvida (ou null).</param>
    /// <param name="up">Sugestão de upgrade (ou null).</param>
    /// <returns>Estado do link para as etapas seguintes.</returns>
    private static LinkState PcieChain(SectionResult res, SystemFacts f, CpuSpec? cpu, ChipsetSpec? chip, GpuSpec? gpu, UpgradeSuggestion? up)
    {
        var g = res.Group(Loc.T("Cadeia PCIe da placa de vídeo", "Graphics card PCIe chain"));

        int? gpuGen = Pos(f.GpuPcieGenMax) ?? gpu?.Gen;
        int? host = Pos(f.GpuPcieHostMax);
        int? cur = Pos(f.GpuPcieGenCurrent);
        int curW = f.GpuPcieWidthCurrent ?? 0;
        int lanes = curW > 0 ? curW : gpu?.Lanes ?? Pos(f.GpuPcieWidthMax) ?? 0;

        int? cpuGen = cpu?.PcieGen;
        int? chipGen = chip?.X16Gen(cpu);
        int? platform = cpuGen is null ? null : chipGen is null ? cpuGen : Math.Min(cpuGen.Value, chipGen.Value);
        bool bothKnown = cpuGen is not null && chipGen is not null;
        int? expected = gpuGen is not null && platform is not null ? Math.Min(gpuGen.Value, platform.Value) : null;

        string cpuShort = Short(f.CpuName);
        string gpuTxt = GpuShort(f.GpuName);
        string lanesTxt = lanes > 0 ? $" x{lanes}" : "";

        // Classificação
        string limiter;      // elo que limita (texto curto)
        string limiterWhy;
        bool config = false, platformLimited = false;
        if (host is null)
        {
            limiter = Loc.T("não determinado", "undetermined");
            limiterWhy = Loc.T("o driver não informou a geração máxima do host; o link atual pode estar reduzido em repouso (meça sob carga).",
                "the driver did not report the host's maximum generation; the current link may be reduced at idle (measure under load).");
        }
        else if (expected is null)
        {
            if (gpuGen is not null && host < gpuGen)
            {
                limiter = Loc.T("host (CPU, placa ou BIOS) abaixo da GPU", "host (CPU, board or BIOS) below the GPU");
                limiterWhy = Loc.T($"o host reporta no máximo {GenS(host.Value)} e a GPU suporta {GenS(gpuGen.Value)}; CPU/chipset não reconhecidos, então não dá para separar placa, CPU e configuração.",
                    $"the host reports at most {GenS(host.Value)} and the GPU supports {GenS(gpuGen.Value)}; CPU/chipset not recognized, so board, CPU and configuration cannot be told apart.");
            }
            else
            {
                limiter = Loc.T("nenhum identificado", "none identified");
                limiterWhy = Loc.T($"o host reporta {GenS(host.Value)}, sem diferença em relação à GPU.",
                    $"the host reports {GenS(host.Value)}, no difference from the GPU.");
            }
        }
        else if (host < expected)
        {
            config = true;
            limiter = bothKnown ? Loc.T("configuração (BIOS, slot ou riser)", "configuration (BIOS, slot or riser)") : Loc.T("placa, BIOS ou configuração", "board, BIOS or configuration");
            limiterWhy = Loc.T($"a CPU e o chipset permitem {GenS(expected.Value)}, mas o host reporta {GenS(host.Value)}.",
                $"the CPU and chipset allow {GenS(expected.Value)}, but the host reports {GenS(host.Value)}.");
        }
        else if (expected < gpuGen && host < gpuGen)
        {
            // A medição manda: se o host já reporta a geração da GPU (ou mais que a tabela), não há limite da plataforma a afirmar.
            platformLimited = true;
            limiter = chipGen is null || cpuGen < chipGen ? Loc.T("processador", "processor")
                : chipGen < cpuGen ? Loc.T("chipset/placa", "chipset/board") : Loc.T("processador e chipset", "processor and chipset");
            limiterWhy = chip is { DependsOnBoard: true }
                ? Loc.T($"o host reporta {GenS(host.Value)} e a GPU suporta {GenS(gpuGen!.Value)}; a {chip.Name} é Gen 4 ou Gen 5 conforme a placa, então o limite vem da medição.",
                    $"the host reports {GenS(host.Value)} and the GPU supports {GenS(gpuGen!.Value)}; the {chip.Name} is Gen 4 or Gen 5 depending on the board, so the limit comes from the measurement.")
                : Loc.T($"a plataforma entrega no máximo {GenS(Math.Max(expected.Value, host.Value))} e a GPU suporta {GenS(gpuGen!.Value)}.",
                    $"the platform delivers at most {GenS(Math.Max(expected.Value, host.Value))} and the GPU supports {GenS(gpuGen!.Value)}.");
        }
        else
        {
            limiter = Loc.T("nenhum", "none");
            limiterWhy = Loc.T($"o host reporta {GenS(host.Value)}, o máximo que a GPU e a plataforma suportam.",
                $"the host reports {GenS(host.Value)}, the maximum the GPU and platform support.");
        }

        // Tabela
        var t = g.Table(Loc.T("Cadeia PCIe", "PCIe chain"), Loc.T("Elo", "Link"), Loc.T("Suporta", "Supports"), Loc.T("Observação", "Note"));
        t.AddRow("GPU",
            gpuGen is null ? null : $"{GenS(gpuGen.Value)}{(gpu is null ? "" : $" x{gpu.Lanes}{(gpu.LanesVary ? Loc.T(" (ou x16, conforme a variante)", " (or x16, depending on the variant)") : "")}")}",
            string.IsNullOrWhiteSpace(f.GpuName) ? Loc.T("GPU não lida", "GPU not read")
                : $"{f.GpuName}; {(Pos(f.GpuPcieGenMax) is not null ? Loc.T("geração lida do driver (nvidia-smi)", "generation read from the driver (nvidia-smi)") : Loc.T("geração da tabela interna", "generation from the internal table"))}");
        t.AddRow("CPU",
            cpu is null ? null : Loc.T($"{GenS(cpu.PcieGen)} no slot x16 (até {Bw(cpu.PcieGen, 16)})", $"{GenS(cpu.PcieGen)} on the x16 slot (up to {Bw(cpu.PcieGen, 16)})"),
            cpu is null ? Loc.T("CPU não reconhecida na tabela interna", "CPU not recognized in the internal table")
                : Loc.T($"{cpuShort}: {cpu.Family}, soquete {cpu.Socket}{(cpu.PcieDependsOnChipset ? "; valor final depende do chipset/placa" : "")}",
                    $"{cpuShort}: {cpu.Family}, socket {cpu.Socket}{(cpu.PcieDependsOnChipset ? "; final value depends on the chipset/board" : "")}"));
        t.AddRow(Loc.T("Chipset e placa", "Chipset and board"),
            chip is null ? null : chipGen is null ? Loc.T("n/d para esta CPU", "n/a for this CPU") : IntelChipsetSupports(chip, cpu, chipGen.Value),
            chip is null ? Loc.T("chipset não identificado", "chipset not identified")
                : Loc.T($"{chip.Name}; placa {f.BoardModel ?? Fmt.NA}; overclock de memória: {(chip.MemoryOcAllowed ? "permitido" : "não permitido")}",
                    $"{chip.Name}; board {f.BoardModel ?? Fmt.NA}; memory overclock: {(chip.MemoryOcAllowed ? "allowed" : "not allowed")}"));
        t.AddRow(Loc.T("BIOS e host", "BIOS and host"),
            host is null ? null : Loc.T($"Host reporta no máximo {GenS(host.Value)}", $"Host reports at most {GenS(host.Value)}"),
            Loc.T($"BIOS {f.BiosVersion ?? Fmt.NA}; boot {(f.FirmwareUefi is null ? Fmt.NA : f.FirmwareUefi.Value ? "UEFI" : "legado")}; valor lido do driver (host max), reflete BIOS, slot e riser",
                $"BIOS {f.BiosVersion ?? Fmt.NA}; boot {(f.FirmwareUefi is null ? Fmt.NA : f.FirmwareUefi.Value ? "UEFI" : "legacy")}; value read from the driver (host max), reflects BIOS, slot and riser"));
        t.AddRow(Loc.T("Link atual", "Current link"),
            cur is null ? null : $"{GenS(cur.Value)}{lanesTxt} (~{Bw(cur.Value, lanes)})",
            curW > 0 && gpu is not null && curW < gpu.Lanes
                ? Loc.T($"largura x{curW} abaixo do x{gpu.Lanes} da GPU: slot ligado ao chipset, compartilhado ou riser; confirme no manual",
                    $"width x{curW} below the GPU's x{gpu.Lanes}: slot wired to the chipset, shared or riser; check the manual")
                : Loc.T("pode cair em repouso para economizar energia; confirmar sob carga (GPU-Z, Render Test)",
                    "may drop at idle to save power; confirm under load (GPU-Z, Render Test)"));
        t.AddRow(Loc.T("Elo que limita", "Limiting link"), limiter, limiterWhy);

        // Findings
        if (host is not null && gpuGen is not null)
        {
            var bwHost = Util.Pcie.Bandwidth(host.Value, lanes);
            if (config)
            {
                int exp = expected!.Value;
                bool lowBw = bwHost is null || bwHost < 15;
                var sev = lowBw ? Severity.Atencao : Severity.Aviso;
                int impact = lowBw ? 95 : 40;
                string cpuClause = Loc.T($"a CPU {cpuShort} entrega até {GenS(cpuGen!.Value)} no slot x16", $"the {cpuShort} CPU delivers up to {GenS(cpuGen!.Value)} on the x16 slot");
                string chipClause = chip is null ? "" :
                    chipGen == cpuGen && cpu is { IsIntel: true }
                        ? Loc.T($" e a {chip.Name} com CPU de {cpu.GenLabel} também é {GenS(chipGen!.Value)}", $" and the {chip.Name} with a {cpu.GenLabel} CPU is also {GenS(chipGen!.Value)}")
                        : Loc.T($" e a {chip.Name} permite até {GenS(chipGen!.Value)}", $" and the {chip.Name} allows up to {GenS(chipGen!.Value)}");
                string where = bothKnown ? Loc.T("configuração, não hardware", "configuration, not hardware")
                    : Loc.T("placa, BIOS ou configuração (a placa não foi identificada)", "board, BIOS or configuration (the board was not identified)");
                string explain = Lim(where) + Loc.T(
                    $". A GPU suporta {GenS(gpuGen.Value)}{(gpu is null ? "" : $" x{gpu.Lanes}")}, {cpuClause}{chipClause}, " +
                    $"mas o host reporta no máximo {GenS(host.Value)}. Isso aponta para a BIOS (opção de geração do slot PCIe fixada em {GenS(host.Value)}), " +
                    "um riser/extensor ou BIOS desatualizada. O app mede o limite reportado; a causa exata precisa ser confirmada na BIOS.",
                    $". The GPU supports {GenS(gpuGen.Value)}{(gpu is null ? "" : $" x{gpu.Lanes}")}, {cpuClause}{chipClause}, " +
                    $"but the host reports at most {GenS(host.Value)}. This points to the BIOS (PCIe slot generation option fixed at {GenS(host.Value)}), " +
                    "a riser/extender cable or an outdated BIOS. The app measures the reported limit; the exact cause must be confirmed in the BIOS.");
                string gain = lanes > 0
                    ? Loc.T($"{BwNum(host.Value, lanes)} → {Bw(exp, lanes)} (x{lanes}), cerca de {Fmt.Num((double)(Util.Pcie.Bandwidth(exp, lanes) ?? 0) / Math.Max(bwHost ?? 1, 0.1), 1)}x de banda para texturas que vêm da RAM quando a VRAM enche.",
                        $"{BwNum(host.Value, lanes)} → {Bw(exp, lanes)} (x{lanes}), about {Fmt.Num((double)(Util.Pcie.Bandwidth(exp, lanes) ?? 0) / Math.Max(bwHost ?? 1, 0.1), 1)}x the bandwidth for textures that come from RAM when VRAM fills up.")
                    : $"{GenS(host.Value)} → {GenS(exp)}.";
                string action = Fix(Loc.T("na BIOS, abra a configuração do slot PCIEX16 (PCIe x16) e use Auto ou Gen3; confirme que não há cabo riser e atualize a BIOS se a opção não existir",
                    "in the BIOS, open the PCIEX16 (PCIe x16) slot setting and use Auto or Gen3; make sure there is no riser cable and update the BIOS if the option does not exist"), true, gain);
                if (up is not null && Math.Min(gpuGen.Value, up.PcieGen) > exp)
                    action += Loc.T($" Acima de {GenS(exp)} só trocando a CPU (ver Caminho de upgrade; custo: hardware).",
                        $" Beyond {GenS(exp)} only by replacing the CPU (see Upgrade path; cost: hardware).");
                res.Find(sev, Loc.T("Link PCIe abaixo do que a plataforma permite", "PCIe link below what the platform allows"),
                    Loc.T($"Host reporta {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); a plataforma permite {GenS(exp)} (~{Bw(exp, lanes)})",
                        $"Host reports {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); the platform allows {GenS(exp)} (~{Bw(exp, lanes)})"),
                    explain, action, "", impact, id: "pcie.config_limited", guide: "PcieSlot",
                    simple: Loc.T("A placa de vídeo está conectada por um caminho mais lento do que o seu PC permite. Isso costuma ser só uma opção da BIOS e é grátis de corrigir.",
                        "The graphics card is connected through a slower path than your PC allows. This is usually just a BIOS option and is free to fix."));
            }
            else if (platformLimited)
            {
                int exp = Math.Max(expected!.Value, host.Value);
                string who = limiter;
                int better = up is not null ? Math.Min(gpuGen.Value, up.PcieGen) : 0;
                string depends = chip is { DependsOnBoard: true }
                    ? Loc.T($" O valor vem da medição do driver: a {chip.Name} é Gen 4 ou Gen 5 conforme a placa.", $" The value comes from the driver measurement: the {chip.Name} is Gen 4 or Gen 5 depending on the board.")
                    : "";
                string explain = Lim(Loc.T(
                    $"{who}. Esta plataforma entrega no máximo {GenS(exp)} no slot da GPU ({cpuShort}{(chip is null ? "" : $" + {chip.Name}")}) e a GPU suporta {GenS(gpuGen.Value)}.{depends} ",
                    $"{who}. This platform delivers at most {GenS(exp)} on the GPU slot ({cpuShort}{(chip is null ? "" : $" + {chip.Name}")}) and the GPU supports {GenS(gpuGen.Value)}.{depends} ")) +
                    Loc.T("Não é defeito nem configuração: é o máximo desta combinação. O efeito prático costuma aparecer só quando a VRAM enche e texturas precisam vir da RAM; não foi medido aqui.",
                        "It is neither a defect nor a configuration issue: it is the maximum of this combination. The practical effect usually shows up only when VRAM fills up and textures have to come from RAM; it was not measured here.");
                string action = better > exp
                    ? Fix(Loc.T($"trocar a CPU por {up!.Cpu} (ver Caminho de upgrade) leva o slot a {GenS(better)}", $"replacing the CPU with {up!.Cpu} (see Upgrade path) takes the slot to {GenS(better)}"),
                        false, $"{BwNum(exp, lanes)} → {Bw(better, lanes)} (x{lanes}).")
                    : Fix(Loc.T($"só trocando CPU e placa por uma plataforma {GenS(Math.Min(gpuGen.Value, 4))} ou superior", $"only by replacing CPU and board with a {GenS(Math.Min(gpuGen.Value, 4))} or newer platform"), false,
                        lanes > 0
                            ? Loc.T($"{BwNum(exp, lanes)} → {Bw(Math.Min(gpuGen.Value, exp + 1), lanes)} (x{lanes}) por geração a mais; sem garantia de ganho em FPS.",
                                $"{BwNum(exp, lanes)} → {Bw(Math.Min(gpuGen.Value, exp + 1), lanes)} (x{lanes}) per extra generation; no guaranteed FPS gain.")
                            : Loc.T("mais banda no link; sem garantia de ganho em FPS.", "more link bandwidth; no guaranteed FPS gain."));
                res.Find(Severity.Info, Loc.T("Link PCIe limitado pela plataforma", "PCIe link limited by the platform"),
                    Loc.T($"Host reporta {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); a GPU suporta {GenS(gpuGen.Value)} (~{Bw(gpuGen.Value, lanes)})",
                        $"Host reports {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); the GPU supports {GenS(gpuGen.Value)} (~{Bw(gpuGen.Value, lanes)})"),
                    explain, action, "", 30, id: "pcie.platform_limited",
                    simple: Loc.T("O caminho entre a placa de vídeo e o resto do PC está no máximo que esta combinação de peças permite. Não é defeito nem ajuste errado.",
                        "The path between the graphics card and the rest of the PC is at the maximum this combination of parts allows. It is neither a defect nor a wrong setting."));
            }
            else if (expected is null && host < gpuGen)
            {
                // Plataforma desconhecida: sem separar a causa, mas Gen 1/2 com poucas linhas ainda merece destaque.
                bool oldGen = host <= 2;
                res.Find(oldGen ? Severity.Aviso : Severity.Info, Loc.T("Link PCIe abaixo do máximo da GPU", "PCIe link below the GPU maximum"),
                    Loc.T($"Host reporta {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); a GPU suporta {GenS(gpuGen.Value)}",
                        $"Host reports {GenS(host.Value)}{lanesTxt} (~{Bw(host.Value, lanes)}); the GPU supports {GenS(gpuGen.Value)}"),
                    Lim(Loc.T("não foi possível separar CPU, placa e configuração, porque a CPU ou o chipset não foram reconhecidos. ",
                        "CPU, board and configuration could not be told apart because the CPU or chipset was not recognized. ")) +
                    Loc.T("O driver informa que o host só negocia essa geração.", "The driver reports that the host only negotiates this generation."),
                    Loc.T("O que dá para melhorar: conferir no manual da placa a geração do slot x16 e, na BIOS, a opção de geração do slot (Auto ou a máxima) (custo: grátis). Ganho esperado: não calculável sem identificar a plataforma.",
                        "What can be improved: check the x16 slot generation in the board manual and, in the BIOS, the slot generation option (Auto or the maximum) (cost: free). Expected gain: cannot be calculated without identifying the platform."),
                    "", oldGen ? 70 : 20, id: "pcie.below_gpu_max", guide: oldGen ? "PcieSlot" : "",
                    simple: Loc.T("A placa de vídeo está usando um caminho mais lento do que ela suporta, mas não foi possível descobrir o motivo com certeza.",
                        "The graphics card is using a slower path than it supports, but the reason could not be determined with certainty."));
            }
            else
            {
                res.Find(Severity.Ok, "Link PCIe", Loc.T($"Host reporta {GenS(host.Value)}{lanesTxt}; sem limite identificado entre GPU e plataforma",
                    $"Host reports {GenS(host.Value)}{lanesTxt}; no limit identified between GPU and platform"), id: "pcie.ok");
            }
        }
        else if (cur is not null && expected is not null && cur < expected)
        {
            res.Find(Severity.Info, Loc.T("Link PCIe atual abaixo do esperado", "Current PCIe link below expected"),
                Loc.T($"Link atual {GenS(cur.Value)}{lanesTxt}; a plataforma e a GPU permitem {GenS(expected.Value)}",
                    $"Current link {GenS(cur.Value)}{lanesTxt}; the platform and GPU allow {GenS(expected.Value)}"),
                Lim(Loc.T("indeterminado. O driver não informou o máximo do host e o link costuma cair em repouso para economizar energia; a leitura atual pode não representar o limite. ",
                    "undetermined. The driver did not report the host maximum and the link usually drops at idle to save power; the current reading may not represent the limit. ")) +
                Loc.T("Sem medição sob carga não é possível afirmar a causa.", "Without a measurement under load the cause cannot be stated."),
                Loc.T("O que dá para melhorar: ler o link sob carga (GPU-Z, Render Test) antes de mexer na BIOS (custo: grátis). Ganho esperado: não calculável sem essa leitura.",
                    "What can be improved: read the link under load (GPU-Z, Render Test) before changing the BIOS (cost: free). Expected gain: cannot be calculated without that reading."),
                id: "pcie.current_below",
                simple: Loc.T("A conexão da placa de vídeo está mais lenta agora, mas isso é normal em repouso. Só vale investigar se ela continuar lenta durante um jogo.",
                    "The graphics card connection is slower right now, but that is normal at idle. It is only worth investigating if it stays slow during a game."));
        }
        else if (host is null && gpuGen is null)
        {
            res.Note(Loc.T("Análise PCIe: o driver não informou as gerações do link (nvidia-smi ausente ou GPU não NVIDIA)",
                "PCIe analysis: the driver did not report the link generations (nvidia-smi missing or non-NVIDIA GPU)"));
        }

        return new LinkState(config, platformLimited, host, lanes);
    }

    /// <summary>Descreve o que o chipset suporta no slot x16 para a CPU atual (tabela por geração de CPU, ou "Gen 4 ou Gen 5 conforme a placa").</summary>
    /// <param name="chip">Chipset.</param>
    /// <param name="cpu">CPU atual (ou null).</param>
    /// <param name="chipGen">Geração PCIe do slot para esta combinação.</param>
    private static string IntelChipsetSupports(ChipsetSpec chip, CpuSpec? cpu, int chipGen)
    {
        if (chip.DependsOnBoard) return Loc.T("Gen 4 ou Gen 5 conforme a placa (a tabela só garante Gen 4; o limite é afirmado só com a medição do driver)",
            "Gen 4 or Gen 5 depending on the board (the table only guarantees Gen 4; the limit is stated only with the driver measurement)");
        if (chip.X16GenByCpuGen.Count == 0) return Loc.T($"{GenS(chipGen)} no slot x16 (limite do chipset)", $"{GenS(chipGen)} on the x16 slot (chipset limit)");
        var all = string.Join("; ", chip.X16GenByCpuGen.OrderBy(kv => kv.Key).Select(kv => Loc.T($"{kv.Key}ª geração = {GenS(kv.Value)}", $"gen {kv.Key} = {GenS(kv.Value)}")));
        return cpu is { IsIntel: true } ? Loc.T($"{GenS(chipGen)} com CPU de {cpu.GenLabel} (tabela: {all})", $"{GenS(chipGen)} with a {cpu.GenLabel} CPU (table: {all})") : all;
    }

    // ------------------------------------------------------------------ Memória

    /// <summary>Analisa a memória RAM: velocidade configurada x anunciada x máximo oficial da CPU, dual channel e se o chipset permite o perfil XMP/DOCP/EXPO.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="cpu">CPU resolvida (ou null).</param>
    /// <param name="chip">Chipset resolvido (ou null).</param>
    /// <param name="up">Sugestão de upgrade (ou null).</param>
    private static void Memory(SectionResult res, SystemFacts f, CpuSpec? cpu, ChipsetSpec? chip, UpgradeSuggestion? up)
    {
        var g = res.Group(Loc.T("Memória RAM", "RAM"));
        int? cur = Pos(f.RamConfiguredMTs);
        int? rated = Pos(f.RamRatedMTs);
        int? max = cpu?.MaxRamMTs(f.RamType);
        string typeTxt = string.IsNullOrWhiteSpace(f.RamType) ? "DDR" : f.RamType!;
        string chan = f.DualChannel switch
        {
            true => ", dual channel",
            false => f.RamModules <= 1 ? Loc.T(", canal único", ", single channel") : Loc.T(", pentes no mesmo canal", ", modules on the same channel"),
            _ => ""
        };
        string mods = f.RamModules > 0 ? $" ({f.RamModules} {(f.RamModules == 1 ? Loc.T("pente", "module") : Loc.T("pentes", "modules"))})" : "";

        g.Add(Loc.T("Velocidade atual", "Current speed"), cur is null ? null : $"{cur} MT/s{chan}{mods}");
        g.Add(Loc.T("Nominal dos pentes", "Module rated speed"), rated is null ? null : Loc.T($"{rated} MT/s (SMBIOS ou part number)", $"{rated} MT/s (SMBIOS or part number)"));
        g.Add(Loc.T("Máximo oficial da CPU", "CPU official maximum"), max is null ? null : $"{typeTxt}-{max} MT/s ({Short(f.CpuName)})");
        g.Add(Loc.T("Overclock de memória no chipset", "Memory overclock on the chipset"),
            chip is null ? null : Loc.T($"{chip.Name}: {(chip.MemoryOcAllowed ? "permitido (XMP/DOCP/EXPO)" : "não permitido")}",
                $"{chip.Name}: {(chip.MemoryOcAllowed ? "allowed (XMP/DOCP/EXPO)" : "not allowed")}"));

        if (cur is { } c)
        {
            bool belowRated = rated is { } r0 && r0 - c >= 150;
            bool atMax = max is { } m0 && c >= m0 - 100;
            string cpuS = Short(f.CpuName);
            string chipS = chip?.Name ?? Loc.T("o chipset", "the chipset");
            string ev = $"{c} MT/s" + (rated is null ? "" : Loc.T($" (nominal dos pentes {rated} MT/s)", $" (module rated {rated} MT/s)"))
                + (max is null ? "" : Loc.T($"; máximo oficial da CPU {max} MT/s", $"; CPU official maximum {max} MT/s"));
            bool locked = chip?.MemoryOcAllowed == false;
            string upRam = up is { RamMTs: { } um } && um > c
                ? Loc.T($" Com {up.Cpu} o máximo oficial sobe para {up.RamType}-{um} (custo: hardware), conforme o manual da placa.",
                    $" With {up.Cpu} the official maximum rises to {up.RamType}-{um} (cost: hardware), as per the board manual.")
                : "";

            if (belowRated && locked && max is { } mx)
            {
                if (atMax)
                    res.Find(Severity.Ok, Loc.T("RAM no máximo da plataforma", "RAM at the platform maximum"), ev,
                        Lim(Loc.T($"processador e chipset. O {cpuS} suporta oficialmente {typeTxt}-{mx} e a {chipS} não permite overclock de memória, então a RAM já está no máximo que a CPU suporta (os pentes aguentam {rated} MT/s, mas a plataforma não usa). RAM mais rápida não ajudaria sem trocar a CPU. Não é problema.",
                            $"processor and chipset. The {cpuS} officially supports {typeTxt}-{mx} and the {chipS} does not allow memory overclocking, so the RAM is already at the maximum the CPU supports (the modules can do {rated} MT/s, but the platform does not use it). Faster RAM would not help without replacing the CPU. This is not a problem.")),
                        Loc.T($"O que dá para melhorar: nada na RAM atual.{upRam} Ganho esperado: pequeno e dependente do jogo; não medido.",
                            $"What can be improved: nothing on the current RAM.{upRam} Expected gain: small and game-dependent; not measured."), id: "analise.ram_at_max",
                        simple: Loc.T("Sua memória já roda na velocidade máxima que o processador aceita. Não há o que melhorar aqui.",
                            "Your memory already runs at the maximum speed the processor accepts. There is nothing to improve here."));
                else
                    res.Find(Severity.Aviso, Loc.T("RAM abaixo do máximo da CPU", "RAM below the CPU maximum"), ev,
                        Lim(Loc.T($"configuração da BIOS. A {chipS} não permite overclock, mas a CPU suporta oficialmente {typeTxt}-{mx} e a memória está mais lenta que isso.",
                            $"BIOS configuration. The {chipS} does not allow overclocking, but the CPU officially supports {typeTxt}-{mx} and the memory is slower than that.")),
                        Fix(Loc.T($"na BIOS, ajustar a frequência da memória para {mx} MT/s (ou ativar o perfil, que a plataforma limita ao máximo da CPU)",
                                $"in the BIOS, set the memory frequency to {mx} MT/s (or enable the profile, which the platform caps at the CPU maximum)"), true,
                            Loc.T($"banda teórica de memória +{Fmt.Num((mx / (double)c - 1) * 100, 0)}% ({c} → {mx} MT/s); o efeito em FPS é menor e varia por jogo.",
                                $"theoretical memory bandwidth +{Fmt.Num((mx / (double)c - 1) * 100, 0)}% ({c} → {mx} MT/s); the effect on FPS is smaller and varies by game.")), "", 35,
                        id: "analise.ram_below_cpu_max", guide: "Xmp",
                        simple: Loc.T("Sua memória está rodando mais devagar do que o processador aceita. Um ajuste na BIOS, sem custo, deixa ela mais rápida.",
                            "Your memory is running slower than the processor accepts. A free BIOS adjustment makes it faster."));
            }
            else if (belowRated && chip?.MemoryOcAllowed == true)
            {
                int target = rated!.Value;
                string over = max is { } mx2 && target > mx2 + 100
                    ? Loc.T($" Acima de {mx2} MT/s é overclock fora da especificação da CPU, permitido pelo chipset {chipS}.",
                        $" Above {mx2} MT/s is overclocking beyond the CPU specification, allowed by the {chipS} chipset.")
                    : "";
                res.Find(Severity.Aviso, Loc.T("XMP/DOCP desligado", "XMP/DOCP off"), ev,
                    Lim(Loc.T($"configuração da BIOS. Os pentes são vendidos para {target} MT/s, mas rodam a {c} MT/s; {(max is null ? "o chipset permite" : $"a CPU suporta oficialmente {max} MT/s e o chipset permite")} ativar o perfil.{over}",
                        $"BIOS configuration. The modules are sold for {target} MT/s but run at {c} MT/s; {(max is null ? "the chipset allows" : $"the CPU officially supports {max} MT/s and the chipset allows")} enabling the profile.{over}")),
                    Fix(Loc.T("na BIOS, ativar o perfil XMP/DOCP/EXPO da memória", "in the BIOS, enable the memory XMP/DOCP/EXPO profile"), true,
                        Loc.T($"banda teórica de memória +{Fmt.Num((target / (double)c - 1) * 100, 0)}% ({c} → {target} MT/s); o efeito em FPS é menor e varia por jogo (maior em Ryzen e em CPUs de poucos núcleos).",
                            $"theoretical memory bandwidth +{Fmt.Num((target / (double)c - 1) * 100, 0)}% ({c} → {target} MT/s); the effect on FPS is smaller and varies by game (larger on Ryzen and on CPUs with few cores).")), "", 35,
                    id: "analise.xmp", guide: "Xmp",
                    simple: Loc.T("Sua memória foi feita para rodar mais rápido, mas está em velocidade básica. Ligar o perfil XMP na BIOS é grátis e deixa ela mais rápida.",
                        "Your memory was made to run faster but is at base speed. Turning on the XMP profile in the BIOS is free and makes it faster."));
            }
            else if (belowRated)
            {
                res.Find(Severity.Info, Loc.T("RAM abaixo da velocidade nominal", "RAM below rated speed"), ev,
                    Lim(Loc.T("indeterminado. Pode ser perfil XMP/DOCP desligado ou limite da CPU/chipset, mas a CPU ou o chipset não foram reconhecidos. Sem essa informação o app não afirma a causa.",
                        "undetermined. It may be the XMP/DOCP profile being off or a CPU/chipset limit, but the CPU or chipset was not recognized. Without that information the app does not state the cause.")),
                    Loc.T("O que dá para melhorar: conferir na BIOS se o perfil XMP/DOCP está ativo e qual é o máximo da CPU e da placa (custo: grátis). Ganho esperado: não calculável sem identificar a plataforma.",
                        "What can be improved: check in the BIOS whether the XMP/DOCP profile is active and what the CPU and board maximum is (cost: free). Expected gain: cannot be calculated without identifying the platform."),
                    id: "analise.ram_below_rated", guide: "Xmp",
                    simple: Loc.T("Sua memória está mais lenta do que o indicado nos pentes. Pode ser só um perfil desligado na BIOS; vale conferir.",
                        "Your memory is slower than the rating on the modules. It may just be a profile turned off in the BIOS; worth checking."));
            }
            else if (atMax)
            {
                string tail = locked
                    ? Loc.T($"a {chipS} não permite overclock de memória, então RAM mais rápida não ajudaria sem trocar a CPU. Não é problema.",
                        $"the {chipS} does not allow memory overclocking, so faster RAM would not help without replacing the CPU. This is not a problem.")
                    : chip is null ? Loc.T("acima disso só em overclock (XMP), fora da especificação da CPU.", "beyond that only with overclocking (XMP), outside the CPU specification.")
                    : Loc.T($"acima disso só em overclock (XMP/DOCP), fora da especificação da CPU; a {chipS} permite.", $"beyond that only with overclocking (XMP/DOCP), outside the CPU specification; the {chipS} allows it.");
                res.Find(Severity.Ok, Loc.T("RAM no máximo da plataforma", "RAM at the platform maximum"), ev,
                    Lim(Loc.T($"{(locked ? "processador e chipset" : "processador")}. O {cpuS} suporta oficialmente {typeTxt}-{max} e a RAM já está no máximo que a CPU suporta; {tail}",
                        $"{(locked ? "processor and chipset" : "processor")}. The {cpuS} officially supports {typeTxt}-{max} and the RAM is already at the maximum the CPU supports; {tail}")),
                    Loc.T($"O que dá para melhorar: nada na RAM atual.{upRam} Ganho esperado: pequeno e dependente do jogo; não medido.",
                        $"What can be improved: nothing on the current RAM.{upRam} Expected gain: small and game-dependent; not measured."), id: "analise.ram_at_max",
                    simple: Loc.T("Sua memória já roda na velocidade máxima que o processador aceita. Não há o que melhorar aqui.",
                        "Your memory already runs at the maximum speed the processor accepts. There is nothing to improve here."));
            }
            else if (max is { } mx3)
            {
                res.Find(Severity.Info, Loc.T("RAM abaixo do máximo oficial da CPU", "RAM below the CPU official maximum"), ev,
                    Lim(Loc.T($"memória instalada. Os pentes são de {c} MT/s e a CPU suporta oficialmente {typeTxt}-{mx3}.",
                        $"installed memory. The modules are {c} MT/s and the CPU officially supports {typeTxt}-{mx3}.")),
                    Fix(Loc.T($"trocar os pentes por um kit {typeTxt}-{mx3}", $"replace the modules with a {typeTxt}-{mx3} kit"), false,
                        Loc.T($"banda teórica de memória +{Fmt.Num((mx3 / (double)c - 1) * 100, 0)}%; o efeito em FPS é menor e varia por jogo.",
                            $"theoretical memory bandwidth +{Fmt.Num((mx3 / (double)c - 1) * 100, 0)}%; the effect on FPS is smaller and varies by game.")),
                    id: "analise.ram_below_official",
                    simple: Loc.T("Seus pentes de memória são mais lentos do que o processador suporta. Só trocando os pentes isso melhora, e o ganho em jogos é pequeno.",
                        "Your memory modules are slower than the processor supports. Only replacing the modules improves this, and the gain in games is small."));
            }
            else
            {
                res.Find(Severity.Ok, Loc.T("Velocidade da RAM", "RAM speed"), ev, id: "analise.ram_speed");
            }
        }
        else
        {
            res.Note(Loc.T("Análise de memória: velocidade configurada da RAM não lida", "Memory analysis: configured RAM speed not read"));
        }

        // Canais
        if (f.DualChannel == false)
        {
            string bw = cur is { } cc
                ? Loc.T($" Banda teórica: {Fmt.Num(cc * 8 / 1000d, 1)} GB/s em canal único contra {Fmt.Num(cc * 16 / 1000d, 1)} GB/s em dual channel.",
                    $" Theoretical bandwidth: {Fmt.Num(cc * 8 / 1000d, 1)} GB/s on a single channel versus {Fmt.Num(cc * 16 / 1000d, 1)} GB/s in dual channel.")
                : "";
            if (f.RamModules <= 1)
                res.Find(Severity.Aviso, Loc.T("RAM em canal único", "RAM in single channel"),
                    Loc.T($"{Math.Max(f.RamModules, 1)} pente instalado", $"{Math.Max(f.RamModules, 1)} module installed"),
                    Lim(Loc.T("configuração de pentes. Com um pente só a memória usa um canal, metade da banda possível.", "module configuration. With a single module the memory uses one channel, half the possible bandwidth.") + bw),
                    Fix(Loc.T("instalar um segundo pente igual (mesma capacidade e velocidade) nos slots indicados no manual", "install a second identical module (same capacity and speed) in the slots indicated in the manual"), false,
                        Loc.T("até 2x de banda teórica de memória; o efeito em FPS é maior em CPUs de poucos núcleos e em gráficos integrados.",
                            "up to 2x theoretical memory bandwidth; the effect on FPS is larger on CPUs with few cores and on integrated graphics.")), "", 45,
                    id: "analise.single_channel",
                    simple: Loc.T("Só há um pente de memória, então ela trabalha com metade da velocidade possível. Um segundo pente igual resolve.",
                        "There is only one memory module, so it works at half the possible speed. A second identical module fixes it."));
            else
                res.Find(Severity.Aviso, Loc.T("Pentes de RAM no mesmo canal", "RAM modules on the same channel"),
                    Loc.T($"{f.RamModules} pentes, 1 canal", $"{f.RamModules} modules, 1 channel"),
                    Lim(Loc.T("posição dos pentes. Os pentes estão ligados ao mesmo canal, o que reduz a banda pela metade.", "module placement. The modules are connected to the same channel, which halves the bandwidth.") + bw),
                    Fix(Loc.T("mudar os pentes para os slots recomendados no manual da placa (geralmente A2 e B2)", "move the modules to the slots recommended in the board manual (usually A2 and B2)"), true,
                        Loc.T("até 2x de banda teórica de memória, sem comprar nada.", "up to 2x theoretical memory bandwidth, without buying anything.")), "", 45,
                    id: "analise.same_channel",
                    simple: Loc.T("Os pentes de memória estão nos encaixes errados e trabalham com metade da velocidade. Trocá-los de lugar resolve, sem gastar nada.",
                        "The memory modules are in the wrong slots and work at half the speed. Moving them fixes it at no cost."));
        }
    }

    // ------------------------------------------------------------------ Resizable BAR

    /// <summary>Analisa o Resizable BAR: estado medido (BAR1 x VRAM), modo de boot e relação com o link PCIe. Secure Boot é só informado, pois não é requisito.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="cpu">CPU resolvida (ou null).</param>
    /// <param name="gpu">GPU resolvida (ou null).</param>
    /// <param name="link">Estado do link PCIe.</param>
    private static void ReBar(SectionResult res, SystemFacts f, CpuSpec? cpu, GpuSpec? gpu, LinkState link)
    {
        var g = res.Group("Resizable BAR");
        string state = f.ReBarActive switch { true => Loc.T("ativo", "active"), false => Loc.T("desligado", "off"), _ => null! };
        g.Add(Loc.T("Estado", "State"), state);
        g.Add("BAR1 / VRAM", f.Bar1MiB is null ? null : $"{Fmt.Num(f.Bar1MiB.Value)} MiB / {(f.GpuVramMiB is null ? Fmt.NA : Fmt.Num(f.GpuVramMiB.Value) + " MiB")}");
        g.Add(Loc.T("Boot UEFI", "UEFI boot"), f.FirmwareUefi is null ? null : f.FirmwareUefi.Value ? Loc.T("sim", "yes") : Loc.T("não (modo legado/CSM)", "no (legacy/CSM mode)"));
        g.Add("Secure Boot", f.SecureBoot is null ? null : f.SecureBoot.Value
            ? Loc.T("ligado (não é requisito do Resizable BAR)", "on (not a Resizable BAR requirement)")
            : Loc.T("desligado (não é requisito do Resizable BAR)", "off (not a Resizable BAR requirement)"));
        if (link.HostMax is { } hm && link.Lanes > 0)
            g.Add(Loc.T("Interação com o link", "Interaction with the link"),
                Loc.T($"o Resizable BAR usa o mesmo link PCIe: com {GenS(hm)} x{link.Lanes} a banda é {Bw(hm, link.Lanes)}",
                    $"Resizable BAR uses the same PCIe link: with {GenS(hm)} x{link.Lanes} the bandwidth is {Bw(hm, link.Lanes)}"));

        if (f.ReBarActive == true)
        {
            string ev = Loc.T($"BAR1 {Fmt.Num(f.Bar1MiB ?? 0)} MiB cobre a VRAM", $"BAR1 {Fmt.Num(f.Bar1MiB ?? 0)} MiB covers the VRAM");
            string prof = f.IsNvidia
                ? Loc.T(" O driver NVIDIA aplica o Resizable BAR por perfil de jogo, então nem todo título o usa.", " The NVIDIA driver applies Resizable BAR per game profile, so not every title uses it.")
                : "";
            string linkNote = link.ConfigLimited
                ? Loc.T(" Se o link PCIe estiver limitado, o que pede atenção é a geração do link PCIe, que é outro ajuste da BIOS (veja Link PCIe).",
                    " If the PCIe link is limited, what needs attention is the PCIe link generation, which is a different BIOS setting (see PCIe link).")
                : "";
            res.Find(Severity.Ok, Loc.T("Resizable BAR ativo", "Resizable BAR active"), ev,
                Loc.T("Está certo: o Resizable BAR já está ligado e cobre toda a VRAM.", "All good: Resizable BAR is already on and covers all of the VRAM.") + prof + linkNote, "",
                id: "analise.rebar",
                simple: Loc.T("Um recurso da BIOS que ajuda o processador a falar com a placa de vídeo já está ligado. Não precisa mexer em nada.",
                    "A BIOS feature that helps the processor talk to the graphics card is already on. Nothing needs to be changed.")
                    + (link.ConfigLimited ? Loc.T(" O ajuste que vale olhar é outro: a velocidade da conexão da placa de vídeo.", " The setting worth looking at is a different one: the speed of the graphics card connection.") : ""));
        }
        else if (f.ReBarActive == false)
        {
            bool gpuOk = gpu?.ReBar ?? f.GpuModern;
            bool cpuOk = cpu is null || (cpu.IsIntel ? cpu.Gen >= 10 : cpu.Gen >= 3);
            string ev = $"BAR1 = {Fmt.Num(f.Bar1MiB ?? 0)} MiB" + (f.GpuVramMiB is null ? "" : Loc.T($" com VRAM de {Fmt.Num(f.GpuVramMiB.Value)} MiB", $" with {Fmt.Num(f.GpuVramMiB.Value)} MiB of VRAM"));
            if (f.FirmwareUefi == false)
                res.Find(Severity.Info, Loc.T("Resizable BAR desligado (boot legado)", "Resizable BAR off (legacy boot)"), ev,
                    Lim(Loc.T("configuração de boot. O Resizable BAR exige boot UEFI e o sistema está em modo legado (CSM).",
                        "boot configuration. Resizable BAR requires UEFI boot and the system is in legacy mode (CSM).")),
                    Fix(Loc.T("converter o Windows para UEFI/GPT (MBR2GPT) e ativar Above 4G Decoding e Resizable BAR na BIOS; faça backup antes",
                            "convert Windows to UEFI/GPT (MBR2GPT) and enable Above 4G Decoding and Resizable BAR in the BIOS; back up first"), true,
                        Loc.T("varia por jogo; não medido.", "varies by game; not measured.")), id: "analise.rebar_legacy",
                    simple: Loc.T("O Windows está instalado no modo antigo de boot, que não permite o Resizable BAR. Mudar isso exige converter o disco; faça backup antes.",
                        "Windows is installed in the old boot mode, which does not allow Resizable BAR. Changing this requires converting the disk; back up first."));
            else if (!gpuOk || !cpuOk)
                res.Find(Severity.Info, Loc.T("Resizable BAR desligado", "Resizable BAR off"), ev,
                    Lim(Loc.T("hardware: a GPU ou a CPU podem não ter suporte oficial ao Resizable BAR. Sem evidência de que a plataforma suporte, o app não recomenda mexer.",
                        "hardware: the GPU or CPU may not officially support Resizable BAR. Without evidence that the platform supports it, the app does not recommend changing it.")),
                    Loc.T("O que dá para melhorar: nada confirmado (custo: n/a). Ganho esperado: n/a.", "What can be improved: nothing confirmed (cost: n/a). Expected gain: n/a."), id: "analise.rebar_unsupported",
                    simple: Loc.T("Seu hardware pode não ter suporte ao Resizable BAR. Não há nada confirmado para ajustar.",
                        "Your hardware may not support Resizable BAR. There is nothing confirmed to adjust."));
            else
                res.Find(Severity.Aviso, Loc.T("Resizable BAR desligado", "Resizable BAR off"), ev,
                    Lim(Loc.T("configuração da BIOS. A GPU e a CPU suportam o recurso, mas ele está desligado.", "BIOS configuration. The GPU and CPU support the feature, but it is off.")),
                    Fix(Loc.T("na BIOS, ativar Above 4G Decoding e Resizable BAR (exige BIOS com suporte e boot UEFI)", "in the BIOS, enable Above 4G Decoding and Resizable BAR (requires a BIOS with support and UEFI boot)"), true,
                        Loc.T("varia por jogo, de nenhum a poucos por cento; não medido aqui.", "varies by game, from none to a few percent; not measured here.")), "", 40,
                    id: "analise.rebar_off", guide: "ReBar",
                    simple: Loc.T("Um recurso da BIOS que ajuda o processador a falar com a placa de vídeo está desligado. Seu hardware suporta; ligar é grátis e pode dar um pequeno ganho.",
                        "A BIOS feature that helps the processor talk to the graphics card is off. Your hardware supports it; turning it on is free and may give a small gain."));
        }
        else
        {
            res.Note(Loc.T("Análise de Resizable BAR: BAR1 não lido (nvidia-smi ausente ou GPU não NVIDIA)",
                "Resizable BAR analysis: BAR1 not read (nvidia-smi missing or non-NVIDIA GPU)"));
        }
    }

    // ------------------------------------------------------------------ Equilíbrio

    /// <summary>Avalia o equilíbrio entre CPU e GPU. Limiares: CPU com até 4 núcleos e GPU de faixa intermediária (Tier 2) ou superior sugerem a CPU como primeiro limite; monitores de até 1920x1080 e de até 75,5 Hz indicam folga da GPU. É hipótese pelos dados estáticos (severidade Info), e a captura de stutter confirma ou descarta.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="cpu">CPU resolvida (ou null).</param>
    /// <param name="gpu">GPU resolvida (ou null).</param>
    private static void Balance(SectionResult res, SystemFacts f, CpuSpec? cpu, GpuSpec? gpu)
    {
        var g = res.Group(Loc.T("Equilíbrio do sistema", "System balance"));
        g.Add("CPU", f.CpuCores > 0 ? Loc.T($"{f.CpuCores} núcleos / {f.CpuThreads} threads{(cpu is null ? "" : $", {cpu.Family}")}",
            $"{f.CpuCores} cores / {f.CpuThreads} threads{(cpu is null ? "" : $", {cpu.Family}")}") : null);
        g.Add("GPU", string.IsNullOrWhiteSpace(f.GpuName) ? null : f.GpuName + (f.GpuVramMiB is { } v ? $" ({Fmt.Num(Math.Round(v / 1024d, 1))} GB)" : ""));

        var disp = f.Displays ?? new List<DisplayInfo>();
        string monitors = string.Join("; ", disp.Select(d => $"{d.Width}x{d.Height} @ {Fmt.Num(d.RefreshHz)} Hz{(string.IsNullOrWhiteSpace(d.Connection) ? "" : $" ({d.Connection})")}"));
        g.Add(Loc.T("Monitores", "Monitors"), disp.Count == 0 ? null : monitors);

        bool known = disp.Count > 0;
        bool lowRes = known && disp.All(d => (long)d.Width * d.Height <= 1920L * 1080);
        bool lowHz = known && disp.All(d => d.RefreshHz > 0 && d.RefreshHz <= 75.5);
        bool weakCpu = f.CpuCores is > 0 and <= 4;
        bool strongGpu = gpu is { Tier: >= 2 };
        if (gpu is null || f.CpuCores <= 0) return;

        if (weakCpu && strongGpu)
        {
            string gpuS = GpuShort(f.GpuName);
            string cpuS = Short(f.CpuName);
            string res1080 = lowRes ? Loc.T(" em 1080p ou menos", " at 1080p or lower") : "";
            string expl = Lim(Loc.T($"processador. Com {f.CpuCores}C/{f.CpuThreads}T e uma {gpuS}{res1080}, a CPU tende a ser o primeiro limite em jogos modernos (hipótese pelos dados estáticos; a captura de stutter confirma ou descarta).",
                $"processor. With {f.CpuCores}C/{f.CpuThreads}T and a {gpuS}{res1080}, the CPU tends to be the first limit in modern games (hypothesis from static data; the stutter capture confirms or rules it out)."));
            string capTxt = f.IsNvidia
                ? Loc.T("Painel NVIDIA > Taxa máxima de quadros, ou no próprio jogo", "NVIDIA Control Panel > Max Frame Rate, or in the game itself")
                : Loc.T("no próprio jogo ou no painel do driver da GPU", "in the game itself or in the GPU driver panel");
            string action;
            if (lowHz)
            {
                expl += Loc.T($" Como os monitores são de {Fmt.Num(disp.Max(d => d.RefreshHz), 0)} Hz ou menos, a GPU tem folga: quadros acima da taxa do monitor não aparecem na tela e só aumentam a carga da CPU.",
                    $" Since the monitors are {Fmt.Num(disp.Max(d => d.RefreshHz), 0)} Hz or less, the GPU has headroom: frames above the monitor refresh rate do not show on screen and only increase CPU load.");
                action = Fix(Loc.T($"limitar o FPS na taxa do monitor ({capTxt}) reduz a carga da CPU e os engasgos", $"capping FPS at the monitor refresh rate ({capTxt}) reduces CPU load and stutter"), true,
                    Loc.T("frametime mais estável; o FPS médio não sobe. Um monitor de taxa mais alta é upgrade (hardware), não correção de defeito, e só faz sentido com CPU que acompanhe. Para mais FPS o caminho é uma CPU de 6 núcleos ou mais (custo: hardware).",
                        "more stable frametime; average FPS does not go up. A higher refresh rate monitor is an upgrade (hardware), not a defect fix, and only makes sense with a CPU that keeps up. For more FPS the path is a CPU with 6 cores or more (cost: hardware)."));
            }
            else
            {
                action = Fix(Loc.T("acompanhar a carga de CPU na captura de stutter; limitar o FPS no jogo reduz a carga se a CPU estiver saturada",
                        "watch the CPU load in the stutter capture; capping FPS in the game reduces the load if the CPU is saturated"), true,
                    Loc.T("frametime mais estável; o FPS médio não sobe. Para mais FPS o caminho é uma CPU de 6 núcleos ou mais (custo: hardware).",
                        "more stable frametime; average FPS does not go up. For more FPS the path is a CPU with 6 cores or more (cost: hardware)."));
            }
            res.Find(Severity.Info, Loc.T("Equilíbrio: a CPU tende a limitar a GPU", "Balance: the CPU tends to limit the GPU"),
                Loc.T($"{f.CpuCores}C/{f.CpuThreads}T ({cpuS}) com {gpuS}" + (known ? $"; monitores {monitors}" : ""),
                    $"{f.CpuCores}C/{f.CpuThreads}T ({cpuS}) with {gpuS}" + (known ? $"; monitors {monitors}" : "")),
                expl, action, "", 40, id: "analise.balance_cpu",
                simple: Loc.T("Sua placa de vídeo é mais forte que o processador, então o processador tende a ser o limite em jogos. Limitar o FPS ajuda a deixar o jogo mais estável.",
                    "Your graphics card is stronger than the processor, so the processor tends to be the limit in games. Capping FPS helps make the game more stable."));
        }
        else
        {
            res.Find(Severity.Ok, Loc.T("Equilíbrio entre CPU e GPU", "CPU and GPU balance"),
                Loc.T($"{f.CpuCores}C/{f.CpuThreads}T com {GpuShort(f.GpuName)}: nenhum desequilíbrio evidente pelos dados estáticos",
                    $"{f.CpuCores}C/{f.CpuThreads}T with {GpuShort(f.GpuName)}: no evident imbalance from the static data"), id: "analise.balance");
        }
    }

    // ------------------------------------------------------------------ Fonte de alimentação

    /// <summary>Estima o consumo de pico (<see cref="PowerEstimator"/>) e, se o usuário informou a potência da fonte, compara com a recomendada. Sem esse dado só mostra a estimativa; nada é medido.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    private static void Psu(SectionResult res, SystemFacts f)
    {
        var est = PowerEstimator.Estimate(f);
        if (est is null)
        {
            res.Note(Loc.T("Análise da fonte: sem dados de CPU e GPU para estimar o consumo", "Power supply analysis: no CPU and GPU data to estimate consumption"));
            return;
        }
        var g = res.Group(Loc.T("Fonte de alimentação", "Power supply"));
        g.Add(Loc.T("Consumo de pico estimado", "Estimated peak consumption"),
            Loc.T($"{est.PeakW} W (CPU {est.CpuW} W + GPU {est.GpuW} W + resto {est.RestW} W)", $"{est.PeakW} W (CPU {est.CpuW} W + GPU {est.GpuW} W + rest {est.RestW} W)"));
        g.Add(Loc.T("Fonte recomendada", "Recommended power supply"), Loc.T($"{est.RecommendedW} W ou mais", $"{est.RecommendedW} W or more"));
        g.Add(Loc.T("Fonte informada", "Power supply entered"), f.PsuWatts is > 0 ? $"{f.PsuWatts} W" + (string.IsNullOrWhiteSpace(f.PsuModel) ? "" : $" ({f.PsuModel})") : null);
        // Sem fonte informada pelo usuário, usa a fonte digital detectada pelos sensores (potência deduzida do nome do modelo).
        bool detected = f.PsuWatts is not > 0 && f.PsuDetectedWatts is > 0;
        int? psuW = f.PsuWatts is > 0 ? f.PsuWatts : detected ? f.PsuDetectedWatts : null;
        if (detected)
            g.Add(Loc.T("Fonte detectada", "Power supply detected"),
                Loc.T($"{f.PsuDetectedWatts} W ({f.PsuDetectedModel}), pelos sensores da fonte digital", $"{f.PsuDetectedWatts} W ({f.PsuDetectedModel}), from the digital power supply sensors"));
        else if (f.PsuWatts is not > 0 && !string.IsNullOrWhiteSpace(f.PsuDetectedModel))
            g.Add(Loc.T("Fonte detectada", "Power supply detected"),
                Loc.T($"{f.PsuDetectedModel} (a potência não consta no nome do modelo)", $"{f.PsuDetectedModel} (the wattage is not in the model name)"));
        g.Notes.Add(est.Basis);

        string ev = Loc.T($"Pico estimado de {est.PeakW} W (CPU {est.CpuW} + GPU {est.GpuW} + resto {est.RestW}); recomendado {est.RecommendedW} W",
            $"Estimated peak of {est.PeakW} W (CPU {est.CpuW} + GPU {est.GpuW} + rest {est.RestW}); recommended {est.RecommendedW} W");
        if (psuW is not > 0)
        {
            res.Find(Severity.Info, Loc.T("Fonte de alimentação não informada", "Power supply not entered"), ev,
                Loc.T("Informe sua fonte em Configurações para checar. ", "Enter your power supply in Settings to check it. ") + est.Basis, "",
                id: "analise.psu",
                simple: Loc.T($"Pela estimativa, seu PC pode puxar cerca de {est.PeakW} W no pico e uma fonte de {est.RecommendedW} W ou mais é a indicada. Informe a sua fonte em Configurações para conferir.",
                    $"By the estimate, your PC may draw about {est.PeakW} W at peak and a power supply of {est.RecommendedW} W or more is recommended. Enter your power supply in Settings to check."));
            return;
        }
        int w = psuW!.Value;
        string evPsu = Loc.T($"{(detected ? $"Fonte detectada de {w} W ({f.PsuDetectedModel}, pelo nome do modelo)" : $"Fonte informada de {w} W")}; {ev}",
            $"{(detected ? $"Detected power supply of {w} W ({f.PsuDetectedModel}, from the model name)" : $"Power supply entered: {w} W")}; {ev}");
        if (w < est.PeakW)
            res.Find(Severity.Atencao, Loc.T("Fonte abaixo do consumo estimado", "Power supply below estimated consumption"), evPsu,
                Loc.T("A fonte informada é menor que o pico de consumo estimado. Sob carga pesada o PC pode desligar ou reiniciar sozinho. ",
                    "The power supply entered is smaller than the estimated peak consumption. Under heavy load the PC may shut down or restart on its own. ") + est.Basis,
                Loc.T($"O que dá para melhorar: trocar a fonte por uma de {est.RecommendedW} W ou mais, de boa marca (custo: hardware). Ganho esperado: estabilidade sob carga.",
                    $"What can be improved: replace the power supply with one of {est.RecommendedW} W or more, from a good brand (cost: hardware). Expected gain: stability under load."),
                "", 90, id: "analise.psu",
                simple: Loc.T($"A sua fonte ({w} W) parece pequena para este PC, que pode chegar a cerca de {est.PeakW} W. Pode haver desligamentos em jogos pesados; uma fonte de {est.RecommendedW} W ou mais é a indicada.",
                    $"Your power supply ({w} W) seems small for this PC, which can reach about {est.PeakW} W. There may be shutdowns in heavy games; a power supply of {est.RecommendedW} W or more is recommended."));
        else if (w < est.RecommendedW)
            res.Find(Severity.Aviso, Loc.T("Fonte com pouca folga", "Power supply with little headroom"), evPsu,
                Loc.T("A fonte informada cobre o pico estimado, mas com menos folga que a recomendada. Fontes trabalham melhor e duram mais abaixo de uns 70% da capacidade. ",
                    "The power supply entered covers the estimated peak, but with less headroom than recommended. Power supplies work better and last longer below about 70% of their capacity. ") + est.Basis,
                Loc.T($"O que dá para melhorar: considere uma fonte de {est.RecommendedW} W ou mais ao fazer upgrades (custo: hardware). Ganho esperado: mais folga e menos ruído.",
                    $"What can be improved: consider a power supply of {est.RecommendedW} W or more when upgrading (cost: hardware). Expected gain: more headroom and less noise."),
                "", 30, id: "analise.psu",
                simple: Loc.T($"A sua fonte ({w} W) aguenta o PC hoje, mas sem muita folga. Se for colocar uma placa de vídeo mais forte, pense em uma de {est.RecommendedW} W ou mais.",
                    $"Your power supply ({w} W) handles the PC today, but without much headroom. If you plan to add a stronger graphics card, consider one of {est.RecommendedW} W or more."));
        else
            res.Find(Severity.Ok, Loc.T("Fonte adequada", "Power supply adequate"), evPsu,
                Loc.T("A fonte informada cobre o pico estimado com folga. É uma estimativa, não uma medição.", "The power supply entered covers the estimated peak with headroom. This is an estimate, not a measurement."), "",
                id: "analise.psu",
                simple: Loc.T($"A sua fonte ({w} W) tem folga para este PC.", $"Your power supply ({w} W) has headroom for this PC."));
    }

    // ------------------------------------------------------------------ Upgrade

    /// <summary>Mostra o caminho de upgrade sem trocar a placa (se houver sugestão) e o ganho de PCIe e memória que ele traria, sempre com a condição a conferir antes de comprar.</summary>
    /// <param name="res">Seção em construção.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="cpu">CPU atual resolvida.</param>
    /// <param name="gpu">GPU resolvida (ou null).</param>
    /// <param name="up">Sugestão de upgrade.</param>
    /// <param name="link">Estado do link PCIe.</param>
    private static void Upgrade(SectionResult res, SystemFacts f, CpuSpec? cpu, GpuSpec? gpu, UpgradeSuggestion? up, LinkState link)
    {
        if (up is null || cpu is null) return;
        var g = res.Group(Loc.T("Caminho de upgrade", "Upgrade path"));
        int lanes = link.Lanes;
        int? gpuGen = Pos(f.GpuPcieGenMax) ?? gpu?.Gen;

        // Base de comparação: o que a plataforma atual entrega (ou o que o host reporta, se não houver tabela)
        int? curPlatform = gpuGen is null ? null : Math.Min(gpuGen.Value, cpu.PcieGen);
        int? newPlatform = gpuGen is null ? null : Math.Min(gpuGen.Value, up.PcieGen);
        string pcieGain = curPlatform is { } a && newPlatform is { } b && b > a && lanes > 0
            ? $"PCIe {GenS(a)} → {GenS(b)} ({BwNum(a, lanes)} → {Bw(b, lanes)} {Loc.T("em", "at")} x{lanes})"
            : "";
        int? curMax = cpu.MaxRamMTs(f.RamType);
        string ramGain = curMax is { } cm && up.RamMTs is { } nm && nm > cm
            ? Loc.T($"RAM oficial {up.RamType}-{cm} → {up.RamType}-{nm}", $"Official RAM {up.RamType}-{cm} → {up.RamType}-{nm}")
            : "";
        string coreGain = f.CpuCores > 0 && up.Cores > f.CpuCores ? $"{f.CpuCores}C/{f.CpuThreads}T → {up.Cores}C/{up.Threads}T" : "";
        var gains = new[] { coreGain, pcieGain, ramGain }.Where(s => s.Length > 0).ToList();

        g.Add(Loc.T("CPU atual", "Current CPU"), $"{Short(f.CpuName)}: {cpu.Family}, {cpu.Socket}");
        g.Add(Loc.T("Sugestão (mesma placa)", "Suggestion (same board)"), $"{up.Cpu} ({up.Cores}C/{up.Threads}T, {up.Socket})");
        g.Add(Loc.T("PCIe com a CPU sugerida", "PCIe with the suggested CPU"),
            Loc.T($"{GenS(up.PcieGen)} no slot x16 (limitado também pelo chipset)", $"{GenS(up.PcieGen)} on the x16 slot (also limited by the chipset)"));
        g.Add(Loc.T("Memória oficial", "Official memory"), up.RamMTs is null ? null : $"{up.RamType}-{up.RamMTs}");
        g.Add(Loc.T("Exige", "Requires"), up.Requirement);

        string gainTxt = gains.Count > 0
            ? string.Join("; ", gains) + Loc.T(". O ganho em jogos depende do título e não foi medido.", ". The gain in games depends on the title and was not measured.")
            : Loc.T("ganho depende do jogo; não medido.", "gain depends on the game; not measured.");
        res.Find(Severity.Info, Loc.T("Caminho de upgrade sem trocar a placa", "Upgrade path without replacing the board"),
            $"{Short(f.CpuName)} → {up.Cpu}",
            Lim(Loc.T($"processador e plataforma de soquete {cpu.Socket}. A mesma placa aceita {up.Cpu} ({up.Cores}C/{up.Threads}T). {up.Requirement}",
                $"processor and {cpu.Socket} socket platform. The same board accepts {up.Cpu} ({up.Cores}C/{up.Threads}T). {up.Requirement}")),
            Fix(Loc.T($"trocar a CPU por {up.Cpu}, mantendo placa e memória", $"replace the CPU with {up.Cpu}, keeping the board and memory"), false, gainTxt),
            id: "analise.upgrade",
            simple: Loc.T($"Dá para melhorar o PC trocando só o processador por um {up.Cpu}, sem trocar a placa-mãe. É um gasto de hardware e o ganho depende do jogo.",
                $"The PC can be improved by replacing only the processor with a {up.Cpu}, without replacing the motherboard. It is a hardware expense and the gain depends on the game."));
    }
}
