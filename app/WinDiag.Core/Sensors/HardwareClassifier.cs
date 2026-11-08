using System.Text.RegularExpressions;

namespace WinDiag.Core.Sensors;

/// <summary>Classifica o nome de uma peça (como o HWiNFO e o LibreHardwareMonitor a nomeiam) em uma categoria curta em pt-BR.</summary>
internal static class HardwareClassifier
{
    internal const string Cpu = "CPU";
    internal const string Board = "Placa-mãe";
    internal const string Gpu = "Placa de vídeo";
    internal const string Memory = "Memória";
    internal const string Storage = "Armazenamento";
    internal const string Psu = "Fonte";
    internal const string Controller = "Controlador";
    internal const string Other = "Outro";

    private static readonly Regex CpuRx = new(@"^CPU\b|\b(Ryzen|Threadripper|Xeon|Core\s*i\d|Core\(TM\)|Core\s*Ultra|Pentium|Celeron|Athlon|EPYC)\b|\bDTS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GpuRx = new(@"^GPU\b|\b(GeForce|Radeon|NVIDIA|RTX|GTX|Intel\s*Arc|UHD Graphics|Iris)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ControllerRx = new(@"Controller|Commander|Kraken|Aquacomputer|Aquaero|Octo|Quadro Fan|Farbwerk|Hub\b|\bAIO\b|Lighting Node|Smart Device|Hydro|iCUE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PsuRx = new(@"\bPSU\b|Power\s*Supply|\b(HX|RM|AX|SF|CX|TX)\d{3,4}[a-z]{0,3}\b|\bNZXT\s*E\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StorageRx = new(@"S\.?M\.?A\.?R\.?T|\bDrive\b|\bSSD\b|\bHDD\b|NVMe|\bDisk\b|\bWDC\b|Seagate|Kingston|Crucial|\bSN\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryRx = new(@"\bDIMM\b|\bDDR[2-5]\b|Memory|\bSPD\b|\bRAM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BoardRx = new(@"Motherboard|Mainboard|Chipset|\bPCH\b|Nuvoton|\bNCT\d|\bITE\b|\bIT8\d|Fintek|Winbond|Super\s*I/?O|Embedded Controller|\bEC\b|\bASUS\b|Gigabyte|\bMSI\b|ASRock|Biostar|\bSystem\b|\bROG\b|\bTUF\b|\bPRIME\b|AORUS", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Categoria da peça pelo nome; "Outro" se nada casar.</summary>
    /// <param name="name">Nome do sensor ou da peça.</param>
    internal static string Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Other;
        var n = name.Trim();
        // Prefixos do HWiNFO têm prioridade: "CPU [#0]: ..." e "GPU [#0]: ...".
        if (Regex.IsMatch(n, @"^GPU\b", RegexOptions.IgnoreCase)) return Gpu;
        if (Regex.IsMatch(n, @"^CPU\b", RegexOptions.IgnoreCase)) return Cpu;
        if (Regex.IsMatch(n, @"^(S\.?M\.?A\.?R\.?T\.?|Drive)\b", RegexOptions.IgnoreCase)) return Storage;
        if (Regex.IsMatch(n, @"^(DIMM|Memory)\b", RegexOptions.IgnoreCase)) return Memory;
        if (ControllerRx.IsMatch(n)) return Controller;
        if (PsuRx.IsMatch(n)) return Psu;
        if (CpuRx.IsMatch(n)) return Cpu;
        if (GpuRx.IsMatch(n)) return Gpu;
        if (StorageRx.IsMatch(n)) return Storage;
        if (MemoryRx.IsMatch(n)) return Memory;
        if (BoardRx.IsMatch(n)) return Board;
        return Other;
    }
}
