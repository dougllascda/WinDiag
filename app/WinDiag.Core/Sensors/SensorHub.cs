using System.Diagnostics;
using Microsoft.Win32;
using WinDiag.Core.Live;
using WinDiag.Core.Util;

namespace WinDiag.Core.Sensors;

/// <summary>
/// Reúne sensores de temperatura, ventoinha, tensão, potência e clock em uma leitura única, em ordem de prioridade:
/// HWiNFO (memória compartilhada, se aberto) -> LibreHardwareMonitor -> nvidia-smi (só se nenhuma fonte trouxe a GPU).
/// Somente leitura. O WinDiag NÃO baixa nem instala drivers: o driver PawnIO, se existir, foi instalado pelo usuário.
/// O construtor abre a LibreHardwareMonitor e pode levar alguns segundos: crie o hub fora do thread da interface.
/// <see cref="Read"/> leva menos de ~300 ms depois da primeira chamada (o nvidia-smi só é consultado se nada mais leu a GPU).
/// Não é thread-safe: use de um único thread por vez. Nunca lança exceção.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private static readonly TimeSpan StatusRefresh = TimeSpan.FromSeconds(10);
    private readonly LhmSource _lhm = new();
    private GpuLive? _gpu;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private TimeSpan _lastStatus;
    private bool _driver;
    private bool _driverAtOpen;
    private bool _hwOk;
    private bool _disposed;
    private SensorStatus _status = new();

    /// <summary>Situação das fontes. Calculada na construção e reavaliada a cada ~10 s dentro de <see cref="Read"/> (o HWiNFO pode abrir e fechar).</summary>
    public SensorStatus Status => _status;

    /// <summary>Cria o hub, detecta o driver e abre as fontes. Pode levar alguns segundos.</summary>
    public SensorHub()
    {
        try
        {
            _driver = IsPawnIoInstalled();
            _driverAtOpen = _driver;
            try { _lhm.Open(_driver); } catch (Exception) { /* LhmSource já registra a falha */ }
            RefreshStatus();
        }
        catch (Exception) { /* o hub continua utilizável, sem fontes */ }
    }

    /// <summary>true se o serviço PawnIO existe no registro (HKLM\SYSTEM\CurrentControlSet\Services\PawnIO).</summary>
    internal static bool IsPawnIoInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return k is not null;
        }
        catch (Exception) { return false; }
    }

    private void RefreshStatus()
    {
        try
        {
            _driver = IsPawnIoInstalled();
            var hw = HwInfoSource.Read();
            _hwOk = hw is { State: HwInfoParseState.Ok };
            var notes = new List<string>();
            if (!_hwOk)
                notes.Add(hw is { State: HwInfoParseState.Inactive }
                    ? Loc.T("HWiNFO está fechando (memória compartilhada inativa).", "HWiNFO is closing (shared memory inactive).")
                    : hw is { State: HwInfoParseState.Stale }
                    ? Loc.T("HWiNFO aberto, mas parado (sem atualizar).", "HWiNFO is open but stalled (not updating).")
                    : Loc.T("HWiNFO não detectado: abra-o com \"Shared Memory Support\" ligado para ler mais sensores.", "HWiNFO not detected: open it with \"Shared Memory Support\" enabled to read more sensors."));
            notes.AddRange(_lhm.Notes);
            if (_lhm.Available && _driver != _driverAtOpen)
                notes.Add(_driver
                    ? Loc.T("O driver PawnIO foi detectado depois da abertura; reinicie o WinDiag para a LibreHardwareMonitor passar a ler CPU e placa-mãe.", "The PawnIO driver was detected after startup; restart WinDiag so LibreHardwareMonitor can read the CPU and motherboard.")
                    : Loc.T("O driver PawnIO deixou de ser detectado; reinicie o WinDiag.", "The PawnIO driver is no longer detected; restart WinDiag."));
            _status = new SensorStatus
            {
                HwInfoAvailable = _hwOk,
                LhmAvailable = _lhm.Available,
                KernelDriverAvailable = _driver,
                DriverName = _driver ? "PawnIO" : "",
                LhmVersion = _lhm.Version,
                Notes = notes,
            };
        }
        catch (Exception) { /* mantém o status anterior */ }
        _lastStatus = _since.Elapsed;
    }

    /// <summary>Faz uma leitura de todas as fontes e devolve o conjunto sem duplicatas óbvias. Nunca lança exceção.</summary>
    public SensorSnapshot Read()
    {
        var groups = new List<IReadOnlyList<SensorReading>>();
        var used = new List<string>();
        if (_disposed) return new SensorSnapshot { Time = DateTime.Now };
        try
        {
            if (_since.Elapsed - _lastStatus >= StatusRefresh) RefreshStatus();

            try
            {
                var hw = HwInfoSource.Read();
                if (hw is { State: HwInfoParseState.Ok } && hw.Readings.Count > 0)
                {
                    groups.Add(hw.Readings);
                    used.Add(HwInfoSource.SourceName);
                }
            }
            catch (Exception) { }

            try
            {
                var l = _lhm.Read();
                if (l.Count > 0)
                {
                    groups.Add(l);
                    used.Add(LhmSource.SourceName);
                }
            }
            catch (Exception) { }

            bool gpuTemp = groups.Any(g => g.Any(r => r.HardwareKind == HardwareClassifier.Gpu && r.Kind == SensorKind.Temperature));
            if (!gpuTemp)
            {
                var n = ReadNvidia();
                if (n.Count > 0)
                {
                    groups.Add(n);
                    used.Add(NvidiaSource);
                }
            }
        }
        catch (Exception) { /* devolve o que foi lido */ }
        IReadOnlyList<SensorReading> merged;
        try { merged = Merge(groups); } catch (Exception) { merged = []; }
        return new SensorSnapshot { Time = DateTime.Now, Readings = merged, SourcesUsed = used };
    }

    internal const string NvidiaSource = "nvidia-smi";

    private List<SensorReading> ReadNvidia()
    {
        var list = new List<SensorReading>();
        try
        {
            if (NvidiaSmi.Path is null) return list;
            _gpu ??= new GpuLive();
            var s = new LiveSample();
            _gpu.Read(s);
            const string hw = "GPU NVIDIA";
            if (s.GpuTempC is { } t) list.Add(new SensorReading(hw, HardwareClassifier.Gpu, "GPU Temperature", SensorKind.Temperature, t, "°C", NvidiaSource));
            if (s.GpuPowerW is { } p) list.Add(new SensorReading(hw, HardwareClassifier.Gpu, "GPU Power", SensorKind.Power, p, "W", NvidiaSource));
            if (s.GpuFanPct is { } f) list.Add(new SensorReading(hw, HardwareClassifier.Gpu, "GPU Fan", SensorKind.Other, f, "%", NvidiaSource));
        }
        catch (Exception) { /* sem leitura nesta passada */ }
        return list;
    }

    /// <summary>
    /// Junta as fontes (em ordem de prioridade) sem duplicatas óbvias: para a mesma peça (categoria), mesmo nome de sensor
    /// (sem diferenciar maiúsculas e símbolos) e mesmo tipo, fica só a fonte de maior prioridade.
    /// </summary>
    /// <param name="sourcesInPriority">Leituras de cada fonte, da mais prioritária para a menos.</param>
    internal static List<SensorReading> Merge(IEnumerable<IReadOnlyList<SensorReading>> sourcesInPriority)
    {
        var owner = new Dictionary<(string, string, SensorKind), string>();
        var result = new List<SensorReading>();
        foreach (var src in sourcesInPriority)
        {
            // A primeira fonte a reivindicar a chave fica com ela; fontes seguintes só repetem a chave se for a mesma fonte
            // (várias peças iguais, como dois discos, mantêm todas as leituras da fonte dona).
            foreach (var r in src)
            {
                var key = (r.HardwareKind, Norm(r.Name), r.Kind);
                if (owner.TryGetValue(key, out var who) && who != r.Source) continue;
                owner[key] = r.Source;
                result.Add(r);
            }
        }
        return result;
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Libera as fontes (fecha a LibreHardwareMonitor e os contadores da GPU).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lhm.Dispose(); } catch (Exception) { }
        try { _gpu?.Dispose(); } catch (Exception) { }
    }
}
