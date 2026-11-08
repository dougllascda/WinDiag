using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinDiag.Core.Util;

namespace WinDiag.Core.Live;

/// <summary>
/// Leitura ao vivo de CPU e memória. Contadores de desempenho criados uma vez e reutilizados;
/// se a criação falhar (nomes localizados, serviço desligado), cai para WMI (Win32_PerfFormattedData_*, nomes fixos).
/// Contadores de taxa precisam de duas leituras: a primeira Read pode devolver CPU/DPC/clock nulos.
/// RAM disponível e % usada vêm de GlobalMemoryStatusEx (P/Invoke documentado).
/// </summary>
public sealed class CpuLive : IDisposable
{
    private const string Cat = "Processor Information";
    private PerformanceCounter? _load, _perf, _dpc, _pagesIn;
    private List<PerformanceCounter> _threads = new();
    private bool _initialized, _useWmi;
    private double _baseMHz;
    private long _primedAt;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>
    /// Preenche em <paramref name="s"/> carga, clock efetivo (por thread e total), DPC, hard faults e RAM. Não exige administrador.
    /// Campos que não puderam ser lidos ficam null. Na primeira chamada os contadores de taxa são apenas inicializados.
    /// </summary>
    /// <param name="s">Amostra a preencher.</param>
    public void Read(LiveSample s)
    {
        try { ReadMemory(s); } catch (Exception) { /* RAM fica null */ }
        try
        {
            if (!_initialized) Init();
            ReadRates(s);
        }
        catch (Exception) { /* taxas ficam null nesta amostra */ }
    }

    private static void ReadMemory(LiveSample s)
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return;
        s.RamAvailGB = Math.Round(m.ullAvailPhys / 1073741824d, 2);
        s.RamUsedPct = Math.Round((1d - (double)m.ullAvailPhys / m.ullTotalPhys) * 100d, 1);
    }

    private void Init()
    {
        _initialized = true;
        try
        {
            _baseMHz = Wmi.First("SELECT MaxClockSpeed FROM Win32_Processor")?.D("MaxClockSpeed") ?? 0;
            if (_baseMHz <= 0)
                _baseMHz = Convert.ToDouble(Reg.Get(@"HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz") ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception) { _baseMHz = 0; }

        try
        {
            _load = new PerformanceCounter(Cat, "% Processor Time", "_Total", true);
            _perf = new PerformanceCounter(Cat, "% Processor Performance", "_Total", true);
            _dpc = new PerformanceCounter(Cat, "% DPC Time", "_Total", true);
            _pagesIn = new PerformanceCounter("Memory", "Pages Input/sec", true);
            var names = new PerformanceCounterCategory(Cat).GetInstanceNames()
                .Where(n => Regex.IsMatch(n, @"^0,\d+$"))
                .OrderBy(n => int.Parse(n.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            foreach (var n in names) _threads.Add(new PerformanceCounter(Cat, "% Processor Performance", n, true));
            // Escorva: a primeira leitura de contadores de taxa é sempre 0.
            _load.NextValue(); _perf.NextValue(); _dpc.NextValue(); _pagesIn.NextValue();
            foreach (var t in _threads) t.NextValue();
            _primedAt = Environment.TickCount64;
        }
        catch (Exception)
        {
            DisposeCounters();
            _useWmi = true;
        }
    }

    private void ReadRates(LiveSample s)
    {
        if (_useWmi) { ReadWmi(s); return; }
        if (_primedAt != 0 && Environment.TickCount64 - _primedAt < 400) return; // intervalo curto demais: devolve nulos
        s.CpuLoadPct = Round(_load!.NextValue());
        var perf = _perf!.NextValue();
        s.CpuEffMHz = _baseMHz > 0 ? Math.Round(_baseMHz * perf / 100d) : null;
        s.DpcPct = Round(_dpc!.NextValue());
        s.HardFaultsPerSec = Round(_pagesIn!.NextValue());
        if (_baseMHz > 0 && _threads.Count > 0)
            s.CpuThreadMHz = _threads.Select(t => Math.Round(_baseMHz * t.NextValue() / 100d)).ToArray();
    }

    private void ReadWmi(LiveSample s)
    {
        // Os objetos WMI são liberados em cada leitura: o LiveSampler roda a cada 1 s e não pode vazar handles.
        using (var total = Wmi.First("SELECT * FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'"))
        {
            if (total is not null)
            {
                s.CpuLoadPct = total.D("PercentProcessorTime");
                s.DpcPct = total.D("PercentDPCTime");
                var pp = total.D("PercentProcessorPerformance");
                if (pp is not null && _baseMHz > 0) s.CpuEffMHz = Math.Round(_baseMHz * pp.Value / 100d);
            }
        }
        var all = Wmi.Query("SELECT Name, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation");
        try
        {
            var th = all.Where(o => Regex.IsMatch(o.S("Name") ?? "", @"^0,\d+$"))
                .OrderBy(o => int.Parse(o.S("Name")!.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (th.Count > 0 && _baseMHz > 0)
                s.CpuThreadMHz = th.Select(o => Math.Round(_baseMHz * (o.D("PercentProcessorPerformance") ?? 0) / 100d)).ToArray();
        }
        finally { Wmi.DisposeAll(all); }
        using var mem = Wmi.First("SELECT PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory");
        s.HardFaultsPerSec = mem?.D("PagesInputPersec");
    }

    private static double Round(float v) => Math.Round(v, 1);

    private void DisposeCounters()
    {
        _load?.Dispose(); _perf?.Dispose(); _dpc?.Dispose(); _pagesIn?.Dispose();
        foreach (var t in _threads) t.Dispose();
        _load = _perf = _dpc = _pagesIn = null;
        _threads = new();
    }

    /// <summary>Libera todos os contadores de desempenho.</summary>
    public void Dispose() => DisposeCounters();
}
