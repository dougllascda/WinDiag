using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Leitura do BCD via "bcdedit /enum {current}" (processo externo com timeout, sem janela). Exige administrador.</summary>
public static class Bcd
{
    /// <summary>Separadores de linha aceitos na saída do bcdedit (CRLF do console ou LF).</summary>
    private static readonly string[] NewLines = { "\r\n", "\n" };
    /// <summary>Linha "chave   valor" do bcdedit: nome alfanumérico seguido de 2 ou mais espaços (colunas alinhadas) e o valor até o fim.</summary>
    private static readonly Regex Line = new(@"^\s*([A-Za-z0-9_]+)\s{2,}(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>Executa o bcdedit e interpreta a saída. Sem administrador (ou em falha) devolve dicionário vazio.</summary>
    public static Dictionary<string, string> Read()
    {
        try
        {
            var r = Proc.Run(Proc.Sys32("bcdedit.exe"), "/enum {current}", 10000);
            if (!r.Ok) return new(StringComparer.OrdinalIgnoreCase);
            return Parse(r.StdOut);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Interpreta o texto do bcdedit. Chaves em minúsculas.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        // Chaves sem diferenciar maiúsculas; o idioma do Windows só altera os rótulos de outras partes, não os nomes das chaves.
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return h;
        foreach (var l in text.Split(NewLines, StringSplitOptions.None))
        {
            var m = Line.Match(l);
            if (m.Success) h[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
        }
        return h;
    }
}
