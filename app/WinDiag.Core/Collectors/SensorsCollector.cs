using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Sensors;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Sensores: temperatura, ventoinha, tensão, potência e clock lidos pelo <see cref="SensorHub"/> (HWiNFO, LibreHardwareMonitor, nvidia-smi).
/// Somente leitura, uma leitura única; não exige administrador, mas CPU, placa-mãe e memória só aparecem com o HWiNFO aberto
/// (Shared Memory Support) ou com o driver PawnIO instalado pelo usuário (o WinDiag não baixa nem instala drivers).
/// Uma leitura única não prova throttling: os alertas descrevem o que foi medido e não afirmam a causa.
/// </summary>
public sealed class SensorsCollector : ICollector
{
    /// <summary>Fonte de leituras injetada em testes; null = usa o <see cref="SensorHub"/> real.</summary>
    private readonly Func<(SensorSnapshot Snapshot, SensorStatus Status)>? _provider;
    /// <summary>Discos físicos injetados em testes; null = lê MSFT_PhysicalDisk pelo WMI.</summary>
    private readonly Func<IReadOnlyCollection<DiskInfo>?>? _diskProvider;

    /// <summary>
    /// Hub compartilhado (opcional). Se definido e devolver um hub, o coletor usa ESSE hub (não cria outro Computer da
    /// LibreHardwareMonitor) e NÃO o descarta; se for null ou devolver null, o coletor cria um hub próprio e o descarta ao fim.
    /// O <see cref="SensorHub"/> não é thread-safe: o chamador deve serializar as chamadas de Read. Defina também
    /// <see cref="SharedHubLock"/> para que o coletor (e a captura) façam lock nele ao ler.
    /// </summary>
    public static Func<SensorHub?>? SharedHub { get; set; }

    /// <summary>Objeto de lock usado ao chamar <see cref="SensorHub.Read"/> no hub compartilhado; se null, não há lock. O App deve usar o mesmo objeto nas suas próprias leituras.</summary>
    public static object? SharedHubLock { get; set; }

    /// <summary>Lê um hub respeitando <see cref="SharedHubLock"/>.</summary>
    /// <param name="hub">Hub a ler.</param>
    internal static SensorSnapshot ReadLocked(SensorHub hub)
    {
        var l = SharedHubLock;
        if (l is null) return hub.Read();
        lock (l) return hub.Read();
    }

    /// <summary>Cria o coletor com o hub padrão.</summary>
    public SensorsCollector() { }

    /// <summary>Cria o coletor com fontes injetadas (testes).</summary>
    /// <param name="provider">Entrega a leitura e o status.</param>
    /// <param name="disks">Discos físicos (null = desconhecido).</param>
    internal SensorsCollector(Func<(SensorSnapshot Snapshot, SensorStatus Status)> provider, Func<IReadOnlyCollection<DiskInfo>?>? disks = null)
    {
        _provider = provider;
        _diskProvider = disks ?? (() => null);
    }

    /// <inheritdoc/>
    public string Key => "sensores";
    /// <inheritdoc/>
    public string Title => Loc.T("Sensores", "Sensors");

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        SensorSnapshot snap;
        SensorStatus status;
        try
        {
            if (_provider is not null) (snap, status) = _provider();
            else
            {
                SensorHub? shared = null;
                try { shared = SharedHub?.Invoke(); } catch (Exception) { shared = null; }
                if (shared is not null)
                {
                    snap = ReadLocked(shared);
                    status = shared.Status;
                }
                else
                {
                    using var hub = new SensorHub();
                    snap = hub.Read();
                    status = hub.Status;
                }
            }
        }
        catch (Exception ex)
        {
            res.Note(Loc.T("Sensores: falha ao ler (", "Sensors: read failed (") + ex.GetType().Name + ": " + ex.Message + ")");
            snap = new SensorSnapshot { Time = DateTime.Now };
            status = new SensorStatus();
        }

        try { BuildGroups(res, snap, status); }
        catch (Exception ex) { res.Note(Loc.T("Sensores: falha ao montar a tabela (" + ex.Message + ")", "Sensors: failed to build the table (" + ex.Message + ")")); }

        // Fontes usadas (chave estável, ordenada): o histórico ignora a seção quando as fontes diferem entre duas leituras.
        facts.SensorSources = snap.SourcesUsed.Count == 0 ? "none" : string.Join("+", snap.SourcesUsed.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        IReadOnlyCollection<DiskInfo>? nvme = null;
        try { nvme = _provider is not null ? _diskProvider?.Invoke() : ReadDisks(res); }
        catch (Exception) { nvme = null; }
        try { ApplyRules(snap.Readings, facts, res, nvme); }
        catch (Exception ex) { res.Note(Loc.T("Sensores: falha ao avaliar os alertas (" + ex.Message + ")", "Sensors: failed to evaluate the alerts (" + ex.Message + ")")); }

        foreach (var n in status.Notes) res.Note(Loc.T("Sensores: ", "Sensors: ") + n);
        if (!status.HwInfoAvailable && !status.LhmAvailable && snap.Readings.Count == 0)
            res.Note(Loc.T("Sensores: nenhuma fonte de leitura disponível", "Sensors: no reading source available"));
        return res;
    }

    // ------------------------------------------------------------------ tabelas

    /// <summary>Ordem de exibição das categorias de peça nas tabelas.</summary>
    private static readonly string[] KindOrder =
        { HardwareClassifier.Cpu, HardwareClassifier.Gpu, HardwareClassifier.Board, HardwareClassifier.Memory, HardwareClassifier.Storage, HardwareClassifier.Psu, HardwareClassifier.Controller, HardwareClassifier.Other };

    /// <summary>Monta uma tabela por peça (Sensor, Valor, Origem) e o grupo com a situação das fontes de leitura e orientações.</summary>
    private static void BuildGroups(SectionResult res, SensorSnapshot snap, SensorStatus status)
    {
        var byPiece = snap.Readings
            .GroupBy(r => (r.HardwareKind, r.Hardware))
            .OrderBy(g => Array.IndexOf(KindOrder, g.Key.HardwareKind) is var i && i < 0 ? 99 : i)
            .ThenBy(g => g.Key.Hardware, StringComparer.OrdinalIgnoreCase);
        foreach (var piece in byPiece)
        {
            var g = res.Group(piece.Key.Hardware);
            // HardwareKind fica em português (é chave de comparação); para exibir usa-se o rótulo do idioma atual.
            g.Add(Loc.T("Categoria", "Category"), HardwareKinds.HardwareKindLabel(piece.Key.HardwareKind));
            var t = g.Table(Loc.T("Sensores de ", "Sensors of ") + piece.Key.Hardware, "Sensor", Loc.T("Valor", "Value"), Loc.T("Origem", "Source"));
            foreach (var r in piece.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                t.AddRow(r.Name, FormatValue(r), r.Source);
        }

        var f = res.Group(Loc.T("Fontes de sensores", "Sensor sources"));
        f.Add(Loc.T("HWiNFO (memória compartilhada)", "HWiNFO (shared memory)"), status.HwInfoAvailable ? Loc.T("ativo", "active") : Loc.T("não detectado", "not detected"));
        f.Add("LibreHardwareMonitor", status.LhmAvailable ? Loc.T("ativa", "active") + (status.LhmVersion is { Length: > 0 } v ? Loc.T($" (versão {v})", $" (version {v})") : "") : Loc.T("indisponível", "unavailable"));
        f.Add(Loc.T("Driver de leitura de hardware", "Hardware reading driver"), status.KernelDriverAvailable ? (string.IsNullOrEmpty(status.DriverName) ? Loc.T("instalado", "installed") : status.DriverName + Loc.T(" instalado", " installed")) : Loc.T("não instalado", "not installed"));
        f.Add(Loc.T("Fontes que trouxeram leituras", "Sources that returned readings"), snap.SourcesUsed.Count == 0 ? null : string.Join(", ", snap.SourcesUsed));
        f.Add(Loc.T("Leituras", "Readings"), snap.Readings.Count);
        foreach (var n in status.Notes) f.Notes.Add(n);
        f.Notes.Add(Loc.T("O WinDiag só lê: não baixa nem instala drivers. Para ver a temperatura da CPU, abra o HWiNFO com \"Shared Memory Support\" ligado ou instale o driver PawnIO pelo site oficial.", "WinDiag only reads: it does not download or install drivers. To see the CPU temperature, open HWiNFO with \"Shared Memory Support\" enabled or install the PawnIO driver from its official site."));
        f.Notes.Add(Loc.T("As leituras são de um instante só (sem carga controlada). Para ver o comportamento durante o jogo, use a aba Captura.", "Readings are a single moment (no controlled load). To see behavior during gameplay, use the Capture tab."));
    }

    /// <summary>Valor com unidade em pt-BR (ex.: "45 °C", "1.200 RPM"), com casas conforme a unidade.</summary>
    /// <param name="r">Leitura.</param>
    public static string FormatValue(SensorReading r)
    {
        int dec = r.Unit switch
        {
            "RPM" or "MHz" => 0,
            "V" => 3,
            "A" => 2,
            _ => 1
        };
        return Fmt.Num(r.Value, dec) + (string.IsNullOrEmpty(r.Unit) ? "" : " " + r.Unit);
    }

    /// <summary>Lê modelo, barramento e tipo de mídia dos discos (WMI MSFT_PhysicalDisk, root\Microsoft\Windows\Storage) para escolher o limiar de temperatura. BusType 17 = NVMe; MediaType 3 = HDD. Falha vira nota e devolve null.</summary>
    private static List<DiskInfo>? ReadDisks(SectionResult res)
    {
        string? err = null;
        var list = new List<DiskInfo>();
        var disks = Wmi.Query("SELECT FriendlyName, BusType, MediaType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage", e => err = e);
        if (err is not null) { res.Note(Loc.T("Sensores: tipo de barramento dos discos não lido (" + err + ")", "Sensors: disk bus type not read (" + err + ")")); return null; }
        foreach (var d in disks)
            if (d.S("FriendlyName") is { Length: > 0 } n) list.Add(new DiskInfo(n, d.L("BusType") == 17, d.L("MediaType") == 3));
        Wmi.DisposeAll(disks);
        return list;
    }

    /// <summary>Disco físico conhecido (para escolher o limiar de temperatura).</summary>
    /// <param name="Model">Nome do modelo.</param>
    /// <param name="Nvme">true se o barramento é NVMe.</param>
    /// <param name="Hdd">true se o disco é mecânico (HDD).</param>
    internal sealed record DiskInfo(string Model, bool Nvme, bool Hdd);

    /// <summary>Normaliza o nome de um disco para casar leitura de sensor e disco: tira prefixo "S.M.A.R.T.:"/"Drive:", trechos entre () e [] (serial) e tudo que não é letra/dígito.</summary>
    /// <param name="name">Nome vindo do sensor ou do WMI.</param>
    internal static string NormDisk(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"^\s*(s\.?m\.?a\.?r\.?t\.?|drive)\s*:", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Casamento tolerante de nomes normalizados (um contém o outro); ignora nomes curtos demais (menos de 4 caracteres) para evitar falsos positivos.</summary>
    private static bool DiskNameMatches(string a, string b)
    {
        if (a.Length < 4 || b.Length < 4) return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ regras

    /// <summary>Sensores de temperatura que são limites (TjMax, crítico, distância até o limite, máx./mín.), e não medições; são descartados.</summary>
    private static readonly Regex ThresholdRx = new(@"warning|critical|threshold|limit|tjmax|t\s?junction\s?max|distance|\bmax\b|\bmin\b|alarm|aviso|crítica", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    /// <summary>Sensor de hot spot/junção da GPU, tratado à parte do núcleo.</summary>
    private static readonly Regex HotSpotRx = new(@"hot\s?spot|junction", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    /// <summary>Sensor de temperatura da memória da GPU (junção da VRAM), que não representa o núcleo.</summary>
    private static readonly Regex MemJunctionRx = new(@"mem(ory)?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    /// <summary>Número de 3 ou 4 dígitos do nome da fonte, com prefixo (0 a 4 letras) e sufixo (0 a 3 letras) opcionais, isolado de outros caracteres alfanuméricos.</summary>
    private static readonly Regex PsuModelRx = new(@"(?<![A-Za-z0-9])([A-Za-z]{0,4})(\d{3,4})([A-Za-z]{0,3})(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>true se é uma medição de temperatura plausível: entre 0 e 150 °C (exclusive) e que não seja um sensor de limite.</summary>
    private static bool IsTemp(SensorReading r) =>
        r.Kind == SensorKind.Temperature && r.Value > 0 && r.Value < 150 && !ThresholdRx.IsMatch(r.Name);

    /// <summary>Sensores preferidos para a CPU: Package (Intel), Tctl/Tdie (AMD), que representam o processador como um todo.</summary>
    private static readonly Regex CpuPreferredRx = new(@"package|tctl|tdie", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>true se o sensor da GPU não é hot spot nem junção da memória, isto é, representa o núcleo.</summary>
    private static bool IsGpuCore(SensorReading r) => !HotSpotRx.IsMatch(r.Name) && !MemJunctionRx.IsMatch(r.Name);

    /// <summary>Leitura de CPU usada nos alertas: prefere Package/Tctl/Tdie; sem elas, a maior entre as demais (limiares como TjMax/Critical/Max são ignorados).</summary>
    private static SensorReading? PickCpu(IEnumerable<SensorReading> temps)
    {
        var cpu = temps.Where(r => r.HardwareKind == HardwareClassifier.Cpu).ToList();
        if (cpu.Count == 0) return null;
        var pref = cpu.Where(r => CpuPreferredRx.IsMatch(r.Name)).ToList();
        return (pref.Count > 0 ? pref : cpu).MaxBy(r => r.Value);
    }

    /// <summary>Leitura do núcleo da GPU (nem hot spot nem junção de memória) usada nos alertas.</summary>
    private static SensorReading? PickGpuCore(IEnumerable<SensorReading> temps)
        => temps.Where(r => r.HardwareKind == HardwareClassifier.Gpu && IsGpuCore(r)).MaxBy(r => r.Value);

    /// <summary>
    /// Escolhe a temperatura de CPU e de GPU para alertas e captura, com a mesma regra do coletor: ignora sensores de limite
    /// (TjMax, Critical, Distance, Limit, Max, Warning); a CPU prefere Package/Tctl/Tdie e a GPU usa o núcleo, não o hot spot nem a junção da memória.
    /// Null quando não há sensor.
    /// </summary>
    /// <param name="readings">Leituras do hub.</param>
    public static (double? Cpu, double? Gpu) PickMaxTemps(IEnumerable<SensorReading> readings)
    {
        var temps = readings.Where(IsTemp).ToList();
        return (PickCpu(temps)?.Value, PickGpuCore(temps)?.Value);
    }

    /// <summary>
    /// Avalia as leituras: preenche os fatos (CpuTempC, GpuTempC, fonte digital) e acrescenta os achados de temperatura e ventoinha.
    /// Os limiares são de uma leitura única e não afirmam throttling nem causa.
    /// </summary>
    /// <param name="readings">Leituras do hub.</param>
    /// <param name="facts">Fatos a preencher.</param>
    /// <param name="res">Seção que recebe os achados.</param>
    /// <param name="disks">Discos físicos conhecidos; null = desconhecidos. Disco sem casamento de nome usa 70 °C.</param>
    internal static void ApplyRules(IReadOnlyList<SensorReading> readings, SystemFacts facts, SectionResult res, IReadOnlyCollection<DiskInfo>? disks)
    {
        var temps = readings.Where(IsTemp).ToList();

        // CPU
        var cpuTop = PickCpu(temps);
        if (cpuTop is not null)
        {
            var top = cpuTop;
            facts.CpuTempC = Math.Round(top.Value, 1);
            // Limiares de CPU: >= 90 °C muito alta (Atenção) e >= 80 °C elevada (Aviso). São referências práticas do WinDiag, não limites de fabricante, e uma leitura única não prova throttling.
            string ev = Loc.T($"{top.Name}: {Fmt.Num(top.Value, 0)} °C (leitura única; fonte: {top.Source})", $"{top.Name}: {Fmt.Num(top.Value, 0)} °C (single reading; source: {top.Source})");
            if (top.Value >= 90)
                res.Find(Severity.Atencao, Loc.T("Temperatura da CPU muito alta", "CPU temperature very high"), ev,
                    Loc.T("Perto de 90 °C ou mais, o processador costuma reduzir o clock para se proteger. Esta é uma leitura de um instante só: sem medir a carga junto, o app não afirma a causa (refrigeração, pasta térmica, fluxo de ar ou carga alta naquele momento).", "At around 90 °C or more, the processor usually lowers its clock to protect itself. This is a single-moment reading: without measuring the load alongside, the app does not state the cause (cooling, thermal paste, airflow or high load at that moment)."),
                    Loc.T("Repita a leitura em repouso e durante o jogo (aba Captura). Se continuar alta, confira o cooler, a pasta térmica e o fluxo de ar do gabinete.", "Repeat the reading at idle and during the game (Capture tab). If it stays high, check the cooler, the thermal paste and the case airflow."),
                    impact: 50, id: "sensors.cpu_hot",
                    simple: Loc.T("O processador está muito quente neste momento. Se isso se repetir durante jogos, o PC pode perder desempenho para se proteger.", "The processor is very hot right now. If this repeats during games, the PC may lose performance to protect itself."));
            else if (top.Value >= 80)
                res.Find(Severity.Aviso, Loc.T("Temperatura da CPU elevada", "CPU temperature elevated"), ev,
                    Loc.T("A CPU está em uma faixa alta de temperatura nesta leitura. Em carga pesada isso pode ser esperado; em repouso, indica refrigeração limitada ou processos usando a CPU. O app não afirma a causa.", "The CPU is in a high temperature range in this reading. Under heavy load this can be expected; at idle, it indicates limited cooling or processes using the CPU. The app does not state the cause."),
                    Loc.T("Compare a leitura em repouso e durante o jogo (aba Captura) e confira o fluxo de ar do gabinete.", "Compare the reading at idle and during the game (Capture tab) and check the case airflow."),
                    impact: 25, id: "sensors.cpu_warm",
                    simple: Loc.T("O processador está quente. Em jogo pode ser normal; em repouso vale conferir a refrigeração.", "The processor is hot. In games it may be normal; at idle it is worth checking the cooling."));
        }
        else
        {
            facts.CpuTempC = null;
            res.Find(Severity.Info, Loc.T("Temperatura da CPU indisponível", "CPU temperature unavailable"), Loc.T("Nenhum sensor de temperatura da CPU respondeu", "No CPU temperature sensor responded"),
                Loc.T("O Windows não expõe a temperatura do núcleo da CPU sem um leitor de sensores. Sem ela, não dá para saber se a CPU está esquentando durante os jogos.", "Windows does not expose the CPU core temperature without a sensor reader. Without it, there is no way to tell whether the CPU is heating up during games."),
                Loc.T("Abra o HWiNFO com \"Shared Memory Support\" ligado, ou instale o driver PawnIO pelo site oficial dele. O WinDiag só lê os sensores e não instala drivers.", "Open HWiNFO with \"Shared Memory Support\" enabled, or install the PawnIO driver from its official site. WinDiag only reads the sensors and does not install drivers."),
                id: "sensors.cpu_unavailable",
                simple: Loc.T("O WinDiag não conseguiu ler a temperatura do processador. Para ver esse dado, deixe o HWiNFO aberto com a opção \"Shared Memory Support\" ligada ou instale o driver PawnIO (site oficial). O WinDiag não instala drivers.", "WinDiag could not read the processor temperature. To see this data, keep HWiNFO open with the \"Shared Memory Support\" option enabled or install the PawnIO driver (official site). WinDiag does not install drivers."));
        }

        // GPU (núcleo e hot spot separados)
        var gpuAll = temps.Where(r => r.HardwareKind == HardwareClassifier.Gpu).ToList();
        var gpuHot = gpuAll.Where(r => HotSpotRx.IsMatch(r.Name) && !MemJunctionRx.IsMatch(r.Name)).ToList();
        var gpuTop = PickGpuCore(temps);
        if (gpuTop is not null)
        {
            var top = gpuTop;
            facts.GpuTempC = Math.Round(top.Value, 1);
            string ev = Loc.T($"{top.Name}: {Fmt.Num(top.Value, 0)} °C (leitura única; fonte: {top.Source})", $"{top.Name}: {Fmt.Num(top.Value, 0)} °C (single reading; source: {top.Source})");
            if (top.Value >= 85)
                res.Find(Severity.Atencao, Loc.T("Temperatura da GPU muito alta", "GPU temperature very high"), ev,
                    Loc.T("Perto de 85 °C ou mais, a placa de vídeo costuma reduzir o clock. Esta é uma leitura de um instante só; o app não afirma a causa (ventoinhas, poeira, fluxo de ar, curva de ventoinha ou carga naquele momento).", "At around 85 °C or more, the graphics card usually lowers its clock. This is a single-moment reading; the app does not state the cause (fans, dust, airflow, fan curve or load at that moment)."),
                    Loc.T("Repita a leitura durante o jogo (aba Captura). Se continuar alta, confira as ventoinhas, a poeira e o fluxo de ar do gabinete.", "Repeat the reading during the game (Capture tab). If it stays high, check the fans, the dust and the case airflow."),
                    impact: 50, id: "sensors.gpu_hot",
                    simple: Loc.T("A placa de vídeo está muito quente neste momento. Se isso se repetir em jogos, ela pode perder desempenho para se proteger.", "The graphics card is very hot right now. If this repeats in games, it may lose performance to protect itself."));
            else if (top.Value >= 80)
                res.Find(Severity.Aviso, Loc.T("Temperatura da GPU elevada", "GPU temperature elevated"), ev,
                    Loc.T("A GPU está em uma faixa alta de temperatura nesta leitura. Sob carga isso pode ser esperado; em repouso, não. O app não afirma a causa.", "The GPU is in a high temperature range in this reading. Under load this can be expected; at idle, it is not. The app does not state the cause."),
                    Loc.T("Compare a leitura em repouso e durante o jogo (aba Captura) e confira o fluxo de ar do gabinete.", "Compare the reading at idle and during the game (Capture tab) and check the case airflow."),
                    impact: 25, id: "sensors.gpu_warm",
                    simple: Loc.T("A placa de vídeo está quente. Em jogo pode ser normal; em repouso vale conferir a refrigeração.", "The graphics card is hot. In games it may be normal; at idle it is worth checking the cooling."));
        }
        else facts.GpuTempC = null;
        if (gpuHot.Count > 0)
        {
            var top = gpuHot.MaxBy(r => r.Value)!;
            string ev = Loc.T($"{top.Name}: {Fmt.Num(top.Value, 0)} °C (leitura única; fonte: {top.Source})", $"{top.Name}: {Fmt.Num(top.Value, 0)} °C (single reading; source: {top.Source})");
            if (top.Value >= 95)
                res.Find(Severity.Atencao, Loc.T("Hot spot da GPU muito alto", "GPU hot spot very high"), ev,
                    Loc.T("O hot spot é o ponto mais quente do chip e fica normalmente uns 10 °C acima do núcleo. Perto de 95 °C ou mais a GPU costuma reduzir o clock. Leitura de um instante só; o app não afirma a causa.", "The hot spot is the hottest point of the chip and is normally about 10 °C above the core. At around 95 °C or more the GPU usually lowers its clock. Single-moment reading; the app does not state the cause."),
                    Loc.T("Repita a leitura durante o jogo (aba Captura) e confira ventoinhas, poeira e fluxo de ar.", "Repeat the reading during the game (Capture tab) and check fans, dust and airflow."),
                    impact: 45, id: "sensors.gpu_hotspot",
                    simple: Loc.T("O ponto mais quente da placa de vídeo passou de 95 °C. Vale conferir a refrigeração se isso se repetir em jogos.", "The hottest point of the graphics card exceeded 95 °C. It is worth checking the cooling if this repeats in games."));
            else if (top.Value >= 90)
                res.Find(Severity.Aviso, Loc.T("Hot spot da GPU elevado", "GPU hot spot elevated"), ev,
                    Loc.T("O hot spot é o ponto mais quente do chip e fica normalmente uns 10 °C acima do núcleo. Esta faixa é alta, mas pode ocorrer sob carga. O app não afirma a causa.", "The hot spot is the hottest point of the chip and is normally about 10 °C above the core. This range is high, but can occur under load. The app does not state the cause."),
                    Loc.T("Compare a leitura em repouso e durante o jogo (aba Captura).", "Compare the reading at idle and during the game (Capture tab)."),
                    impact: 20, id: "sensors.gpu_hotspot",
                    simple: Loc.T("O ponto mais quente da placa de vídeo está alto. Em jogo pode ser normal; em repouso vale conferir.", "The hottest point of the graphics card is high. In games it may be normal; at idle it is worth checking."));
        }

        // Ventoinha parada com a peça correspondente quente
        foreach (var piece in new[] { HardwareClassifier.Cpu, HardwareClassifier.Gpu })
        {
            // GPU: só a temperatura do núcleo (zero-RPM em repouso é normal); CPU: Package/Tctl/Tdie quando existir. Corte de 70 °C.
            var topT = piece == HardwareClassifier.Cpu ? PickCpu(temps) : PickGpuCore(temps);
            if (topT is null || topT.Value < FanTempCut) continue;
            var stopped = readings.FirstOrDefault(r => r.Kind == SensorKind.Fan && r.Value < 1 && r.Unit.Equals("RPM", StringComparison.OrdinalIgnoreCase)
                && IsRelevantFan(r, piece));
            if (stopped is null) continue;
            // Ventoinha que gira em outra leitura com o mesmo nome (outra fonte) não está parada.
            if (readings.Any(r => r.Kind == SensorKind.Fan && r.Value >= 1 && r.HardwareKind == stopped.HardwareKind
                && NormKey(r.Name) == NormKey(stopped.Name))) continue;
            string pn = piece == HardwareClassifier.Cpu ? "CPU" : "GPU";
            res.Find(Severity.Aviso, Loc.T($"Ventoinha da {pn} parada com a peça quente", $"{pn} fan stopped while the part is hot"),
                Loc.T($"{stopped.Name}: 0 RPM com {topT.Name} em {Fmt.Num(topT.Value, 0)} °C (fonte: {stopped.Source})", $"{stopped.Name}: 0 RPM with {topT.Name} at {Fmt.Num(topT.Value, 0)} °C (source: {stopped.Source})"),
                piece == HardwareClassifier.Gpu
                    ? Loc.T("Muitas placas de vídeo desligam as ventoinhas em repouso (modo silencioso), mas com o núcleo em 70 °C ou mais elas costumam girar. Pode ser sensor sem leitura, ventoinha desconectada ou defeito; o app não afirma qual.", "Many graphics cards turn the fans off at idle (silent mode), but with the core at 70 °C or more they usually spin. It may be a sensor without a reading, a disconnected fan or a defect; the app does not state which.")
                    : Loc.T("A ventoinha da CPU lida como parada com a temperatura em 70 °C ou mais. Pode ser conector sem sinal de rotação, ventoinha desconectada ou defeito; o app não afirma qual.", "The CPU fan reads as stopped with the temperature at 70 °C or more. It may be a connector with no rotation signal, a disconnected fan or a defect; the app does not state which."),
                Loc.T("Confira visualmente se a ventoinha gira e se o conector está bem encaixado; compare com a leitura de outro programa de sensores.", "Visually check whether the fan spins and the connector is firmly seated; compare with the reading of another sensor program."),
                impact: 40, id: "sensors.fan_stopped_" + pn.ToLowerInvariant() + (NormKey(stopped.Name) is { Length: > 0 } fk ? "." + fk : ""),
                simple: Loc.T($"A ventoinha da {pn} aparece parada enquanto a peça está quente. Vale olhar se ela realmente gira.", $"The {pn} fan appears stopped while the part is hot. It is worth checking whether it really spins."));
        }

        // Discos
        foreach (var d in temps.Where(r => r.HardwareKind == HardwareClassifier.Storage).GroupBy(r => r.Hardware))
        {
            var top = d.MaxBy(r => r.Value)!;
            // Casamento por nome normalizado (sem espaços nem serial) entre a leitura de sensor e o disco físico.
            string dn = NormDisk(d.Key);
            var match = disks?.FirstOrDefault(m => DiskNameMatches(dn, NormDisk(m.Model)));
            bool nvme = d.Key.Contains("nvme", StringComparison.OrdinalIgnoreCase) || match?.Nvme == true;
            bool hdd = !nvme && (match?.Hdd == true || Regex.IsMatch(d.Key, @"\bHDD\b", RegexOptions.IgnoreCase));
            // HDD 55 °C; SSD SATA 70 °C; NVMe 75 °C; sem casamento, 70 °C.
            double limit = nvme ? 75 : hdd ? 55 : 70;
            string kindPt = nvme ? "NVMe" : hdd ? "HDD" : match is not null ? "SSD SATA" : "disco sem tipo conhecido";
            string kindEn = nvme ? "NVMe" : hdd ? "HDD" : match is not null ? "SATA SSD" : "disk of unknown type";
            if (top.Value > limit)
                res.Find(Severity.Aviso, Loc.T($"Disco quente: {d.Key}", $"Hot disk: {d.Key}"),
                    Loc.T($"{top.Name}: {Fmt.Num(top.Value, 0)} °C (limite de referência {limit:0} °C para {kindPt}; leitura única; fonte: {top.Source})", $"{top.Name}: {Fmt.Num(top.Value, 0)} °C (reference limit {limit:0} °C for {kindEn}; single reading; source: {top.Source})"),
                    Loc.T("Discos esquentam sob uso intenso; HDD tolera menos calor que SSD e NVMe tolera mais que SATA. Esta é uma leitura de um instante só; o app não afirma a causa.", "Disks heat up under heavy use; HDDs tolerate less heat than SSDs and NVMe tolerates more than SATA. This is a single-moment reading; the app does not state the cause."),
                    Loc.T("Confira o fluxo de ar perto do disco e, em NVMe, se há dissipador. Repita a leitura em repouso.", "Check the airflow near the disk and, for NVMe, whether there is a heatsink. Repeat the reading at idle."),
                    impact: 20, id: dn.Length > 0 ? "sensors.disk_hot." + dn : "sensors.disk_hot",
                    simple: Loc.T("Um dos discos está mais quente que o ideal. Vale conferir o fluxo de ar perto dele.", "One of the disks is hotter than ideal. It is worth checking the airflow near it."));
        }

        // VRM
        var vrm = temps.Where(r => r.HardwareKind == HardwareClassifier.Board && Regex.IsMatch(r.Name, @"VRM|MOS", RegexOptions.IgnoreCase)).ToList();
        if (vrm.Count > 0 && vrm.MaxBy(r => r.Value) is { Value: > 100 } vt)
            res.Find(Severity.Aviso, Loc.T("Temperatura do VRM alta", "VRM temperature high"),
                Loc.T($"{vt.Name}: {Fmt.Num(vt.Value, 0)} °C (leitura única; fonte: {vt.Source})", $"{vt.Name}: {Fmt.Num(vt.Value, 0)} °C (single reading; source: {vt.Source})"),
                Loc.T("O VRM é o circuito que alimenta a CPU na placa-mãe; acima de 100 °C ele pode limitar a potência da CPU. Leitura de um instante só; o app não afirma a causa.", "The VRM is the circuit that powers the CPU on the motherboard; above 100 °C it may limit the CPU power. Single-moment reading; the app does not state the cause."),
                Loc.T("Confira o fluxo de ar sobre o VRM e o dissipador da placa-mãe.", "Check the airflow over the VRM and the motherboard heatsink."),
                impact: 30, id: "sensors.vrm_hot",
                simple: Loc.T("A parte da placa-mãe que alimenta o processador está muito quente. Vale melhorar o fluxo de ar.", "The part of the motherboard that powers the processor is very hot. It is worth improving the airflow."));

        // Fonte digital
        var psu = readings.Where(r => r.HardwareKind == HardwareClassifier.Psu).Select(r => r.Hardware).FirstOrDefault();
        if (psu is not null)
        {
            facts.PsuDetectedModel = psu;
            facts.PsuDetectedWatts = ParsePsuWatts(psu);
        }
        else { facts.PsuDetectedModel = null; facts.PsuDetectedWatts = null; }
    }

    /// <summary>Temperatura mínima (°C) da peça para o alerta de ventoinha parada.</summary>
    private const double FanTempCut = 70;

    /// <summary>Cabeçalhos de ventoinha opcionais (OPT, SYS_FAN, AIO, bomba, chassis) que costumam ficar sem ventoinha ligada e marcam 0 RPM sem ser defeito.</summary>
    private static readonly Regex OptionalFanRx = new(@"\bopt\b|opt_?\d|sys_?fan|aio|pump|chassis|\bcase\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Chave de comparação: só letras e dígitos, minúsculas.</summary>
    private static string NormKey(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// A ventoinha pertence à peça? CPU: ventoinha da própria peça CPU ou nome exatamente "CPU Fan"/"CPU"/"CPU_FAN" (não OPT/AIO_PUMP/SYS);
    /// GPU: ventoinha da própria placa de vídeo ou nome com "GPU". Cabeçalhos opcionais (OPT, SYS_FAN) parados em 0 são ignorados na varredura única.
    /// </summary>
    private static bool IsRelevantFan(SensorReading r, string piece)
    {
        if (OptionalFanRx.IsMatch(r.Name)) return false;
        if (r.HardwareKind == piece) return true;
        string k = NormKey(r.Name);
        return piece == HardwareClassifier.Cpu ? k is "cpufan" or "cpu" : k.Contains("gpu", StringComparison.Ordinal);
    }

    /// <summary>Prefixos típicos de modelos de fonte (Corsair RM/HX/AX/SF/CX/TX, EVGA/Seasonic etc.) que dão confiança à potência extraída do nome.</summary>
    private static readonly string[] PsuPrefixes = { "RM", "HX", "AX", "SF", "CX", "TX", "VS", "GF", "MWE" };

    /// <summary>
    /// Potência nominal deduzida do nome do modelo (ex.: "Corsair HX850i" = 850, "RM850x" = 850, "AX760" = 760); null se o nome não traz uma potência plausível.
    /// Aceita múltiplos de 10 entre 300 e 2000 e prefere o número colado a um prefixo típico de fonte (RM/HX/AX/SF/CX/TX); nomes de cooler (H100i, H150i) são ignorados.
    /// </summary>
    /// <param name="model">Nome do modelo.</param>
    internal static int? ParsePsuWatts(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        int? best = null;
        double bestScore = -1;
        foreach (Match m in PsuModelRx.Matches(model))
        {
            string prefix = m.Groups[1].Value, suffix = m.Groups[3].Value;
            if (!int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var w)
                || w is < 300 or > 2000 || w % 10 != 0) continue;
            if (prefix.Equals("H", StringComparison.OrdinalIgnoreCase) && suffix.Length <= 1) continue; // cooler Corsair H100i/H150i
            double score = PsuPrefixes.Any(p => prefix.Equals(p, StringComparison.OrdinalIgnoreCase)) ? 3 : prefix.Length > 0 ? 2 : 1;
            if (score > bestScore) { best = w; bestScore = score; }
        }
        return best;
    }
}
