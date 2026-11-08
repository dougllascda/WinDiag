using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Memória: capacidade, disponibilidade, pagefile, pentes (Win32_PhysicalMemory) e recursos do MMAgent.
/// Fontes: WMI (Win32_PhysicalMemory, PhysicalMemoryArray, PageFileUsage, contadores de memória, OperatingSystem).
/// MMAgent: como administrador, o estado configurado (MemoryCompression, PageCombining, ApplicationPreLaunch) é lido por
/// <c>Get-MMAgent</c> via powershell.exe (com timeout); sem administrador, ou se a leitura falhar, a compressão é inferida
/// pela existência do processo de sistema "Memory Compression" (reserva) e os outros dois campos ficam "n/d".
/// Somente leitura; falhas viram notas.
/// </summary>
public sealed class MemoryCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "memoria";
    /// <inheritdoc/>
    public string Title => Loc.T("Memória", "Memory");

    /// <summary>Converte a saída de <c>MemoryCompression|PageCombining|ApplicationPreLaunch</c> (valores True/False, sem cultura) em booleanos; campo ilegível vira null.</summary>
    internal static (bool? Compression, bool? PageCombining, bool? PreLaunch) ParseMmAgent(string? stdout)
    {
        var line = (stdout ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Count(c => c == '|') == 2);
        if (line is null) return (null, null, null);
        var p = line.Split('|');
        static bool? B(string s) => s.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : s.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
        return (B(p[0].Trim()), B(p[1].Trim()), B(p[2].Trim()));
    }

    /// <summary>
    /// Chave do canal de memória a partir de DeviceLocator/BankLabel: "Controller1-ChannelA" quando há controlador, "ChannelA" quando só há canal.
    /// Devolve null se o padrão não é reconhecido (nesse caso não se afirma nada sobre dual channel).
    /// </summary>
    /// <param name="deviceLocator">Win32_PhysicalMemory.DeviceLocator (ex.: "Controller0-ChannelA-DIMM0").</param>
    /// <param name="bankLabel">Win32_PhysicalMemory.BankLabel (ex.: "BANK 0").</param>
    internal static string? ChannelKey(string? deviceLocator, string? bankLabel)
    {
        var text = $"{deviceLocator} {bankLabel}";
        var m = Regex.Match(text, @"Controller\s*(\d+)\W*Channel\s*([A-Z0-9])\b", RegexOptions.IgnoreCase);
        if (m.Success) return $"Controller{m.Groups[1].Value}-Channel{m.Groups[2].Value.ToUpperInvariant()}";
        m = Regex.Match(text, @"Channel\s*([A-Z0-9])\b", RegexOptions.IgnoreCase);
        return m.Success ? $"Channel{m.Groups[1].Value.ToUpperInvariant()}" : null;
    }

    /// <summary>true = pentes em canais distintos; false = pente único ou todos no mesmo canal; null = indeterminado (canais não reconhecidos).</summary>
    /// <param name="sticks">Quantidade de pentes.</param>
    /// <param name="distinctChannels">Canais distintos reconhecidos.</param>
    /// <param name="allRecognized">true se todos os pentes tiveram o canal reconhecido.</param>
    internal static bool? DualChannelFrom(int sticks, int distinctChannels, bool allRecognized) =>
        sticks == 1 ? false
        : sticks >= 2 && allRecognized && distinctChannels >= 2 ? true
        : sticks >= 2 && allRecognized && distinctChannels == 1 ? false
        : null;

    private static (bool? Compression, bool? PageCombining, bool? PreLaunch) ReadMmAgent(SectionResult res)
    {
        const string cmd = "$a = Get-MMAgent; \"$($a.MemoryCompression)|$($a.PageCombining)|$($a.ApplicationPreLaunch)\"";
        var r = Proc.Run(Proc.Sys32(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -Command \"" + cmd.Replace("\"", "\\\"") + "\"", 20000);
        if (!r.Ok) { res.Note("Get-MMAgent: " + (r.TimedOut ? Loc.T("tempo esgotado", "timed out") : Loc.T("sem resposta (" + r.StdErr.Trim() + ")", "no response (" + r.StdErr.Trim() + ")"))); return (null, null, null); }
        var parsed = ParseMmAgent(r.StdOut);
        if (parsed.Compression is null) res.Note(Loc.T("Get-MMAgent: saída não reconhecida", "Get-MMAgent: unrecognized output"));
        return parsed;
    }

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        void Err(string s) => res.Note(s);

        var sticks = new List<System.Management.ManagementBaseObject>();
        System.Management.ManagementBaseObject? arr = null, memPerf = null, pf = null;
        sticks = Wmi.Query("SELECT * FROM Win32_PhysicalMemory", onError: Err);
        arr = Wmi.First("SELECT * FROM Win32_PhysicalMemoryArray", onError: Err);
        memPerf = Wmi.First("SELECT AvailableMBytes, PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory", onError: Err);
        pf = Wmi.First("SELECT * FROM Win32_PageFileUsage", onError: Err);

        double total = facts.RamGB;
        if (total <= 0)
        {
            var os = Wmi.First("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem", onError: Err);
            var kb = os?.L("TotalVisibleMemorySize");
            if (kb is > 0) total = Math.Round(kb.Value / 1048576d, 2);
            facts.RamGB = total;
        }

        double avail = 0;
        var availMb = memPerf?.D("AvailableMBytes");
        if (availMb is not null) avail = Math.Round(availMb.Value / 1024d, 2);
        else
        {
            res.Note(Loc.T("Contadores de memória não lidos - disponibilidade de RAM incompleta", "Memory counters not read - RAM availability incomplete"));
            var os = Wmi.First("SELECT FreePhysicalMemory FROM Win32_OperatingSystem", onError: Err);
            var free = os?.L("FreePhysicalMemory");
            if (free is not null) avail = Math.Round(free.Value / 1048576d, 2);
        }
        double availPct = total > 0 ? Math.Round(avail / total * 100, 1) : 0;
        facts.AvailGB = avail;
        facts.AvailPct = availPct;

        // Compressão de memória (ver nota da classe): configurado via Get-MMAgent como administrador; processo como reserva.
        bool? mmc = null, pageComb = null, preLaunch = null;
        bool fromAgent = false;
        if (facts.IsAdmin)
        {
            try { (mmc, pageComb, preLaunch) = ReadMmAgent(res); fromAgent = mmc is not null; }
            catch (Exception ex) { res.Note("Get-MMAgent: " + ex.Message.Trim()); }
        }
        if (mmc is null)
        {
            try
            {
                var ps = Process.GetProcessesByName("Memory Compression");
                mmc = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
            }
            catch (Exception) { res.Note(Loc.T("Compressão de memória: processo \"Memory Compression\" não verificado", "Memory compression: \"Memory Compression\" process not checked")); }
        }
        facts.MemCompression = mmc;

        var g = res.Group(Loc.T("Memória", "Memory"));
        var totalLabel = Loc.T("Total / Disponível", "Total / Available");
        g.Add(totalLabel, $"{Fmt.Num(total)} GB / {Fmt.Num(avail)} GB ({Fmt.Num(availPct, 1)}%)");
        if (arr is not null)
        {
            var maxKb = arr.L("MaxCapacity");
            g.Add(Loc.T("Slots / Capacidade máxima", "Slots / Maximum capacity"), $"{arr.L("MemoryDevices")} slots / {(maxKb is null ? Fmt.NA : Math.Round(maxKb.Value / 1048576d).ToString("0", Fmt.Culture))} GB");
        }
        if (pf is not null)
            g.Add("Pagefile", Loc.T($"{pf.S("Name")} | alocado {pf.L("AllocatedBaseSize")} MB | uso {pf.L("CurrentUsage")} MB | pico {pf.L("PeakUsage")} MB", $"{pf.S("Name")} | allocated {pf.L("AllocatedBaseSize")} MB | in use {pf.L("CurrentUsage")} MB | peak {pf.L("PeakUsage")} MB"));
        if (memPerf is not null)
            g.Add(Loc.T("Páginas lidas do disco/s", "Pages read from disk/s"), $"{memPerf.L("PagesInputPersec")} (hard faults)");
        g.Add(Loc.T("Compressão de memória (MMAgent)", "Memory compression (MMAgent)"), mmc is null ? null
            : fromAgent ? (mmc.Value ? Loc.T("ligada (configuração do MMAgent)", "on (MMAgent setting)") : Loc.T("desligada (configuração do MMAgent)", "off (MMAgent setting)"))
            : mmc.Value ? Loc.T("ligada (processo \"Memory Compression\" ativo)", "on (\"Memory Compression\" process running)") : Loc.T("desligada (processo \"Memory Compression\" ausente)", "off (\"Memory Compression\" process absent)"));
        if (pageComb is not null) g.Add(Loc.T("Combinação de páginas (PageCombining)", "Page combining (PageCombining)"), pageComb.Value ? Loc.T("ligada", "on") : Loc.T("desligada", "off"));
        if (preLaunch is not null) g.Add(Loc.T("Pré-carregamento de aplicativos (ApplicationPreLaunch)", "App pre-launch (ApplicationPreLaunch)"), preLaunch.Value ? Loc.T("ligado", "on") : Loc.T("desligado", "off"));

        // Tabela de pentes
        var t = g.Table(Loc.T("Pentes de memória", "Memory modules"), "Slot", Loc.T("Tamanho", "Size"), Loc.T("Tipo", "Type"), Loc.T("Nominal", "Rated"), Loc.T("Atual", "Current"), Loc.T("Tensão", "Voltage"), Loc.T("Fabricante", "Manufacturer"), "Part number", Loc.T("Série", "Serial"), Loc.T("Part number decodificado", "Decoded part number"), "Rank");
        var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unrecognized = 0;
        double installedBytes = 0;
        foreach (var s in sticks)
        {
            try
            {
                var type = (int)(s.L("SMBIOSMemoryType") ?? 0) switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", var x => Loc.T("tipo ", "type ") + x };
                var ff = (int)(s.L("FormFactor") ?? 0) switch { 8 => "DIMM", 12 => "SODIMM", var x => Loc.T("forma ", "form ") + x };
                var cap = s.L("Capacity");
                var volt = s.L("ConfiguredVoltage");
                var rankV = s.L("Attributes");
                var pn = s.S("PartNumber");
                var speed = s.L("Speed"); var cfg = s.L("ConfiguredClockSpeed");
                t.AddRow(s.S("DeviceLocator"),
                    cap is null ? null : $"{Math.Round(cap.Value / 1073741824d):0} GB",
                    $"{type} {ff}",
                    speed is null ? null : $"{speed} MHz",
                    cfg is null ? null : $"{cfg} MHz",
                    volt is > 0 ? Fmt.Num(volt.Value / 1000d) + " V" : null,
                    s.S("Manufacturer"), pn, s.S("SerialNumber"),
                    RamPart.Decode(pn),
                    rankV is > 0 ? $"rank {rankV}" : $"rank {Fmt.NA}");
                var key = ChannelKey(s.S("DeviceLocator"), s.S("BankLabel"));
                if (key is null) unrecognized++;
                else channels.Add(key);
                if (cap is > 0) installedBytes += cap.Value;
            }
            catch { unrecognized++; res.Note(Loc.T("Um pente de memória não pôde ser lido por completo", "A memory module could not be read completely")); }
        }
        if (sticks.Count == 0) res.Note(Loc.T("Win32_PhysicalMemory não retornou pentes", "Win32_PhysicalMemory returned no modules"));
        // Padrão de slot não reconhecido em algum pente: não dá para afirmar nada sobre os canais.
        bool channelsKnown = sticks.Count > 0 && unrecognized == 0;
        if (installedBytes > 0)
        {
            facts.RamInstalledGB = Math.Round(installedBytes / 1073741824d, 2);
            g.Rows.RemoveAll(r => r.Label == totalLabel);
            g.Rows.Insert(0, new InfoRow { Label = totalLabel, Value = Loc.T($"{Fmt.Num(facts.RamInstalledGB)} GB instalados ({Fmt.Num(total)} GB visíveis ao Windows) / {Fmt.Num(avail)} GB disponíveis ({Fmt.Num(availPct, 1)}%)", $"{Fmt.Num(facts.RamInstalledGB)} GB installed ({Fmt.Num(total)} GB visible to Windows) / {Fmt.Num(avail)} GB available ({Fmt.Num(availPct, 1)}%)") });
        }
        double ramThr = facts.RamForThresholdGB;

        // Fatos para a Análise da plataforma
        try
        {
            facts.RamModules = sticks.Count;
            facts.RamChannels = channelsKnown && channels.Count > 0 ? channels.Count : null;
            facts.DualChannel = DualChannelFrom(sticks.Count, channels.Count, channelsKnown);
            var cfgs = sticks.Select(s => s.L("ConfiguredClockSpeed")).Where(v => v is > 0).Select(v => (int)v!.Value).ToList();
            facts.RamConfiguredMTs = cfgs.Count > 0 ? cfgs.Max() : null;
            var rated = new List<int>();
            foreach (var s in sticks)
            {
                if (s.L("Speed") is > 0 and var sp) rated.Add((int)sp);
                var dec = RamPart.Decode(s.S("PartNumber"));
                var mm = dec is null ? null : Regex.Match(dec, @"(\d{4})\s*MT/s");
                if (mm is { Success: true }) rated.Add(int.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            facts.RamRatedMTs = rated.Count > 0 ? rated.Max() : null;
            var types = sticks.Select(s => (int)(s.L("SMBIOSMemoryType") ?? 0)).Where(x => x > 0).ToList();
            facts.RamType = types.Count > 0 ? types[0] switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", _ => null } : null;
        }
        catch { res.Note(Loc.T("Fatos de memória (velocidade/canais) não preenchidos por completo", "Memory facts (speed/channels) not fully populated")); }

        // Verificações
        if (sticks.Count >= 2 && channelsKnown && channels.Count >= 2)
            res.Find(Severity.Ok, "Dual channel", Loc.T($"{sticks.Count} pentes em {channels.Count} canais distintos", $"{sticks.Count} modules on {channels.Count} distinct channels"), id: "memoria.dual_channel");
        else if (sticks.Count >= 2 && channelsKnown && channels.Count == 1)
            res.Find(Severity.Atencao, Loc.T("Pentes no mesmo canal", "Modules on the same channel"), Loc.T("Dois pentes ligados ao mesmo canal", "Two modules connected to the same channel"),
                Loc.T("Isso reduz a largura de banda pela metade e prejudica desempenho em CPU e GPU integrada.", "This halves the bandwidth and hurts performance on the CPU and integrated GPU."),
                Loc.T("Mude os pentes para os slots recomendados no manual da placa-mãe (geralmente A2 e B2).", "Move the modules to the slots recommended in the motherboard manual (usually A2 and B2)."),
                id: "memoria.same_channel",
                simple: Loc.T("Os dois pentes de memória estão no par de encaixes errado, e a memória trabalha com metade da velocidade possível. Trocar de encaixe resolve, sem gastar nada.", "The two memory modules are in the wrong pair of slots, and the memory runs at half its possible speed. Moving them to other slots fixes it at no cost."));
        else if (sticks.Count == 1)
            res.Find(Severity.Aviso, "Single channel", Loc.T("Apenas 1 pente instalado", "Only 1 module installed"),
                Loc.T("Com um único pente a memória usa metade da largura de banda possível.", "With a single module the memory uses half of the possible bandwidth."),
                Loc.T("Adicione um segundo pente igual para ativar o dual channel.", "Add a second identical module to enable dual channel."),
                id: "memoria.single_channel",
                simple: Loc.T("Só há um pente de memória instalado, o que limita a velocidade da memória pela metade. Um segundo pente igual resolve.", "Only one memory module is installed, which halves the memory speed. A second identical module fixes it."));
        else if (sticks.Count >= 2)
            res.Find(Severity.Info, Loc.T("Canais de memória", "Memory channels"), Loc.T("Não foi possível identificar os canais pelo nome dos slots", "Could not identify the channels from the slot names"),
                "", Loc.T("Confirme o dual channel com um programa como CPU-Z (aba Memory).", "Confirm dual channel with a program such as CPU-Z (Memory tab)."), id: "memoria.channels_unknown");

        var slow = sticks.FirstOrDefault(s => s.L("Speed") is > 0 && s.L("ConfiguredClockSpeed") is > 0 && s.L("ConfiguredClockSpeed") < s.L("Speed"));
        if (slow is not null)
            res.Find(Severity.Aviso, Loc.T("RAM abaixo da velocidade nominal", "RAM below rated speed"), Loc.T($"Rodando a {slow.L("ConfiguredClockSpeed")} MHz, nominal {slow.L("Speed")} MHz", $"Running at {slow.L("ConfiguredClockSpeed")} MHz, rated {slow.L("Speed")} MHz"),
                Loc.T("O perfil XMP/DOCP está desligado ou o processador/chipset limita a frequência. Memória mais lenta reduz desempenho, principalmente em CPUs que dependem dela.", "The XMP/DOCP profile is off or the processor/chipset limits the frequency. Slower memory reduces performance, especially on CPUs that depend on it."),
                Loc.T("Confira na BIOS se o XMP está ativo e qual é a frequência máxima suportada pelo seu processador e chipset.", "Check in the BIOS whether XMP is enabled and what the maximum frequency supported by your processor and chipset is."),
                id: "memoria.below_rated",
                simple: Loc.T("Sua memória está rodando mais devagar do que foi feita para rodar. Normalmente isso se corrige ligando o perfil XMP na BIOS.", "Your memory is running slower than it was built to. This is usually fixed by enabling the XMP profile in the BIOS."), guide: "Xmp");
        else if (sticks.Count > 0)
            res.Find(Severity.Ok, Loc.T("Frequência da RAM", "RAM frequency"), Loc.T($"{sticks[0].L("ConfiguredClockSpeed")} MHz (igual ao nominal)", $"{sticks[0].L("ConfiguredClockSpeed")} MHz (same as rated)"), id: "memoria.ram_speed");

        if (total > 0 && (availPct < 20 || avail < 3))
            res.Find(Severity.Aviso, Loc.T("Pouca memória disponível", "Low available memory"), Loc.T($"{Fmt.Num(avail)} GB disponíveis ({Fmt.Num(availPct, 1)}%)", $"{Fmt.Num(avail)} GB available ({Fmt.Num(availPct, 1)}%)"),
                Loc.T("O valor já inclui o cache que o Windows libera sob demanda, então é memória realmente livre. Com o jogo aberto sobra menos ainda; quando falta RAM, o Windows passa a usar o disco como memória, o que causa engasgos fortes.", "The value already includes the cache that Windows releases on demand, so it is really free memory. With the game open even less remains; when RAM runs out, Windows starts using the disk as memory, which causes heavy stutters."),
                Loc.T("Feche programas pesados (navegador, IDEs, launchers) antes de jogar. A seção de processos lista os maiores consumidores.", "Close heavy programs (browser, IDEs, launchers) before playing. The processes section lists the biggest consumers."), "", 80,
                id: "memoria.low_available",
                simple: Loc.T("Sobra pouca memória livre. Quando ela acaba, o Windows usa o disco no lugar e o jogo pode travar. Feche programas antes de jogar.", "Little free memory is left. When it runs out, Windows uses the disk instead and the game may freeze. Close programs before playing."));
        else if (total > 0)
            res.Find(Severity.Ok, Loc.T("Memória disponível", "Available memory"), Loc.T($"{Fmt.Num(avail)} GB ({Fmt.Num(availPct, 1)}%) - inclui cache liberável", $"{Fmt.Num(avail)} GB ({Fmt.Num(availPct, 1)}%) - includes releasable cache"), id: "memoria.available");

        if (mmc is not null)
        {
            string ramTxt = facts.RamInstalledGB > 0 ? Loc.T($"{Fmt.Num(facts.RamInstalledGB)} GB de RAM instalada", $"{Fmt.Num(facts.RamInstalledGB)} GB of installed RAM") : Loc.T($"{Fmt.Num(total)} GB de RAM visível", $"{Fmt.Num(total)} GB of visible RAM");
            bool lowMem = ramThr <= 16 || avail < 3 || availPct < 20;
            if (!mmc.Value && lowMem)
                res.Find(Severity.Aviso, Loc.T("Compressão de memória desligada", "Memory compression off"), Loc.T($"{ramTxt}, {Fmt.Num(avail)} GB livres ({Fmt.Num(availPct, 1)}%)", $"{ramTxt}, {Fmt.Num(avail)} GB free ({Fmt.Num(availPct, 1)}%)"),
                    Loc.T("Com pouca RAM livre, sem a compressão o Windows passa a paginar no disco, o que é bem mais lento e pode causar engasgos em jogos.", "With little free RAM, without compression Windows starts paging to disk, which is much slower and can cause stutters in games."),
                    Loc.T("Ligue a compressão de memória (MMAgent).", "Turn on memory compression (MMAgent)."), "MMC_ON", 75,
                    id: "memoria.compression_off",
                    simple: Loc.T("Com pouca memória, uma função do Windows que \"comprime\" dados na RAM ajuda a evitar travadas. Ela está desligada.", "With little memory, a Windows feature that \"compresses\" data in RAM helps avoid freezes. It is turned off."));
            else if (mmc.Value && ramThr >= 32 && availPct >= 40)
                res.Find(Severity.Info, Loc.T("Compressão de memória ativa", "Memory compression on"), Loc.T($"{ramTxt}, {Fmt.Num(availPct, 1)}% livre", $"{ramTxt}, {Fmt.Num(availPct, 1)}% free"),
                    Loc.T("Com bastante RAM livre, a compressão gasta CPU sem necessidade. O ganho de desligar é pequeno.", "With plenty of free RAM, compression spends CPU needlessly. The gain from turning it off is small."), "", "MMC",
                    id: "memoria.compression_on",
                    simple: Loc.T("Há bastante memória livre, então a compressão de memória trabalha à toa. Desligar dá um ganho pequeno.", "There is plenty of free memory, so memory compression works for nothing. Turning it off gives a small gain."));
            else
                res.Find(Severity.Ok, Loc.T("Compressão de memória", "Memory compression"), mmc.Value ? Loc.T("Ligada (adequado para a RAM disponível)", "On (appropriate for the available RAM)") : Loc.T("Desligada (RAM folgada)", "Off (RAM is ample)"), id: "memoria.compression");
        }
        return res;
    }
}
