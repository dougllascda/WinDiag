using System.Diagnostics;
using System.Globalization;
using WinDiag.Core.Util;

namespace WinDiag.Core.Live;

/// <summary>
/// Leitura ao vivo da GPU. NVIDIA: uma chamada de nvidia-smi por leitura (timeout 2 s).
/// Demais: contadores "GPU Engine" (soma do engtype_3D) e "GPU Adapter Memory" (Dedicated Usage), se existirem.
/// </summary>
public sealed class GpuLive : IDisposable
{
    private const string Base = "temperature.gpu,clocks.gr,clocks.mem,power.draw,utilization.gpu,memory.used,fan.speed,pstate";
    private const string Tail = "pcie.link.gen.current,pcie.link.width.current";
    private static readonly string[] Variants =
    {
        Base + ",clocks_event_reasons.active," + Tail,
        Base + ",clocks_throttle_reasons.active," + Tail,
    };

    private int _variant = -1;       // variante de consulta que funcionou (-1 = ainda não definida)
    private int _smiFailures;        // falhas seguidas do nvidia-smi (zera no primeiro sucesso)
    private long _smiRetryAt;        // Environment.TickCount64 a partir do qual o nvidia-smi é tentado de novo
    private const int RetryMs = 10000;
    private const int DiscoveryTimeoutMs = 5000;
    private bool _hasReasons = true;

    /// <summary>Falhas seguidas do nvidia-smi desde a última leitura bem-sucedida.</summary>
    public int SmiFailures => _smiFailures;

    private List<PerformanceCounter>? _eng;
    private int _engAge;
    private List<PerformanceCounter>? _mem;
    private bool _countersDisabled;

    /// <summary>
    /// Preenche em <paramref name="s"/> temperatura, clocks, potência, uso, VRAM, ventoinha, P-state, limitação e link PCIe da GPU.
    /// Prefere nvidia-smi; sem ele (ou se falhar) usa contadores de desempenho. Não exige administrador. Campos ilegíveis ficam null.
    /// </summary>
    /// <param name="s">Amostra a preencher.</param>
    public void Read(LiveSample s)
    {
        try
        {
            var smi = NvidiaSmi.Path;
            if (smi is not null && Environment.TickCount64 >= _smiRetryAt)
            {
                if (ReadNvidia(smi, s)) { _smiFailures = 0; return; }
                _smiFailures++;
                _smiRetryAt = Environment.TickCount64 + RetryMs; // nova tentativa em ~10 s; enquanto isso usa os contadores
            }
        }
        catch (Exception) { /* cai para os contadores */ }
        try { ReadCounters(s); } catch (Exception) { /* GPU fica sem leitura nesta amostra */ }
    }

    private static string Query(int v)
    {
        if (v == 0) return Variants[0];
        if (v == 1) return Variants[1];
        return Base + "," + Tail; // sem motivos de limitação
    }

    private bool ReadNvidia(string smi, LiveSample s)
    {
        string?[]? row = null;
        int used = _variant;
        if (_variant >= 0)
        {
            row = Run(smi, Query(_variant), 2000);
        }
        else
        {
            for (int v = 0; v < 3 && row is null; v++)
            {
                row = Run(smi, Query(v), DiscoveryTimeoutMs);
                if (row is not null) used = v;
            }
            if (row is null) return false;
            _variant = used;
            _hasReasons = used < 2;
        }
        if (row is null) return false; // falha pontual: tenta contadores nesta amostra

        int i = 0;
        s.GpuTempC = D(row[i++]);
        s.GpuCoreMHz = D(row[i++]);
        s.GpuMemMHz = D(row[i++]);
        s.GpuPowerW = D(row[i++]);
        s.GpuUtilPct = D(row[i++]);
        s.VramUsedMiB = D(row[i++]);
        s.GpuFanPct = D(row[i++]);
        s.GpuPState = row[i++];
        if (_hasReasons) s.GpuThrottle = NvidiaSmi.DescribeEventReasons(row[i++]);
        var gen = D(row[i++]); var w = D(row[i++]);
        s.PcieGen = gen is null ? null : (int)gen.Value;
        s.PcieWidth = w is null ? null : (int)w.Value;
        return true;
    }

    private static string?[]? Run(string smi, string fields, int timeoutMs)
    {
        var r = Proc.Run(smi, $"--query-gpu={fields} --format=csv,noheader,nounits", timeoutMs, System.Text.Encoding.UTF8);
        if (!r.Ok) return null;
        var line = r.Lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null || line.Contains("not a valid", StringComparison.OrdinalIgnoreCase)) return null;
        var row = NvidiaSmi.ParseCsvLine(line);
        return row.Length == fields.Split(',').Length ? row : null;
    }

    private static double? D(string? t) =>
        double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private void ReadCounters(LiveSample s)
    {
        if (_countersDisabled) return;
        try
        {
            bool justBuilt = false;
            if (_eng is null || ++_engAge > 30) { BuildCounters(); justBuilt = true; }
            if (_eng is { Count: > 0 })
            {
                double sum = 0; var dead = new List<PerformanceCounter>();
                foreach (var c in _eng)
                {
                    try { sum += c.NextValue(); } catch (Exception) { dead.Add(c); }
                }
                foreach (var c in dead) { _eng.Remove(c); c.Dispose(); }
                // Contadores recém-criados devolvem 0 na primeira leitura: não publica uso nessa amostra.
                if (!justBuilt) s.GpuUtilPct = Math.Min(100, Math.Round(sum, 1));
            }
            if (_mem is { Count: > 0 })
            {
                double best = 0;
                foreach (var c in _mem)
                {
                    try { best = Math.Max(best, c.NextValue()); } catch (Exception) { /* instância sumiu */ }
                }
                if (!justBuilt) s.VramUsedMiB = Math.Round(best / 1048576d, 0);
            }
        }
        catch (Exception) { _countersDisabled = true; }
    }

    private void BuildCounters()
    {
        DisposeCounters();
        _engAge = 0;
        _eng = new List<PerformanceCounter>();
        _mem = new List<PerformanceCounter>();
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames().Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)))
                {
                    try { _eng.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true)); } catch { }
                }
            }
        }
        catch { }
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
            {
                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                foreach (var inst in cat.GetInstanceNames())
                {
                    try { _mem.Add(new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true)); } catch { }
                }
            }
        }
        catch { }
        if (_eng.Count == 0 && _mem.Count == 0) _countersDisabled = true;
    }

    private void DisposeCounters()
    {
        if (_eng is not null) foreach (var c in _eng) { try { c.Dispose(); } catch { } }
        if (_mem is not null) foreach (var c in _mem) { try { c.Dispose(); } catch { } }
        _eng = null; _mem = null;
    }

    /// <summary>Libera os contadores de desempenho da GPU.</summary>
    public void Dispose() => DisposeCounters();
}
