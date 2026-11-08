using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Live;

/// <summary>Uma amostra (1 s) da captura durante o jogo. Campo null = contador indisponível.</summary>
public sealed class CaptureSample
{
    /// <summary>Segundo da captura (1 a N).</summary>
    public int Second { get; set; }
    /// <summary>CPU total, em %.</summary>
    public double? CpuPct { get; set; }
    /// <summary>Tempo em DPC, em %.</summary>
    public double? DpcPct { get; set; }
    /// <summary>Tempo em interrupções, em %.</summary>
    public double? InterruptPct { get; set; }
    /// <summary>Disco ocupado (100 - tempo ocioso), em %.</summary>
    public double? DiskBusyPct { get; set; }
    /// <summary>Tamanho médio da fila de disco.</summary>
    public double? DiskQueue { get; set; }
    /// <summary>Páginas lidas do disco por segundo (hard faults).</summary>
    public double? HardFaultsPerSec { get; set; }
    /// <summary>RAM disponível, em GB.</summary>
    public double? RamAvailGB { get; set; }
    /// <summary>VRAM dedicada em uso, em MB (maior adaptador).</summary>
    public double? VramDedicatedMB { get; set; }
    /// <summary>Memória de vídeo compartilhada (RAM) em uso, em MB.</summary>
    public double? VramSharedMB { get; set; }
    /// <summary>Uso do motor 3D da GPU, em %.</summary>
    public double? Gpu3DPct { get; set; }
    // Processo do jogo = pid com maior uso de GPU Engine 3D (o próprio WinDiag é ignorado)
    /// <summary>PID do processo com maior uso de GPU 3D.</summary>
    public int? GamePid { get; set; }
    /// <summary>Nome do processo do jogo.</summary>
    public string? GameName { get; set; }
    /// <summary>CPU do processo do jogo, em % do total de threads.</summary>
    public double? GameCpuPct { get; set; }
    /// <summary>Working set do jogo, em MB.</summary>
    public double? GameWorkingSetMB { get; set; }
    /// <summary>Page faults por segundo do jogo.</summary>
    public double? GamePageFaultsPerSec { get; set; }
    /// <summary>Maior temperatura de CPU lida pelos sensores, em °C (null sem sensor).</summary>
    public double? CpuTempC { get; set; }
    /// <summary>Maior temperatura do núcleo da GPU lida pelos sensores, em °C (null sem sensor).</summary>
    public double? GpuTempC { get; set; }
}

/// <summary>Linha de percentis de uma métrica da captura.</summary>
public sealed class PercentileRow
{
    /// <summary>Nome da métrica.</summary>
    public string Metric { get; set; } = "";
    /// <summary>Unidade (%, MB, GB, /s ou vazio).</summary>
    public string Unit { get; set; } = "";
    /// <summary>Mediana.</summary>
    public double? P50 { get; set; }
    /// <summary>Percentil 95.</summary>
    public double? P95 { get; set; }
    /// <summary>Percentil 99.</summary>
    public double? P99 { get; set; }
    /// <summary>Máximo observado.</summary>
    public double? Max { get; set; }
}

/// <summary>Resumo de uma captura: percentis por métrica, interpretação em texto e amostras brutas.</summary>
public sealed class CaptureSummary
{
    /// <summary>Quantidade de amostras coletadas.</summary>
    public int SampleCount { get; set; }
    /// <summary>true se o usuário cancelou antes do fim.</summary>
    public bool Cancelled { get; set; }
    /// <summary>Nome do processo identificado como o jogo.</summary>
    public string? GameName { get; set; }
    /// <summary>PID do jogo.</summary>
    public int? GamePid { get; set; }
    /// <summary>Percentis por métrica.</summary>
    public List<PercentileRow> Rows { get; } = new();
    /// <summary>Leituras em texto (sem afirmar causa além do que foi medido).</summary>
    public List<string> Interpretation { get; } = new();
    /// <summary>Sugestão sobre PresentMon, se encontrado.</summary>
    public string? PresentMonHint { get; set; }
    /// <summary>Amostras brutas.</summary>
    public List<CaptureSample> Samples { get; } = new();
}

/// <summary>
/// Captura durante o jogo: amostragem somente leitura a 1 por segundo (contadores de desempenho, GlobalMemoryStatusEx)
/// e resumo com percentis. Não exige administrador; contadores indisponíveis deixam o campo null.
/// </summary>
public static class Capture
{
    /// <summary>Durações oferecidas na interface, em segundos.</summary>
    public static readonly int[] DurationOptions = { 30, 60, 120, 300 };
    private static readonly Regex PidRx = new(@"pid_(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Percentil por posto mais próximo (ceil(p/100*n)-1), ignorando nulos e NaN; 0 se não houver valores.</summary>
    /// <param name="values">Valores (nulos são ignorados).</param>
    /// <param name="p">Percentil de 0 a 100.</param>
    public static double Percentile(IEnumerable<double?> values, double p)
    {
        var a = values.Where(v => v.HasValue && !double.IsNaN(v.Value)).Select(v => v!.Value).OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p / 100d * a.Length) - 1;
        if (idx < 0) idx = 0;
        if (idx >= a.Length) idx = a.Length - 1;
        return a[idx];
    }

    /// <summary>Extrai o PID da instância de GPU Engine ("pid_1234_luid_..._engtype_3D" -> 1234).</summary>
    public static int? ParseGpuPid(string? instance)
    {
        if (string.IsNullOrEmpty(instance)) return null;
        var m = PidRx.Match(instance);
        return m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>Procura PresentMon*.exe no PATH e em Program Files. Não baixa nem executa nada.</summary>
    public static string? FindPresentMon()
    {
        try
        {
            var dirs = new List<string>();
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(pf)) continue;
                dirs.Add(pf);
                try { dirs.AddRange(Directory.GetDirectories(pf, "*PresentMon*")); } catch { }
                try { dirs.AddRange(Directory.GetDirectories(pf, "*Intel*").SelectMany(d => Directory.GetDirectories(d, "*PresentMon*"))); } catch { }
            }
            foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var f = Directory.GetFiles(d.Trim('"'), "PresentMon*.exe").FirstOrDefault();
                    if (f is not null) return f;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Executa a captura por <paramref name="seconds"/> segundos, uma amostra por segundo. Nunca lança exceção; cancelamento devolve o resumo parcial com <see cref="CaptureSummary.Cancelled"/>.</summary>
    /// <param name="seconds">Duração.</param>
    /// <param name="progress">Recebe cada amostra (opcional).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <param name="vramTotalMiB">VRAM total, em MiB (ex.: <c>facts.GpuVramMiB</c>), para avaliar saturação (opcional).</param>
    public static async Task<CaptureSummary> RunAsync(int seconds, IProgress<CaptureSample>? progress, CancellationToken ct, double? vramTotalMiB = null)
    {
        var samples = new List<CaptureSample>();
        bool cancelled = false;
        using var rd = new Reader();
        // Sensores (opcional): o hub abre em segundo plano para não atrasar o início; enquanto não estiver pronto, as amostras ficam sem temperatura.
        // Se o App definiu um hub compartilhado, ele é reutilizado (e não descartado); senão a captura cria e descarta o seu.
        Task<Sensors.SensorHub?>? hubTask = null;
        bool ownsHub = true;
        try
        {
            Sensors.SensorHub? shared = null;
            try { shared = Collectors.SensorsCollector.SharedHub?.Invoke(); } catch (Exception) { shared = null; }
            if (shared is not null) { ownsHub = false; hubTask = Task.FromResult<Sensors.SensorHub?>(shared); }
            else hubTask = Task.Run<Sensors.SensorHub?>(() => { try { return new Sensors.SensorHub(); } catch (Exception) { return null; } });
        }
        catch (Exception) { hubTask = null; }
        try
        {
            rd.Prime();
            var sw = Stopwatch.StartNew();
            for (int i = 1; i <= seconds; i++)
            {
                var wait = TimeSpan.FromSeconds(i) - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; break; }
                }
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                CaptureSample s;
                try { s = rd.Read(i); } catch (Exception) { s = new CaptureSample { Second = i }; }
                try
                {
                    if (hubTask is { IsCompletedSuccessfully: true } && hubTask.Result is { } hub)
                    {
                        var (cpuT, gpuT) = Collectors.SensorsCollector.PickMaxTemps(Collectors.SensorsCollector.ReadLocked(hub).Readings);
                        s.CpuTempC = cpuT is null ? null : Math.Round(cpuT.Value, 1);
                        s.GpuTempC = gpuT is null ? null : Math.Round(gpuT.Value, 1);
                    }
                }
                catch (Exception) { /* sem temperatura nesta amostra */ }
                samples.Add(s);
                try { progress?.Report(s); } catch (Exception) { /* erro do observador não derruba a captura */ }
            }
        }
        catch (Exception) { /* resume o que foi coletado */ }
        try
        {
            if (hubTask is not null && ownsHub)
                _ = hubTask.ContinueWith(t => { try { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); } catch (Exception) { } }, TaskScheduler.Default);
        }
        catch (Exception) { }
        var sum = Summarize(samples, vramTotalMiB);
        sum.Cancelled = cancelled;
        return sum;
    }

    /// <summary>Calcula percentis e a interpretação a partir de amostras. Com menos de 5 amostras só devolve as linhas, sem interpretação.</summary>
    /// <param name="s">Amostras da captura.</param>
    /// <param name="vramTotalMiB">VRAM total em MiB; sem ela, a saturação de VRAM não é avaliada.</param>
    /// <param name="logicalThreads">Threads lógicas da CPU (padrão: as do computador); usado para detectar uma thread do jogo saturada.</param>
    public static CaptureSummary Summarize(IReadOnlyList<CaptureSample> s, double? vramTotalMiB = null, int? logicalThreads = null)
    {
        var r = new CaptureSummary { SampleCount = s.Count };
        r.Samples.AddRange(s);
        var pm = FindPresentMon();
        if (pm is not null) r.PresentMonHint = Loc.T("PresentMon encontrado: use-o para medir o tempo de quadro (" + pm + ").", "PresentMon found: use it to measure frame time (" + pm + ").");
        var game = s.Where(x => x.GamePid.HasValue).GroupBy(x => (x.GamePid, x.GameName)).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (game is not null) { r.GamePid = game.Key.GamePid; r.GameName = game.Key.GameName; }

        void Row(string name, string unit, Func<CaptureSample, double?> f)
        {
            var v = s.Select(f).ToList();
            bool any = v.Any(x => x.HasValue);
            r.Rows.Add(new PercentileRow
            {
                Metric = name, Unit = unit,
                P50 = any ? Percentile(v, 50) : null, P95 = any ? Percentile(v, 95) : null,
                P99 = any ? Percentile(v, 99) : null, Max = any ? v.Where(x => x.HasValue).Max() : null
            });
        }
        Row(Loc.T("CPU total", "Total CPU"), "%", x => x.CpuPct);
        Row("GPU 3D", "%", x => x.Gpu3DPct);
        Row("DPC", "%", x => x.DpcPct);
        Row(Loc.T("Interrupções", "Interrupts"), "%", x => x.InterruptPct);
        Row(Loc.T("Disco ocupado", "Disk busy"), "%", x => x.DiskBusyPct);
        Row(Loc.T("Fila de disco", "Disk queue"), "", x => x.DiskQueue);
        Row(Loc.T("Páginas lidas do disco/s (hard faults)", "Pages read from disk/s (hard faults)"), "/s", x => x.HardFaultsPerSec);
        Row(Loc.T("VRAM dedicada usada", "Dedicated VRAM used"), "MB", x => x.VramDedicatedMB);
        Row(Loc.T("VRAM compartilhada (RAM)", "Shared VRAM (RAM)"), "MB", x => x.VramSharedMB);
        Row(Loc.T("RAM disponível", "Available RAM"), "GB", x => x.RamAvailGB);
        // Temperatura máxima por amostra (só aparece quando algum sensor respondeu).
        if (s.Any(x => x.CpuTempC.HasValue)) Row(Loc.T("Temperatura máx. da CPU", "Max CPU temperature"), "°C", x => x.CpuTempC);
        if (s.Any(x => x.GpuTempC.HasValue)) Row(Loc.T("Temperatura máx. da GPU", "Max GPU temperature"), "°C", x => x.GpuTempC);

        if (s.Count < 5)
        {
            r.Interpretation.Add(Loc.T("Poucas amostras para concluir. Tente novamente.", "Too few samples to conclude. Try again."));
            return r;
        }
        double P(Func<CaptureSample, double?> f, double p) => Percentile(s.Select(f), p);
        double Mx(Func<CaptureSample, double?> f) { var v = s.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? 0 : v.Max(); }
        double cpuP95 = P(x => x.CpuPct, 95), gpuP95 = P(x => x.Gpu3DPct, 95);
        double dpcP95 = P(x => x.DpcPct, 95), dpcMax = Mx(x => x.DpcPct);
        double diskP95 = P(x => x.DiskBusyPct, 95);
        double pgP95 = P(x => x.HardFaultsPerSec, 95);
        double shrMax = Mx(x => x.VramSharedMB), dedMax = Mx(x => x.VramDedicatedMB);
        int threads = Math.Max(1, logicalThreads ?? Environment.ProcessorCount);
        double gameCpuP95 = P(x => x.GameCpuPct, 95);
        // GameCpuPct é % do total de threads: 100/threads equivale a um núcleo inteiro; >= 90% disso sugere uma thread saturada.
        bool oneThreadSaturated = gameCpuP95 >= 100d / threads * 0.9;
        bool totalHigh = cpuP95 >= 85;
        int n = 0;
        if ((totalHigh || oneThreadSaturated) && gpuP95 < 85)
        {
            n++;
            string why = totalHigh && oneThreadSaturated ? Loc.T($"CPU total em {Fmt.Num(cpuP95, 0)}% (P95) e o processo do jogo usando o equivalente a um núcleo inteiro ou mais", $"total CPU at {Fmt.Num(cpuP95, 0)}% (P95) and the game process using the equivalent of one full core or more")
                : totalHigh ? Loc.T($"CPU total em {Fmt.Num(cpuP95, 0)}% (P95)", $"total CPU at {Fmt.Num(cpuP95, 0)}% (P95)")
                : Loc.T($"o processo do jogo usa o equivalente a um núcleo inteiro ({Fmt.Num(gameCpuP95, 1)}% do total, P95), então uma thread pode estar saturada mesmo com a CPU total baixa (o uso por thread não é medido aqui)", $"the game process uses the equivalent of one full core ({Fmt.Num(gameCpuP95, 1)}% of the total, P95), so a thread may be saturated even with low total CPU (per-thread usage is not measured here)");
            r.Interpretation.Add(Loc.T($"CPU no limite com a GPU folgada ({why}; GPU 3D em {Fmt.Num(gpuP95, 0)}%, P95): o jogo parece limitado pela CPU (CPU-bound). Engasgos podem aparecer quando a CPU satura. Reduza configurações que pesam na CPU (distância de visão, NPCs, sombras), feche navegador/Discord e evite overlays.",
                $"CPU at its limit with the GPU underused ({why}; 3D GPU at {Fmt.Num(gpuP95, 0)}%, P95): the game appears CPU-bound. Stutter may appear when the CPU saturates. Lower CPU-heavy settings (view distance, NPCs, shadows), close the browser/Discord and avoid overlays."));
        }
        else if (gpuP95 >= 95)
        { n++; r.Interpretation.Add(Loc.T("GPU no limite: o jogo está limitado pela GPU, o que é esperado em jogos. Se há engasgos mesmo assim, observe os demais indicadores.", "GPU at its limit: the game is GPU-bound, which is expected in games. If you still see stutter, look at the other indicators.")); }
        if (diskP95 >= 90)
        { n++; r.Interpretation.Add(Loc.T("Disco com ocupação alta durante a captura. Leituras lentas enquanto o jogo carrega dados podem causar engasgos. Se o jogo está em HDD, considere movê-lo para SSD.", "Disk busy time was high during the capture. Slow reads while the game loads data can cause stutter. If the game is on an HDD, consider moving it to an SSD.")); }
        if (pgP95 >= 500)
        { n++; r.Interpretation.Add(Loc.T("Muitas leituras de páginas do disco (paginação): sinal de RAM insuficiente no momento. Feche programas que usam RAM.", "Many page reads from disk (paging): a sign of insufficient RAM at the time. Close programs that use RAM.")); }
        // VRAM cheia: dedicada >= 95% do total. Memória compartilhada (RAM) só conta se a dedicada também estiver alta (>= 90%).
        bool vramFull = vramTotalMiB is > 0 && dedMax >= vramTotalMiB.Value * 0.95;
        bool sharedWithDedicatedHigh = shrMax >= 512 && vramTotalMiB is > 0 && dedMax >= vramTotalMiB.Value * 0.90;
        if (vramFull)
        { n++; r.Interpretation.Add(Loc.T($"VRAM dedicada próxima do limite ({Fmt.Num(dedMax, 0)} de {Fmt.Num(vramTotalMiB!.Value, 0)} MiB, máximo da captura): sinal de VRAM insuficiente. Reduza a qualidade das texturas.", $"Dedicated VRAM close to the limit ({Fmt.Num(dedMax, 0)} of {Fmt.Num(vramTotalMiB!.Value, 0)} MiB, capture maximum): a sign of insufficient VRAM. Lower texture quality.")); }
        else if (sharedWithDedicatedHigh)
        { n++; r.Interpretation.Add(Loc.T($"VRAM dedicada alta ({Fmt.Num(dedMax, 0)} de {Fmt.Num(vramTotalMiB!.Value, 0)} MiB) junto com uso de memória compartilhada da GPU em RAM (até {Fmt.Num(shrMax, 0)} MB): a GPU pode estar usando a RAM como extensão da VRAM. Reduza a qualidade das texturas.", $"High dedicated VRAM ({Fmt.Num(dedMax, 0)} of {Fmt.Num(vramTotalMiB!.Value, 0)} MiB) together with shared GPU memory in RAM (up to {Fmt.Num(shrMax, 0)} MB): the GPU may be using RAM as an extension of VRAM. Lower texture quality.")); }
        if (dpcP95 >= 3 || dpcMax >= 10)
        { n++; r.Interpretation.Add(Loc.T("DPC/interrupções elevados: algum driver pode estar ocupando a CPU em rotinas de kernel. Este utilitário não identifica qual; use o LatencyMon para isso.", "Elevated DPC/interrupts: a driver may be occupying the CPU in kernel routines. This utility does not identify which one; use LatencyMon for that.")); }
        double cpuTMax = Mx(x => x.CpuTempC), gpuTMax = Mx(x => x.GpuTempC);
        if (cpuTMax >= 95 || gpuTMax >= 87)
        {
            n++;
            var parts = new List<string>();
            if (cpuTMax >= 95) parts.Add(Loc.T($"CPU em {Fmt.Num(cpuTMax, 0)} °C", $"CPU at {Fmt.Num(cpuTMax, 0)} °C"));
            if (gpuTMax >= 87) parts.Add(Loc.T($"GPU em {Fmt.Num(gpuTMax, 0)} °C", $"GPU at {Fmt.Num(gpuTMax, 0)} °C"));
            r.Interpretation.Add(Loc.T($"Temperatura máxima alta durante a captura ({string.Join(" e ", parts)}): provável limitação térmica, em que a peça reduz o clock para se proteger. A inferência vem só da temperatura; o clock não foi medido junto. Confira refrigeração, poeira e fluxo de ar do gabinete.", $"High peak temperature during the capture ({string.Join(" and ", parts)}): likely thermal throttling, where the part lowers its clock to protect itself. The inference comes from temperature only; the clock was not measured alongside. Check cooling, dust and case airflow."));
        }
        if (n == 0)
            r.Interpretation.Add(Loc.T("Nenhum dos indicadores medidos ficou fora do normal durante a captura. Se você sentiu engasgos, a causa pode estar em algo que este utilitário não mede (tempo de quadro do jogo, compilação de shaders, monitores com taxas diferentes). Use PresentMon/CapFrameX para ver os quadros.", "None of the measured indicators was outside the normal range during the capture. If you felt stutter, the cause may be something this utility does not measure (game frame time, shader compilation, monitors with different refresh rates). Use PresentMon/CapFrameX to see the frames."));
        return r;
    }

    // ---------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private sealed class Reader : IDisposable
    {
        private PerformanceCounter? _cpu, _dpc, _int, _idle, _queue, _pages;
        private readonly List<PerformanceCounter> _vramDed = new(), _vramShr = new();
        private PerformanceCounterCategory? _gpuEng;
        private Dictionary<string, CounterSample> _prevEng = new();
        private bool _engFailed;

        private int _gamePid;
        private string? _gameName;
        private TimeSpan _lastCpuTime;
        private DateTime _lastCpuAt;
        private PerformanceCounter? _gamePf;

        private static PerformanceCounter? Mk(string cat, string ctr, string? inst)
        {
            try { return inst is null ? new PerformanceCounter(cat, ctr, true) : new PerformanceCounter(cat, ctr, inst, true); }
            catch (Exception) { return null; }
        }

        public void Prime()
        {
            _cpu = Mk("Processor Information", "% Processor Time", "_Total");
            _dpc = Mk("Processor Information", "% DPC Time", "_Total");
            _int = Mk("Processor Information", "% Interrupt Time", "_Total");
            _idle = Mk("PhysicalDisk", "% Idle Time", "_Total");
            _queue = Mk("PhysicalDisk", "Current Disk Queue Length", "_Total");
            _pages = Mk("Memory", "Pages Input/sec", null);
            foreach (var c in new[] { _cpu, _dpc, _int, _idle, _queue, _pages }) Next(c);
            try
            {
                if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                    foreach (var inst in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames())
                    {
                        var d = Mk("GPU Adapter Memory", "Dedicated Usage", inst); if (d is not null) _vramDed.Add(d);
                        var s = Mk("GPU Adapter Memory", "Shared Usage", inst); if (s is not null) _vramShr.Add(s);
                    }
            }
            catch { }
            try
            {
                if (PerformanceCounterCategory.Exists("GPU Engine")) _gpuEng = new PerformanceCounterCategory("GPU Engine");
                else _engFailed = true;
            }
            catch (Exception) { _engFailed = true; }
            ReadEngines(out _, out _); // guarda a primeira amostra bruta
        }

        private static double? Next(PerformanceCounter? c)
        {
            try { return c is null ? null : Math.Round(c.NextValue(), 1); } catch (Exception) { return null; }
        }

        public CaptureSample Read(int second)
        {
            var s = new CaptureSample { Second = second };
            s.CpuPct = Clamp(Next(_cpu));
            s.DpcPct = Clamp(Next(_dpc));
            s.InterruptPct = Clamp(Next(_int));
            var idle = Next(_idle);
            s.DiskBusyPct = idle.HasValue ? Clamp(100 - idle.Value) : null;
            s.DiskQueue = Next(_queue);
            s.HardFaultsPerSec = Next(_pages);
            try
            {
                var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref m)) s.RamAvailGB = Math.Round(m.ullAvailPhys / 1073741824d, 2);
            }
            catch { }
            double? Best(List<PerformanceCounter> l)
            {
                double? best = null;
                foreach (var c in l) { var v = Next(c); if (v.HasValue && (best is null || v > best)) best = v; }
                return best.HasValue ? Math.Round(best.Value / 1048576d, 0) : null;
            }
            s.VramDedicatedMB = Best(_vramDed);
            s.VramSharedMB = Best(_vramShr);
            try
            {
                ReadEngines(out var total, out var top);
                s.Gpu3DPct = total.HasValue ? Math.Min(100, Math.Round(total.Value, 0)) : null;
                if (top.HasValue) ReadGame(top.Value, s);
            }
            catch { }
            return s;
        }

        private static double? Clamp(double? v) => v.HasValue ? Math.Max(0, Math.Min(100, v.Value)) : null;

        private void ReadEngines(out double? total, out int? topPid)
        {
            total = null; topPid = null;
            if (_gpuEng is null || _engFailed) return;
            try
            {
                var data = _gpuEng.ReadCategory();
                if (!data.Contains("Utilization Percentage")) return;
                var cur = new Dictionary<string, CounterSample>();
                var perPid = new Dictionary<int, double>();
                double sum = 0; bool got = false;
                foreach (InstanceData d in data["Utilization Percentage"].Values)
                {
                    if (d.InstanceName is null || d.InstanceName.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    cur[d.InstanceName] = d.Sample;
                    if (!_prevEng.TryGetValue(d.InstanceName, out var prev)) continue;
                    double v = CounterSample.Calculate(prev, d.Sample);
                    if (double.IsNaN(v) || v < 0) continue;
                    got = true; sum += v;
                    var pid = ParseGpuPid(d.InstanceName);
                    if (pid.HasValue && pid.Value != Environment.ProcessId) perPid[pid.Value] = perPid.GetValueOrDefault(pid.Value) + v;
                }
                _prevEng = cur;
                if (got) total = sum;
                if (perPid.Count > 0)
                {
                    var best = perPid.OrderByDescending(kv => kv.Value).First();
                    if (best.Value > 0) topPid = best.Key;
                }
            }
            catch (Exception) { _engFailed = true; }
        }

        private void ReadGame(int pid, CaptureSample s)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var name = p.ProcessName;
                s.GamePid = pid; s.GameName = name;
                try { s.GameWorkingSetMB = Math.Round(p.WorkingSet64 / 1048576d, 0); } catch { }
                try
                {
                    var cpu = p.TotalProcessorTime; var now = DateTime.UtcNow;
                    if (pid == _gamePid && _lastCpuAt != default)
                    {
                        var el = (now - _lastCpuAt).TotalSeconds;
                        if (el > 0) s.GameCpuPct = Math.Round(Math.Min(100, (cpu - _lastCpuTime).TotalSeconds / el / Environment.ProcessorCount * 100), 1);
                    }
                    _lastCpuTime = cpu; _lastCpuAt = now;
                }
                catch (Exception) { _lastCpuAt = default; }
                if (pid != _gamePid || _gameName != name)
                {
                    _gamePf?.Dispose(); _gamePf = null;
                    _gamePid = pid; _gameName = name;
                    _gamePf = FindPageFaultCounter(pid, name);
                    Next(_gamePf);
                }
                else s.GamePageFaultsPerSec = Next(_gamePf);
            }
            catch { }
        }

        private static PerformanceCounter? FindPageFaultCounter(int pid, string name)
        {
            try
            {
                var cat = new PerformanceCounterCategory("Process");
                foreach (var inst in cat.GetInstanceNames().Where(i => i.Equals(name, StringComparison.OrdinalIgnoreCase) || i.StartsWith(name + "#", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var idc = new PerformanceCounter("Process", "ID Process", inst, true);
                        if ((int)idc.RawValue == pid) return new PerformanceCounter("Process", "Page Faults/sec", inst, true);
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            foreach (var c in new[] { _cpu, _dpc, _int, _idle, _queue, _pages, _gamePf }) { try { c?.Dispose(); } catch { } }
            foreach (var c in _vramDed.Concat(_vramShr)) { try { c.Dispose(); } catch { } }
        }
    }
}
