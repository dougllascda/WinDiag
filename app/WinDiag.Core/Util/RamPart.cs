using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Decodificação aproximada de part numbers de RAM (Corsair Vengeance/CM, Kingston Fury, G.Skill, Crucial Ballistix).</summary>
public static class RamPart
{
    /// <summary>Tabela código de velocidade do part number para MT/s (ex.: 32 = DDR4-3200). Valores não listados usam código x 100 em <see cref="SpeedCode"/>.</summary>
    private static readonly Dictionary<int, int> Codes = new()
    {
        [21] = 2133, [24] = 2400, [26] = 2666, [27] = 2666, [29] = 2933,
        [30] = 3000, [32] = 3200, [34] = 3466, [36] = 3600, [40] = 4000
    };

    /// <summary>Converte o código de velocidade do part number em MT/s (códigos desconhecidos viram código x 100).</summary>
    public static int SpeedCode(int c) => Codes.TryGetValue(c, out var v) ? v : c * 100;

    /// <summary>Descreve o part number (capacidade, velocidade, latência) ou devolve null se o padrão não for reconhecido.</summary>
    public static string? Decode(string? pn)
    {
        var p = (pn ?? "").Trim().ToUpperInvariant();
        if (p.Length == 0) return null;
        Match m;
        // Crucial Ballistix: BL{GB}G{velocidade}C{CL}.
        if ((m = Regex.Match(p, @"^BL(\d+)G(\d+)C(\d+)")).Success)
            return $"{m.Groups[1].Value} GB, {SpeedCode(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))} MT/s, CL{m.Groups[3].Value}";
        // Kingston Fury: KF{geração}{velocidade}C{CL}.
        if ((m = Regex.Match(p, @"^KF\d(\d{2})C(\d+)")).Success)
            return $"{SpeedCode(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))} MT/s, CL{m.Groups[2].Value}";
        // G.Skill DDR4: F4-{MT/s}C{CL}.
        if ((m = Regex.Match(p, @"^F4-(\d+)C(\d+)")).Success)
            return $"{m.Groups[1].Value} MT/s, CL{m.Groups[2].Value}";
        // Corsair Vengeance (CM...): capacidade do kit, velocidade e latência.
        if ((m = Regex.Match(p, @"^CM\w+?(\d+)GX4M\d?[A-Z](\d{4})C(\d+)")).Success)
            return $"kit {m.Groups[1].Value} GB, {m.Groups[2].Value} MT/s, CL{m.Groups[3].Value}";
        return null;
    }
}
