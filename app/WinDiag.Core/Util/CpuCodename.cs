using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Codinome/litografia aproximados por tabela (família/modelo), como na v7.</summary>
public static class CpuCodename
{
    /// <summary>Codinome aproximado por tabela; null se desconhecido.</summary>
    /// <param name="isIntel">Fabricante Intel.</param>
    /// <param name="isAmd">Fabricante AMD.</param>
    /// <param name="family">Família do processador.</param>
    /// <param name="model">Modelo do processador.</param>
    /// <param name="stepping">Stepping, se conhecido. O modelo 158 é Kaby Lake (7ª geração) no stepping 9 e Coffee Lake (8ª/9ª) nos steppings 10 a 13; sem stepping, "Kaby Lake/Coffee Lake".</param>
    public static string? Lookup(bool isIntel, bool isAmd, int family, int model, int? stepping = null)
    {
        if (isIntel && family == 6)
            return model switch
            {
                158 => stepping switch
                {
                    9 => "Kaby Lake (14 nm)",
                    >= 10 and <= 13 => "Coffee Lake (14 nm)",
                    _ => "Kaby Lake/Coffee Lake (14 nm)",
                },
                165 or 166 => "Comet Lake (14 nm)",
                167 => "Rocket Lake (14 nm)",
                151 or 154 => "Alder Lake (Intel 7)",
                183 or 191 => "Raptor Lake (Intel 7)",
                170 => "Meteor Lake (Intel 4)",
                197 or 198 => "Arrow Lake",
                189 => "Lunar Lake",
                _ => null
            };
        if (isAmd)
            return family switch
            {
                23 => "Zen / Zen+ / Zen 2",
                25 => "Zen 3 / Zen 4",
                26 => "Zen 5",
                _ => null
            };
        return null;
    }

    /// <summary>Usa a descrição do Win32_Processor ("... Family 6 Model 165 Stepping 3") e o fabricante.</summary>
    public static string? FromDescription(string? description, string? manufacturer)
    {
        var m = Regex.Match(description ?? "", @"Family\s+(\d+)\s+Model\s+(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var fam) ||
            !int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mod)) return null;
        var man = manufacturer ?? "";
        int? step = null;
        var ms = Regex.Match(description ?? "", @"Stepping\s+(\d+)", RegexOptions.IgnoreCase);
        if (ms.Success && int.TryParse(ms.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var st)) step = st;
        return Lookup(man.Contains("Intel", StringComparison.OrdinalIgnoreCase),
                      man.Contains("AMD", StringComparison.OrdinalIgnoreCase),
                      fam, mod, step);
    }
}
