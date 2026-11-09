using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Descoberta das bibliotecas Steam (registro + libraryfolders.vdf). Não exige administrador; sem Steam devolve lista vazia.</summary>
public static class Steam
{
    /// <summary>Linha "path" do libraryfolders.vdf (formato KeyValues da Valve), com o caminho entre aspas.</summary>
    private static readonly Regex PathLine = new("^\\s*\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Extrai os caminhos "path" de um libraryfolders.vdf (barras duplas viram simples).</summary>
    public static List<string> ParseVdf(IEnumerable<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var m = PathLine.Match(line);
            if (m.Success) list.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return list;
    }

    /// <summary>Barras normalizadas, sem barra final e com a letra da unidade em maiúscula.</summary>
    public static string NormalizePath(string p)
    {
        p = (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])) p = char.ToUpperInvariant(p[0]) + p[1..];
        return p;
    }

    /// <summary>Normaliza e remove duplicados (sem diferenciar maiúsculas). <paramref name="fixCase"/> corrige a caixa pelo disco.</summary>
    public static List<string> Dedup(IEnumerable<string> candidates, Func<string, string>? fixCase = null)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var p = NormalizePath(c);
            if (fixCase != null)
            {
                try { p = NormalizePath(fixCase(p)); } catch { }
            }
            if (seen.Add(p)) result.Add(p);
        }
        return result;
    }

    /// <summary>Acerta a caixa de cada segmento conforme o que existe no disco (Fix-PathCase).</summary>
    public static string FixPathCase(string p)
    {
        var m = Regex.Match(p ?? "", @"^([A-Za-z]):\\?(.*)$");
        if (!m.Success) return p ?? "";
        string cur = char.ToUpperInvariant(m.Groups[1].Value[0]) + @":\";
        foreach (var seg in m.Groups[2].Value.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            // Se o segmento não for encontrado (permissão ou removido), mantém a grafia original.
            string name = seg;
            try
            {
                var found = new DirectoryInfo(cur).EnumerateFileSystemInfos()
                    .FirstOrDefault(i => string.Equals(i.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (found != null) name = found.Name;
            }
            catch { }
            cur = Path.Combine(cur, name);
        }
        return cur.TrimEnd('\\');
    }

    /// <summary>Bibliotecas Steam encontradas. Vazio se a Steam não está instalada. <paramref name="note"/> recebe falhas de leitura.</summary>
    public static List<string> Libraries(Action<string>? note = null)
    {
        string? steam = null;
        try { steam = Reg.GetString(@"HKCU:\Software\Valve\Steam", "SteamPath"); }
        catch (Exception ex) { note?.Invoke("Registro da Steam: " + ex.Message.Trim()); }
        if (string.IsNullOrWhiteSpace(steam)) return new List<string>();
        steam = steam.Replace('/', '\\').TrimEnd('\\');

        var cands = new List<string> { steam };
        try
        {
            var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf)) cands.AddRange(ParseVdf(File.ReadAllLines(vdf)));
        }
        catch (Exception ex) { note?.Invoke("libraryfolders.vdf: " + ex.Message.Trim()); }
        return Dedup(cands, FixPathCase);
    }
}
