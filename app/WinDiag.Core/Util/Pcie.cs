namespace WinDiag.Core.Util;

/// <summary>Banda teórica do PCIe por geração e largura (GB/s por linha, já descontada a codificação).</summary>
public static class Pcie
{
    /// <summary>GB/s aproximados por linha: Gen 1 = 0,25; Gen 2 = 0,5; Gen 3 = 0,985; Gen 4 = 1,969; Gen 5 = 3,938; Gen 6 = 7,563.</summary>
    public static double? LaneGBs(int gen) => gen switch
    {
        1 => 0.25,
        2 => 0.5,
        3 => 0.985,
        4 => 1.969,
        5 => 3.938,
        6 => 7.563,
        _ => null
    };

    /// <summary>Banda aproximada em GB/s (uma casa decimal), ou null se geração/largura forem inválidas.</summary>
    public static double? Bandwidth(int gen, int lanes)
    {
        if (lanes <= 0) return null;
        var l = LaneGBs(gen);
        return l is null ? null : Math.Round(l.Value * lanes, 1);
    }
}
