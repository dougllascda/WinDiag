namespace WinDiag.Core.Live;

/// <summary>Leitura ao vivo (1 por segundo) para os campos que mudam: clocks, carga, RAM, GPU. Campo null = não lido.</summary>
public sealed class LiveSample
{
    /// <summary>Instante da leitura (hora local).</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    // CPU (CpuLive)
    /// <summary>Carga total da CPU, em %.</summary>
    public double? CpuLoadPct { get; set; }
    /// <summary>Clock efetivo médio, em MHz (base x desempenho do processador).</summary>
    public double? CpuEffMHz { get; set; }
    /// <summary>Clock efetivo por thread lógica, em MHz (vazio se não lido).</summary>
    public double[] CpuThreadMHz { get; set; } = Array.Empty<double>();
    /// <summary>Tempo em DPC, em %.</summary>
    public double? DpcPct { get; set; }

    // Memória (CpuLive)
    /// <summary>RAM disponível, em GB.</summary>
    public double? RamAvailGB { get; set; }
    /// <summary>RAM em uso, em % do total.</summary>
    public double? RamUsedPct { get; set; }
    /// <summary>Páginas lidas do disco por segundo (hard faults).</summary>
    public double? HardFaultsPerSec { get; set; }

    // GPU (GpuLive; NVIDIA via nvidia-smi, demais via contadores GPU Engine/Adapter Memory)
    /// <summary>Temperatura da GPU, em °C (somente nvidia-smi).</summary>
    public double? GpuTempC { get; set; }
    /// <summary>Clock do núcleo da GPU, em MHz.</summary>
    public double? GpuCoreMHz { get; set; }
    /// <summary>Clock da memória da GPU, em MHz.</summary>
    public double? GpuMemMHz { get; set; }
    /// <summary>Potência consumida, em W.</summary>
    public double? GpuPowerW { get; set; }
    /// <summary>Uso da GPU, em %.</summary>
    public double? GpuUtilPct { get; set; }
    /// <summary>VRAM em uso, em MiB.</summary>
    public double? VramUsedMiB { get; set; }
    /// <summary>Velocidade da ventoinha, em %.</summary>
    public double? GpuFanPct { get; set; }
    /// <summary>Estado de desempenho (P0, P8...).</summary>
    public string? GpuPState { get; set; }
    /// <summary>Motivos ativos de limitação de clock, em texto.</summary>
    public string? GpuThrottle { get; set; }
    /// <summary>Geração atual do link PCIe.</summary>
    public int? PcieGen { get; set; }
    /// <summary>Largura atual do link PCIe (linhas).</summary>
    public int? PcieWidth { get; set; }
}

/// <summary>
/// Coleta amostras periódicas combinando <see cref="CpuLive"/> e <see cref="GpuLive"/>. Mantém contadores abertos entre as
/// chamadas; use <c>using</c> ou chame <see cref="Dispose"/> ao terminar. Não é thread-safe: chame <see cref="Sample"/> de um único thread por vez.
/// </summary>
public sealed class LiveSampler : IDisposable
{
    /// <summary>Leitor de CPU e memória.</summary>
    private readonly CpuLive _cpu = new();
    /// <summary>Leitor de GPU.</summary>
    private readonly GpuLive _gpu = new();

    /// <summary>Lê uma amostra. Nunca lança exceção; leituras com falha ficam null.</summary>
    public LiveSample Sample()
    {
        var s = new LiveSample();
        try { _cpu.Read(s); } catch (Exception) { /* campos de CPU ficam null */ }
        try { _gpu.Read(s); } catch (Exception) { /* campos de GPU ficam null */ }
        return s;
    }

    /// <summary>Libera os contadores de CPU e GPU.</summary>
    public void Dispose()
    {
        _cpu.Dispose();
        _gpu.Dispose();
    }
}
