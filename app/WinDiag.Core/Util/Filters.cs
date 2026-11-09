using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Linha da tabela do "fltmc filters".</summary>
/// <param name="Name">Nome do filtro.</param>
/// <param name="Instances">Número de instâncias.</param>
/// <param name="Altitude">Altitude (ordem de empilhamento).</param>
public sealed record FilterInfo(string Name, int Instances, string Altitude);

/// <summary>Minifilters do sistema de arquivos: interpretação da saída do fltmc e descrição de filtros conhecidos.</summary>
public static class Filters
{
    /// <summary>Descrições em pt-BR dos minifilters comuns do Windows (chave = nome do driver, comparação sem diferenciar maiúsculas).</summary>
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wdfilter"] = "Microsoft Defender (antivírus)",
        ["bindflt"] = "Windows Bind Filter (containers/WSL)",
        ["wcifs"] = "Windows Container Isolation",
        ["storqosflt"] = "Storage QoS",
        ["cldflt"] = "Cloud Files (OneDrive)",
        ["bfs"] = "Windows Bind/Isolation (apps empacotados)",
        ["filecrypt"] = "Cifragem de arquivos (EFS)",
        ["luafv"] = "Virtualização de UAC",
        ["unionfs"] = "Union FS (containers)",
        ["npsvctrig"] = "Gatilho de serviço de pipes",
        ["wof"] = "Windows Overlay (compressão de arquivos)",
        ["fileinfo"] = "Informação de arquivos (SuperFetch/ReadyBoost)",
    };

    /// <summary>Linha de dados do "fltmc filters": nome, nº de instâncias, altitude (decimal com ponto) e frame. Cabeçalho e separador não casam, o que dispensa depender do idioma.</summary>
    static readonly Regex Line = new(@"^\s*(\S+)\s+(\d+)\s+([\d\.]+)\s+(\d+)\s*$", RegexOptions.Compiled);
    /// <summary>Drivers da Kaspersky começam por "kl" (klif, klflt, kneps...).</summary>
    static readonly Regex Kaspersky = new(@"^kl[a-z]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Prefixos de drivers de antivírus/EDR de terceiros (McAfee, Avast, AVG, Bitdefender, Avira, ESET, Symantec, Carbon Black, SentinelOne, CrowdStrike).</summary>
    static readonly Regex Third = new(@"^(mfe|avast|asw|avg|bd|bdfs|atc|eset|epfw|sym|cb|sentinel|crowdstrike|csagent)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Interpreta a tabela do "fltmc filters" (colunas numéricas, independente do idioma). Linhas fora do formato são ignoradas.</summary>
    public static List<FilterInfo> Parse(string output)
    {
        var list = new List<FilterInfo>();
        foreach (var l in (output ?? "").Split('\n'))
        {
            var m = Line.Match(l.TrimEnd('\r'));
            if (m.Success) list.Add(new FilterInfo(m.Groups[1].Value, int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var inst) ? inst : 0, m.Groups[3].Value));
        }
        return list;
    }

    /// <summary>Versão em inglês de <see cref="Known"/>, usada quando o idioma da interface não é pt-BR.</summary>
    private static readonly Dictionary<string, string> KnownEn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wdfilter"] = "Microsoft Defender (antivirus)",
        ["bindflt"] = "Windows Bind Filter (containers/WSL)",
        ["wcifs"] = "Windows Container Isolation",
        ["storqosflt"] = "Storage QoS",
        ["cldflt"] = "Cloud Files (OneDrive)",
        ["bfs"] = "Windows Bind/Isolation (packaged apps)",
        ["filecrypt"] = "File encryption (EFS)",
        ["luafv"] = "UAC virtualization",
        ["unionfs"] = "Union FS (containers)",
        ["npsvctrig"] = "Pipe service trigger",
        ["wof"] = "Windows Overlay (file compression)",
        ["fileinfo"] = "File information (SuperFetch/ReadyBoost)",
    };

    /// <summary>Descrição do filtro no idioma atual, ou "sem descrição no utilitário" / "no description in the utility".</summary>
    /// <param name="name">Nome do filtro.</param>
    public static string Describe(string name) => Loc.IsPt ? DescribePt(name) : DescribeEn(name);

    /// <summary>Descrição em inglês: tabela conhecida, depois heurística por prefixo (Kaspersky, terceiros).</summary>
    private static string DescribeEn(string name)
    {
        if (KnownEn.TryGetValue(name, out var d)) return d;
        if (Kaspersky.IsMatch(name)) return "Kaspersky (antivirus/protection)";
        if (Third.IsMatch(name)) return "Third-party antivirus/security";
        return "no description in the utility";
    }

    /// <summary>Descrição em pt-BR: mesma lógica de <see cref="DescribeEn"/>. Também é a base de <see cref="IsAntivirus"/>.</summary>
    private static string DescribePt(string name)
    {
        if (Known.TryGetValue(name, out var d)) return d;
        if (Kaspersky.IsMatch(name)) return "Kaspersky (antivírus/proteção)";
        if (Third.IsMatch(name)) return "Antivírus/segurança de terceiros";
        return "sem descrição no utilitário";
    }

    /// <summary>true se o filtro pertence a um antivírus conhecido.</summary>
    public static bool IsAntivirus(string name)
    {
        // Reutiliza o texto em pt-BR para classificar; assim a decisão não muda com o idioma da interface.
        var d = DescribePt(name);
        return Regex.IsMatch(d, "antiv|Kaspersky|Defender", RegexOptions.IgnoreCase);
    }
}
