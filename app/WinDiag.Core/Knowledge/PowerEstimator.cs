using System.Text.RegularExpressions;
using WinDiag.Core.Model;

namespace WinDiag.Core.Knowledge;

/// <summary>Estimativa de consumo do PC e de fonte (PSU) recomendada. Valores em watts.</summary>
/// <param name="CpuW">Consumo estimado da CPU em picos de turbo.</param>
/// <param name="GpuW">Consumo estimado da GPU em carga máxima.</param>
/// <param name="RestW">Placa-mãe, memória, ventoinhas e discos.</param>
/// <param name="PeakW">Soma dos três: pico estimado do sistema.</param>
/// <param name="RecommendedW">Fonte recomendada (pico x 1,5, arredondado para cima ao múltiplo de 50 W, mínimo 400 W).</param>
/// <param name="Basis">Texto que explica de onde vieram os números e o que foi suposto.</param>
public sealed record PowerEstimate(int CpuW, int GpuW, int RestW, int PeakW, int RecommendedW, string Basis);

/// <summary>
/// Estima o consumo máximo do PC a partir de TDP da CPU, limite de potência da GPU e discos.
/// É uma estimativa conservadora, não uma medição de consumo real.
/// </summary>
public static class PowerEstimator
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Potência gráfica típica (TGP) por modelo. Padrões mais específicos vêm antes.</summary>
    private static readonly (Regex Pattern, int Watts)[] GpuTable =
    {
        // Variantes Ti/Super/XT/XTX vêm sempre antes do modelo básico.
        (new(@"RTX\s*5090", Rx), 575), (new(@"RTX\s*5080", Rx), 360), (new(@"RTX\s*5070\s*Ti", Rx), 300), (new(@"RTX\s*5070", Rx), 250),
        (new(@"RTX\s*5060\s*Ti", Rx), 180), (new(@"RTX\s*5060", Rx), 145), (new(@"RTX\s*5050", Rx), 130),
        (new(@"RTX\s*4090", Rx), 450), (new(@"RTX\s*4080\s*Super", Rx), 320), (new(@"RTX\s*4080", Rx), 320),
        (new(@"RTX\s*4070\s*Ti\s*Super", Rx), 285), (new(@"RTX\s*4070\s*Ti", Rx), 285), (new(@"RTX\s*4070\s*Super", Rx), 220), (new(@"RTX\s*4070", Rx), 200),
        (new(@"RTX\s*4060\s*Ti", Rx), 160), (new(@"RTX\s*4060", Rx), 115),
        (new(@"RTX\s*3090\s*Ti", Rx), 450), (new(@"RTX\s*3090", Rx), 350), (new(@"RTX\s*3080\s*Ti", Rx), 350), (new(@"RTX\s*3080", Rx), 320),
        (new(@"RTX\s*3070\s*Ti", Rx), 290), (new(@"RTX\s*3070", Rx), 220), (new(@"RTX\s*3060\s*Ti", Rx), 200),
        (new(@"RTX\s*3060", Rx), 170), (new(@"RTX\s*3050", Rx), 130),
        (new(@"RTX\s*20[78]0", Rx), 250), (new(@"RTX\s*2060", Rx), 160), (new(@"GTX\s*1660", Rx), 125), (new(@"GTX\s*1650", Rx), 75),
        (new(@"GTX\s*1080\s*Ti", Rx), 250), (new(@"GTX\s*10[78]0", Rx), 180), (new(@"GTX\s*1060", Rx), 120),
        (new(@"RX\s*9070\s*XT", Rx), 304), (new(@"RX\s*9070", Rx), 220), (new(@"RX\s*9060\s*XT", Rx), 160),
        (new(@"RX\s*7900\s*XTX", Rx), 355), (new(@"RX\s*7900\s*XT", Rx), 315), (new(@"RX\s*7900", Rx), 300),
        (new(@"RX\s*580\b", Rx), 185),
        (new(@"RX\s*7800", Rx), 263), (new(@"RX\s*7700", Rx), 245), (new(@"RX\s*7600", Rx), 165),
        (new(@"RX\s*6[89]\d0", Rx), 300), (new(@"RX\s*6700", Rx), 230), (new(@"RX\s*66\d0", Rx), 132), (new(@"RX\s*6[45]00", Rx), 107),
        (new(@"Arc.*\bB580\b", Rx), 190), (new(@"Arc.*\bA7[57]0\b", Rx), 225),
    };

    /// <summary>
    /// Estima o consumo. Devolve null se não há dado nenhum de CPU nem de GPU para estimar.
    /// </summary>
    /// <param name="f">Fatos coletados.</param>
    public static PowerEstimate? Estimate(SystemFacts f)
    {
        if (string.IsNullOrWhiteSpace(f.CpuName) && string.IsNullOrWhiteSpace(f.GpuName) && f.CpuTdpW is null && f.GpuPowerLimitW is null)
            return null;

        var notes = new List<string>();

        // CPU
        int? tdp = f.CpuTdpW ?? SafeTdp(f.CpuName);
        int cpu;
        if (tdp is { } t)
        {
            cpu = (int)Math.Ceiling(t * 1.5);
            notes.Add(Loc.T($"CPU: TDP de {t} W x 1,5 para picos de turbo = {cpu} W", $"CPU: TDP of {t} W x 1.5 for turbo peaks = {cpu} W"));
        }
        else
        {
            cpu = 95;
            notes.Add(Loc.T("CPU: modelo fora da tabela interna, assumidos 95 W", "CPU: model not in the internal table, 95 W assumed"));
        }

        // GPU
        int gpu;
        if (f.GpuPowerLimitW is { } lim && lim > 0)
        {
            gpu = (int)Math.Round(lim, MidpointRounding.AwayFromZero);
            notes.Add(Loc.T($"GPU: limite de potência medido pelo driver = {gpu} W", $"GPU: power limit measured by the driver = {gpu} W"));
        }
        else if (IsIntegratedGpu(f.GpuName))
        {
            gpu = 15;
            notes.Add(Loc.T("GPU: integrada (sem VRAM dedicada), considerados 15 W", "GPU: integrated (no dedicated VRAM), 15 W assumed"));
        }
        else if (GpuFromTable(f.GpuName) is { } tw)
        {
            gpu = tw;
            notes.Add(Loc.T($"GPU: potência típica do modelo pela tabela interna = {gpu} W", $"GPU: typical power of the model from the internal table = {gpu} W"));
        }
        else
        {
            gpu = 150;
            notes.Add(string.IsNullOrWhiteSpace(f.GpuName)
                ? Loc.T("GPU: não identificada, reservados 150 W", "GPU: not identified, 150 W reserved")
                : Loc.T("GPU: modelo fora da tabela interna e sem leitura de potência, reservados 150 W", "GPU: model not in the internal table and no power reading, 150 W reserved"));
        }

        // Resto
        int hdd = Math.Max(0, f.HddCount), ssd = Math.Max(0, f.SsdCount);
        int rest = 50 + 10 * hdd + 5 * ssd;
        notes.Add(Loc.T($"Resto: 50 W (placa, RAM, ventoinhas) + {hdd} HDD x 10 W + {ssd} SSD x 5 W = {rest} W", $"Rest: 50 W (board, RAM, fans) + {hdd} HDD x 10 W + {ssd} SSD x 5 W = {rest} W"));

        int peak = cpu + gpu + rest;
        int rec = Math.Max(400, (int)(Math.Ceiling(peak * 1.5 / 50.0) * 50));
        string basis = string.Join("; ", notes) + Loc.T(
            $". Pico estimado = {peak} W; recomendado = pico x 1,5 arredondado para cima a múltiplos de 50 W (mínimo 400 W) = {rec} W. " +
            "É uma estimativa conservadora, não uma medição do consumo real.",
            $". Estimated peak = {peak} W; recommended = peak x 1.5 rounded up to multiples of 50 W (minimum 400 W) = {rec} W. " +
            "This is a conservative estimate, not a measurement of real consumption.");
        return new PowerEstimate(cpu, gpu, rest, peak, rec, basis);
    }

    private static readonly Regex[] IntegratedPatterns =
    {
        new(@"Intel.*\b(UHD|Iris|HD)\b.*Graphics|Intel.*Iris", Rx),
        new(@"Intel.*\bArc\b(?!.*\b[AB]\d{3}\b).*Graphics", Rx),
        new(@"Radeon(\s*\(TM\))?\s+Graphics\b", Rx),
        new(@"Vega\s*\d+\s*Graphics|Radeon(\s+RX)?\s+Vega\s*(3|6|7|8|10|11)\b", Rx),
    };

    /// <summary>true para GPU integrada (Intel UHD/Iris/Arc integrada, AMD Radeon Graphics/Vega integrada), que consome pouco: o app considera 15 W.</summary>
    private static bool IsIntegratedGpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var p in IntegratedPatterns)
        {
            try { if (p.IsMatch(name)) return true; } catch { }
        }
        return false;
    }

    private static int? SafeTdp(string? cpuName)
    {
        try { return PlatformDb.ResolveCpu(cpuName)?.TdpW; } catch { return null; }
    }

    private static int? GpuFromTable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var (p, w) in GpuTable)
        {
            try { if (p.IsMatch(name)) return w; } catch { }
        }
        return null;
    }
}
