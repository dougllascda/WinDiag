using System.Text.RegularExpressions;

namespace WinDiag.Core.Knowledge;

/// <summary>
/// Tabelas internas de plataforma (CPU, chipset, GPU) usadas pela "Análise da plataforma".
/// Fontes das especificações: Intel ARK (ark.intel.com: geração PCIe do processador e memória oficial),
/// páginas de produto AMD (Ryzen e chipsets AM4/AM5) e especificações públicas de NVIDIA, AMD e Intel Arc
/// (geração e largura do link das placas de vídeo).
/// Política: ser conservador. Se o nome não for reconhecido, a resolução devolve null e as regras
/// dizem apenas o que foi medido, sem inferir limites. Mobile (sufixos H/U/P/Y/G#) não é resolvido.
/// </summary>
public static class PlatformDb
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ------------------------------------------------------------------ CPU

    /// <summary>
    /// Especificação de uma família de CPU de desktop.
    /// Gen: geração Intel (6 a 14) ou série AMD (1 = Ryzen 1000, 5 = Ryzen 5000...).
    /// PcieGen: geração PCIe que a CPU entrega ao slot x16 da GPU.
    /// PcieDependsOnChipset: o valor final também depende do chipset/placa (AMD AM4/AM5 e Intel LGA1700).
    /// Ddr4MTs / Ddr5MTs: velocidade oficial máxima da memória em MT/s (null = tipo não suportado).
    /// </summary>
    public sealed record CpuSpec(string Vendor, string Family, int Gen, string Socket, int PcieGen,
        bool PcieDependsOnChipset, int? Ddr4MTs, int? Ddr5MTs, int? Cores = null, int? Threads = null)
    {
        /// <summary>TDP (potência base) em watts da CPU resolvida; null se o modelo exato não consta da tabela.</summary>
        public int? TdpW { get; init; }

        /// <summary>true se o fabricante é Intel.</summary>
        public bool IsIntel => Vendor == "Intel";

        /// <summary>Rótulo curto da geração no idioma atual (ex.: "10ª geração", "Ryzen série 5000"; em inglês "10th gen", "Ryzen 5000 series").</summary>
        public string GenLabel => IsIntel ? Loc.T($"{Gen}ª geração", $"{Gen}th gen") : Loc.T($"Ryzen série {Gen}000", $"Ryzen {Gen}000 series");

        /// <summary>Máximo oficial de RAM para o tipo informado. Sem tipo, só responde se a CPU aceita um único tipo.</summary>
        public int? MaxRamMTs(string? ramType)
        {
            if (string.Equals(ramType, "DDR5", StringComparison.OrdinalIgnoreCase)) return Ddr5MTs;
            if (string.Equals(ramType, "DDR4", StringComparison.OrdinalIgnoreCase)) return Ddr4MTs;
            if (Ddr4MTs is not null && Ddr5MTs is null) return Ddr4MTs;
            if (Ddr5MTs is not null && Ddr4MTs is null) return Ddr5MTs;
            return null;
        }
    }

    private static readonly Regex IntelRx = new(@"\bi([3579])-(\d{4,5})([A-Z]{0,3}\d?)\b", Rx);
    private static readonly Regex IntelMobileSuffix = new(@"^(H|U|P|Y|G\d)", Rx);
    private static readonly Regex AmdRx = new(@"Ryzen\s+[3579]\s+(?:PRO\s+)?(\d{4})([A-Z0-9]*)", Rx);
    private static readonly Regex AmdMobileSuffix = new(@"^(U|H|C)", Rx);

    /// <summary>Resolve o nome do processador (ex.: "Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz"). Null se não reconhecido.</summary>
    public static CpuSpec? ResolveCpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var mi = IntelRx.Match(name);
            if (mi.Success) return ResolveIntel(mi) is { } si ? si with { TdpW = IntelTdp(mi) } : null;
            var ma = AmdRx.Match(name);
            if (ma.Success) return ResolveAmd(ma) is { } sa ? sa with { TdpW = AmdTdp(ma) } : null;
        }
        catch { }
        return null;
    }

    /// <summary>TDP (PL1) de desktops Intel pelo sufixo: T = 35 W, K/KF/KS = 125 W (150 W nos KS de 9ª geração), demais = 65 W.</summary>
    private static int? IntelTdp(Match m)
    {
        string suffix = m.Groups[3].Value.ToUpperInvariant();
        if (suffix.StartsWith('T')) return 35;
        if (suffix.StartsWith('K')) return 125;
        if (suffix is "" or "F" or "X") return suffix == "X" ? null : 65;
        return null;
    }

    private static readonly Dictionary<string, int> AmdTdpTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1600"] = 65, ["1600X"] = 95, ["2600"] = 65, ["2600X"] = 95, ["3600"] = 65, ["3600X"] = 95, ["3700X"] = 65, ["3800X"] = 105,
        ["3900X"] = 105, ["3950X"] = 105, ["5500"] = 65, ["5600"] = 65, ["5600X"] = 65, ["5600G"] = 65, ["5700X"] = 65, ["5700G"] = 65,
        ["5800X"] = 105, ["5800X3D"] = 105, ["5900X"] = 105, ["5950X"] = 105, ["7500F"] = 65, ["7600"] = 65, ["7600X"] = 105, ["7700"] = 65,
        ["7700X"] = 105, ["7800X3D"] = 120, ["7900"] = 65, ["7900X"] = 170, ["7950X"] = 170, ["7950X3D"] = 120, ["9600X"] = 65,
        ["9700X"] = 65, ["9800X3D"] = 120, ["9900X"] = 120, ["9950X"] = 170,
    };

    private static int? AmdTdp(Match m) => AmdTdpTable.TryGetValue(m.Groups[1].Value + m.Groups[2].Value, out var w) ? w : null;

    private static CpuSpec? ResolveIntel(Match m)
    {
        string tier = m.Groups[1].Value;                 // 3, 5, 7, 9
        string digits = m.Groups[2].Value;
        string suffix = m.Groups[3].Value;
        if (IntelMobileSuffix.IsMatch(suffix)) return null;   // notebooks: limites dependem do projeto, não tratados
        int gen = digits.Length == 5 ? int.Parse(digits[..2]) : int.Parse(digits[..1]);
        bool k = suffix.StartsWith("K", StringComparison.OrdinalIgnoreCase);
        bool highTier = tier is "7" or "9";
        string Fam(string code) => Loc.T($"{code} ({gen}ª geração)", $"{code} (gen {gen})");

        return gen switch
        {
            6 => new CpuSpec("Intel", Fam("Skylake"), 6, "LGA1151", 3, false, 2133, null),
            7 => new CpuSpec("Intel", Fam("Kaby Lake"), 7, "LGA1151", 3, false, 2400, null),
            // i3 de 8ª e 9ª geração: DDR4-2400 oficial (ARK); demais: 2666.
            8 => new CpuSpec("Intel", Fam("Coffee Lake"), 8, "LGA1151", 3, false, tier == "3" ? 2400 : 2666, null),
            9 => new CpuSpec("Intel", Fam("Coffee Lake Refresh"), 9, "LGA1151", 3, false, tier == "3" ? 2400 : 2666, null),
            // 10ª: i3/i5 DDR4-2666; i7/i9 DDR4-2933.
            10 => new CpuSpec("Intel", Fam("Comet Lake"), 10, "LGA1200", 3, false, highTier ? 2933 : 2666, null),
            11 => new CpuSpec("Intel", Fam("Rocket Lake"), 11, "LGA1200", 4, false, 3200, null),
            // 12ª a 14ª: PCIe 5.0 no x16 da CPU, mas a geração efetiva depende da placa.
            12 => new CpuSpec("Intel", Fam("Alder Lake"), 12, "LGA1700", 5, true, 3200, 4800),
            13 => new CpuSpec("Intel", Fam("Raptor Lake"), 13, "LGA1700", 5, true, 3200, (tier == "5" && k) || highTier ? 5600 : 4800),
            14 => new CpuSpec("Intel", Fam("Raptor Lake Refresh"), 14, "LGA1700", 5, true, 3200, (tier == "5" && k) || highTier ? 5600 : 4800),
            _ => null
        };
    }

    private static CpuSpec? ResolveAmd(Match m)
    {
        int n = int.Parse(m.Groups[1].Value);
        string suffix = m.Groups[2].Value;
        if (AmdMobileSuffix.IsMatch(suffix)) return null;
        bool g = suffix.Contains('G', StringComparison.OrdinalIgnoreCase);
        bool hasX = suffix.StartsWith("X", StringComparison.OrdinalIgnoreCase);
        int series = n / 1000;

        return series switch
        {
            1 => new CpuSpec("AMD", "Zen (Ryzen 1000)", 1, "AM4", 3, false, 2666, null),
            2 => new CpuSpec("AMD", "Zen+ (Ryzen 2000)", 2, "AM4", 3, false, 2933, null),
            3 when g => new CpuSpec("AMD", Loc.T("Zen+ com gráficos (Ryzen 3000G)", "Zen+ with graphics (Ryzen 3000G)"), 3, "AM4", 3, false, 2933, null),
            3 => new CpuSpec("AMD", "Zen 2 (Ryzen 3000)", 3, "AM4", 4, true, 3200, null),
            4 => new CpuSpec("AMD", "Zen 2 (Ryzen 4000)", 4, "AM4", 3, false, 3200, null),
            // 5500, 5700 (sem X) e versões G usam o núcleo Cezanne, limitado a PCIe 3.0.
            5 when g || (!hasX && n is 5500 or 5700 && suffix.Length == 0) =>
                new CpuSpec("AMD", Loc.T("Zen 3 com gráficos (Ryzen 5000G/5500/5700)", "Zen 3 with graphics (Ryzen 5000G/5500/5700)"), 5, "AM4", 3, false, 3200, null),
            5 => new CpuSpec("AMD", "Zen 3 (Ryzen 5000)", 5, "AM4", 4, true, 3200, null),
            7 => new CpuSpec("AMD", "Zen 4 (Ryzen 7000)", 7, "AM5", 5, true, null, 5200),
            8 => new CpuSpec("AMD", "Zen 4 (Ryzen 8000)", 8, "AM5", 4, false, null, 5200),
            9 => new CpuSpec("AMD", "Zen 5 (Ryzen 9000)", 9, "AM5", 5, true, null, 5600),
            _ => null
        };
    }

    // ------------------------------------------------------------------ Chipset

    /// <summary>
    /// Chipset de placa-mãe. X16GenByCpuGen (Intel): geração PCIe do slot x16 por geração de CPU suportada;
    /// DefaultX16Gen (AMD): geração máxima do slot x16 que o chipset/placa oferece (a CPU pode limitar mais).
    /// MemoryOcAllowed: o chipset permite memória acima do máximo oficial da CPU (XMP/DOCP/EXPO).
    /// DependsOnBoard: o slot x16 é "Gen4 ou Gen5 conforme a placa"; a tabela guarda só o mínimo (Gen4) e a regra só afirma limite com a medição do driver.
    /// </summary>
    public sealed record ChipsetSpec(string Name, string Vendor, string Socket,
        IReadOnlyDictionary<int, int> X16GenByCpuGen, int DefaultX16Gen, bool MemoryOcAllowed, bool DependsOnBoard = false)
    {
        /// <summary>Geração PCIe do slot x16 para a CPU informada; null se a combinação não é conhecida.</summary>
        public int? X16Gen(CpuSpec? cpu)
        {
            if (X16GenByCpuGen.Count > 0)
                return cpu is { IsIntel: true } && X16GenByCpuGen.TryGetValue(cpu.Gen, out var g) ? g : null;
            return cpu is null || cpu.Vendor == Vendor ? DefaultX16Gen : null;
        }

        /// <summary>true se o chipset aceita oficialmente a geração Intel informada.</summary>
        public bool SupportsIntelGen(int gen) => X16GenByCpuGen.ContainsKey(gen);
    }

    private static readonly Dictionary<string, ChipsetSpec> Chipsets = BuildChipsets();

    private static Dictionary<string, ChipsetSpec> BuildChipsets()
    {
        var d = new Dictionary<string, ChipsetSpec>(StringComparer.OrdinalIgnoreCase);
        void Intel(string name, string socket, bool oc, params (int Cpu, int Pcie)[] map) =>
            d[name] = new ChipsetSpec(name, "Intel", socket, map.ToDictionary(x => x.Cpu, x => x.Pcie), 0, oc);
        void IntelBoard(string name, string socket, bool oc, params (int Cpu, int Pcie)[] map) =>
            d[name] = new ChipsetSpec(name, "Intel", socket, map.ToDictionary(x => x.Cpu, x => x.Pcie), 0, oc, DependsOnBoard: true);
        void Amd(string name, string socket, int pcie, bool oc = true, bool board = false) =>
            d[name] = new ChipsetSpec(name, "AMD", socket, new Dictionary<int, int>(), pcie, oc, board);

        // LGA1151 (100/200/300): PCIe 3.0 no slot da GPU. Memória acima do oficial só nas séries Z.
        foreach (var n in new[] { "H110", "B150", "H170" }) Intel(n, "LGA1151", false, (6, 3), (7, 3));
        Intel("Z170", "LGA1151", true, (6, 3), (7, 3));
        foreach (var n in new[] { "B250", "H270" }) Intel(n, "LGA1151", false, (6, 3), (7, 3));
        Intel("Z270", "LGA1151", true, (6, 3), (7, 3));
        foreach (var n in new[] { "H310", "B360", "B365", "H370" }) Intel(n, "LGA1151", false, (8, 3), (9, 3));
        foreach (var n in new[] { "Z370", "Z390" }) Intel(n, "LGA1151", true, (8, 3), (9, 3));

        // LGA1200 (400/500). H410/B460: só 10ª geração. H470/Z490/500: 10ª (Gen3) e 11ª (Gen4, exige BIOS).
        foreach (var n in new[] { "H410", "B460" }) Intel(n, "LGA1200", false, (10, 3));
        Intel("H470", "LGA1200", false, (10, 3), (11, 4));
        Intel("Z490", "LGA1200", true, (10, 3), (11, 4));
        Intel("H510", "LGA1200", false, (10, 3), (11, 4));
        foreach (var n in new[] { "B560", "H570", "Z590" }) Intel(n, "LGA1200", true, (10, 3), (11, 4));

        // LGA1700 (600/700): Gen5 no slot x16 depende da placa; só as séries Z são tratadas como Gen5.
        // B660/B760: "Gen4 ou Gen5 conforme a placa" (a tabela guarda o mínimo, Gen4; o limite só é afirmado com a medição).
        Intel("H610", "LGA1700", false, (12, 4), (13, 4), (14, 4));
        foreach (var n in new[] { "B660", "B760" }) IntelBoard(n, "LGA1700", true, (12, 4), (13, 4), (14, 4));
        foreach (var n in new[] { "Z690", "Z790" }) Intel(n, "LGA1700", true, (12, 5), (13, 5), (14, 5));

        // AM4: PCIe 3.0 nos chipsets 300/400 e A520; 4.0 em B550/X570.
        foreach (var n in new[] { "A320", "B350", "X370", "B450", "X470", "A520" }) Amd(n, "AM4", 3);
        foreach (var n in new[] { "B550", "X570" }) Amd(n, "AM4", 4);
        // AM5: Gen5 no slot x16 só em B650E/X670E/X870/X870E; A620/B650/X670: "Gen4 ou Gen5 conforme a placa" (mínimo Gen4 na tabela).
        foreach (var n in new[] { "A620", "B650", "X670" }) Amd(n, "AM5", 4, board: true);
        foreach (var n in new[] { "B650E", "X670E", "X870", "X870E" }) Amd(n, "AM5", 5);
        return d;
    }

    private static readonly Regex ChipsetRx = new(
        @"(?<![A-Za-z0-9])(" + string.Join("|", BuildChipsetNames()) + @")(?![0-9])", Rx);

    private static IEnumerable<string> BuildChipsetNames() =>
        Chipsets.Keys.OrderByDescending(k => k.Length).ThenBy(k => k);

    /// <summary>Procura um nome de chipset conhecido dentro de um texto (chipset via PnP ou modelo da placa).</summary>
    public static ChipsetSpec? ResolveChipset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var m = ChipsetRx.Match(text);
            return m.Success && Chipsets.TryGetValue(m.Groups[1].Value, out var c) ? c : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ GPU

    /// <summary>
    /// Placa de vídeo: Gen e Lanes do link PCIe nativo; Tier (1 entrada, 2 intermediária, 3 alta);
    /// ReBar: família com suporte oficial a Resizable BAR; LanesVary: a largura muda conforme a variante.
    /// </summary>
    public sealed record GpuSpec(string Family, int Gen, int Lanes, int Tier, bool ReBar, bool LanesVary = false);

    private static readonly (Regex Pattern, GpuSpec Spec)[] Gpus =
    {
        // NVIDIA RTX 50
        (new(@"RTX\s*50(50|60)", Rx), new("GeForce RTX 50 (entrada/intermediária)", 5, 8, 2, true)),
        (new(@"RTX\s*50(70|80|90)", Rx), new("GeForce RTX 50 (alta)", 5, 16, 3, true)),
        // NVIDIA RTX 40
        (new(@"RTX\s*40(50|60)", Rx), new("GeForce RTX 40 (intermediária)", 4, 8, 2, true)),
        (new(@"RTX\s*40\d0", Rx), new("GeForce RTX 40", 4, 16, 3, true)),
        // NVIDIA RTX 30 (a 3050 tem variantes x8 e x16)
        (new(@"RTX\s*3050", Rx), new("GeForce RTX 3050", 4, 8, 1, true, LanesVary: true)),
        (new(@"RTX\s*3060", Rx), new("GeForce RTX 3060", 4, 16, 2, true)),
        (new(@"RTX\s*30\d0", Rx), new("GeForce RTX 30", 4, 16, 3, true)),
        // NVIDIA GTX 16 / RTX 20 / GTX 10
        (new(@"RTX\s*20\d0|GTX\s*16\d0", Rx), new("GeForce GTX 16 / RTX 20", 3, 16, 2, false)),
        // GT/GTX 1030 é x4 (a regra GTX 10x0 x16 não vale para ela).
        (new(@"\bGTX?\s*1030\b", Rx), new("GeForce GT/GTX 1030", 3, 4, 1, false)),
        (new(@"GTX\s*10\d0", Rx), new("GeForce GTX 10", 3, 16, 1, false)),
        // AMD Radeon
        (new(@"RX\s*(6400|6500)", Rx), new("Radeon RX 6400/6500 XT", 4, 4, 1, true)),
        (new(@"RX\s*(6600|6650|7600)", Rx), new("Radeon RX 6600/7600", 4, 8, 2, true)),
        (new(@"RX\s*(6[789]\d0|7[789]\d0)", Rx), new("Radeon RX 6700+/7700+", 4, 16, 3, true)),
        (new(@"RX\s*90(60|70)", Rx), new("Radeon RX 9000", 5, 16, 3, true)),
        // Intel Arc
        (new(@"Arc.*\bA380\b", Rx), new("Intel Arc A380", 4, 8, 1, true)),
        (new(@"Arc.*\bA(580|750|770)\b", Rx), new("Intel Arc A-series", 4, 16, 2, true)),
        (new(@"Arc.*\bB(570|580)\b", Rx), new("Intel Arc B-series", 4, 8, 2, true)),
    };

    private static readonly Regex LaptopGpuRx = new(@"Laptop|Mobile|Max-?Q|Notebook", Rx);

    /// <summary>Resolve o nome da GPU (ex.: "NVIDIA GeForce RTX 5060"). Null se não reconhecida ou se for GPU de notebook (a largura do link varia por projeto).</summary>
    /// <param name="name">Nome da GPU.</param>
    /// <param name="isLaptop">true se o PC é um notebook (nenhuma GPU é resolvida).</param>
    public static GpuSpec? ResolveGpu(string? name, bool isLaptop = false)
    {
        if (string.IsNullOrWhiteSpace(name) || isLaptop || LaptopGpuRx.IsMatch(name)) return null;
        foreach (var (p, s) in Gpus)
        {
            try { if (p.IsMatch(name)) return Loc.IsPt ? s : s with { Family = s.Family.Replace("entrada/intermediária", "entry/mid-range").Replace("intermediária", "mid-range").Replace("alta", "high-end") }; } catch { }
        }
        return null;
    }

    // ------------------------------------------------------------------ Caminho de upgrade

    /// <summary>
    /// Sugestão de troca de CPU mantendo a placa-mãe. PcieGen e RamMTs são o que a combinação nova entrega
    /// (limitada também pelo chipset). Requirement: condição a conferir antes de comprar.
    /// </summary>
    public sealed record UpgradeSuggestion(string Cpu, int Cores, int Threads, string Socket, int PcieGen,
        int? RamMTs, string RamType, string Requirement);

    /// <summary>
    /// Caminho de upgrade sem trocar a placa, para combinações conhecidas; null quando não há sugestão segura.
    /// </summary>
    public static UpgradeSuggestion? UpgradePath(CpuSpec? cpu, ChipsetSpec? chipset, int cpuCores, int? gpuPcieGen) =>
        UpgradePath(cpu, chipset, cpuCores, 0, gpuPcieGen);

    /// <summary>
    /// Caminho de upgrade sem trocar a placa. Só sugere melhora real: a CPU sugerida não pode ter menos núcleos nem threads que a atual
    /// e precisa ganhar em núcleos/threads ou em PCIe/RAM oficial. Sem a contagem de núcleos da CPU atual não há sugestão.
    /// </summary>
    /// <param name="cpu">CPU atual resolvida.</param>
    /// <param name="chipset">Chipset da placa.</param>
    /// <param name="cpuCores">Núcleos físicos da CPU atual.</param>
    /// <param name="cpuThreads">Threads da CPU atual (0 = não lido).</param>
    /// <param name="gpuPcieGen">Geração PCIe máxima da GPU, se conhecida.</param>
    public static UpgradeSuggestion? UpgradePath(CpuSpec? cpu, ChipsetSpec? chipset, int cpuCores, int cpuThreads, int? gpuPcieGen)
    {
        var s = UpgradeCandidate(cpu, chipset, cpuCores, gpuPcieGen);
        return s is not null && cpu is not null && IsRealImprovement(s, cpu, cpuCores, cpuThreads, gpuPcieGen) ? s : null;
    }

    private static bool IsRealImprovement(UpgradeSuggestion s, CpuSpec cpu, int cores, int threads, int? gpuPcieGen)
    {
        if (cores <= 0) return false;
        if (s.Cores < cores || (threads > 0 && s.Threads < threads)) return false;
        if (s.Cores > cores || (threads > 0 && s.Threads > threads)) return true;
        bool pcie = gpuPcieGen is { } g && Math.Min(g, s.PcieGen) > Math.Min(g, cpu.PcieGen);
        var curRam = cpu.MaxRamMTs(s.RamType);
        bool ram = s.RamMTs is { } nm && curRam is { } cm && nm > cm;
        return pcie || ram;
    }

    private static UpgradeSuggestion? UpgradeCandidate(CpuSpec? cpu, ChipsetSpec? chipset, int cpuCores, int? gpuPcieGen)
    {
        if (cpu is null || chipset is null) return null;
        bool fewCores = cpuCores is > 0 and <= 4;

        // LGA1200: 10ª geração em placa que aceita a 11ª.
        if (cpu.IsIntel && cpu.Gen == 10 && chipset.SupportsIntelGen(11) && (fewCores || gpuPcieGen >= 4))
        {
            int pcie = chipset.X16Gen(Spec11) ?? 4;
            var req = chipset.Name.StartsWith('H') && chipset.Name != "H510" || chipset.Name == "Z490"
                ? Loc.T("BIOS compatível com a 11ª geração; em H470/Z490 o Gen 4 só vale em placas que declaram suporte (confira a lista de CPUs do fabricante).", "BIOS compatible with 11th gen; on H470/Z490 Gen 4 only applies to boards that declare support (check the manufacturer's CPU list).")
                : Loc.T("BIOS compatível com a 11ª geração (confira a lista de CPUs suportadas do fabricante da placa).", "BIOS compatible with 11th gen (check the CPU support list from the board manufacturer).");
            return new UpgradeSuggestion("Core i5-11400F", 6, 12, "LGA1200", pcie, 3200, "DDR4", req);
        }
        // LGA1200 sem suporte à 11ª geração: só dentro da 10ª.
        if (cpu.IsIntel && cpu.Gen == 10 && fewCores)
            return new UpgradeSuggestion("Core i5-10400F", 6, 12, "LGA1200", 3, 2666, "DDR4",
                Loc.T("Mesma geração: o PCIe continua em Gen 3 e a memória em DDR4-2666; o ganho é de núcleos.", "Same generation: PCIe stays at Gen 3 and memory at DDR4-2666; the gain is in cores."));
        // AM4: Ryzen 1000 a 4000 (ou poucos núcleos) -> Ryzen 5 5600.
        if (cpu.Vendor == "AMD" && cpu.Socket == "AM4" && cpu.Gen <= 4 && chipset.Socket == "AM4")
        {
            int pcie = Math.Min(4, chipset.DefaultX16Gen);
            return new UpgradeSuggestion("Ryzen 5 5600", 6, 12, "AM4", pcie, 3200, "DDR4",
                Loc.T("BIOS com suporte aos Ryzen 5000 (placas 300/400 exigem atualização; confira a lista de CPUs do fabricante).", "BIOS with Ryzen 5000 support (300/400 boards require an update; check the manufacturer's CPU list)."));
        }
        // LGA1700: i3 de poucos núcleos -> i5 de 6 núcleos.
        if (cpu.IsIntel && cpu.Gen is 12 or 13 && fewCores && chipset.SupportsIntelGen(cpu.Gen))
            return new UpgradeSuggestion("Core i5-12400F", 6, 12, "LGA1700", chipset.X16Gen(cpu) ?? 4, 3200, "DDR4",
                Loc.T("Se a placa usa DDR5, o máximo oficial dessa CPU é DDR5-4800; BIOS atualizada recomendada.", "If the board uses DDR5, this CPU's official maximum is DDR5-4800; an updated BIOS is recommended."));
        return null;
    }

    private static readonly CpuSpec Spec11 = new("Intel", "Rocket Lake (11ª geração)", 11, "LGA1200", 4, false, 3200, null);
}
