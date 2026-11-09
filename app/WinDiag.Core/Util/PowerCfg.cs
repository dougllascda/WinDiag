using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Leituras do powercfg (processo externo com timeout). A interpretação usa só índices hexadecimais, independente do idioma do Windows.</summary>
public static class PowerCfg
{
    /// <summary>GUID do subgrupo de processador.</summary>
    public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    /// <summary>GUID do subgrupo de USB.</summary>
    public const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    /// <summary>GUID do subgrupo PCI Express.</summary>
    public const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";

    /// <summary>Opções de consulta, em ordem: /query (padrão) e /qh (inclui configurações ocultas, ex.: planos do AtlasOS).</summary>
    private static readonly string[] Modes = { "/query", "/qh" };
    /// <summary>Índice de configuração impresso pelo powercfg: "0x" + 8 dígitos hexadecimais, independente do idioma.</summary>
    private static readonly Regex Hex = new(@"0x[0-9a-fA-F]{8}", RegexOptions.Compiled);

    /// <summary>Valor CA (penúltimo 0x...) do esquema atual. /query omite itens ocultos pelo plano (AtlasOS); /qh inclui.</summary>
    /// <param name="subGuid">GUID do subgrupo.</param>
    /// <param name="setGuid">GUID da configuração.</param>
    /// <returns>O índice em CA, ou null se não foi possível ler.</returns>
    public static long? Get(string subGuid, string setGuid)
    {
        foreach (var mode in Modes)
        {
            try
            {
                var r = Proc.Run(Proc.Sys32("powercfg.exe"), $"{mode} SCHEME_CURRENT {subGuid} {setGuid}", 10000);
                var v = ParseAc(r.StdOut);
                if (v is not null) return v;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Extrai o valor CA: a saída termina com dois índices hexadecimais (CA e CC); vale o penúltimo.</summary>
    /// <param name="output">Saída do powercfg.</param>
    public static long? ParseAc(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = Hex.Matches(output);
        if (m.Count < 2) return null;
        try { return Convert.ToInt64(m[m.Count - 2].Value[2..], 16); } catch { return null; }
    }

    /// <summary>GUID no formato 8-4-4-4-12.</summary>
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>Extrai o GUID de uma saída do "powercfg /getactivescheme"; null se não houver.</summary>
    /// <param name="output">Saída do powercfg.</param>
    public static string? ParseSchemeGuid(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = GuidRx.Match(output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Extrai todos os GUIDs (minúsculos, sem repetição) de uma saída do "powercfg /list".</summary>
    /// <param name="output">Saída do powercfg.</param>
    public static List<string> ParseSchemeGuids(string? output)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(output)) return list;
        foreach (Match m in GuidRx.Matches(output))
        {
            var g = m.Value.ToLowerInvariant();
            if (!list.Contains(g)) list.Add(g);
        }
        return list;
    }

    /// <summary>GUID do plano de energia ativo; null em falha.</summary>
    public static string? ActiveSchemeGuid()
    {
        try { return ParseSchemeGuid(Proc.Run(Proc.Sys32("powercfg.exe"), "/getactivescheme", 10000).StdOut); }
        catch { return null; }
    }

    /// <summary>Nome do plano ativo (texto entre parênteses de "powercfg /getactivescheme"). Devolve null em falha.</summary>
    public static string? ActiveSchemeName()
    {
        try
        {
            var r = Proc.Run(Proc.Sys32("powercfg.exe"), "/getactivescheme", 10000);
            var raw = r.StdOut.Replace("\r", " ").Replace("\n", " ").Trim();
            if (raw.Length == 0) return null;
            // O nome vem entre parênteses; sem parênteses devolve a saída bruta.
            var m = Regex.Match(raw, @"\((.+)\)");
            return m.Success ? m.Groups[1].Value : raw;
        }
        catch { return null; }
    }
}
