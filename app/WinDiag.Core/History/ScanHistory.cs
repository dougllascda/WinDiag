using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.Core.History;

/// <summary>Resumo de um achado guardado no histórico.</summary>
public sealed class FindingSummary
{
    /// <summary>Chave: o Id estável do achado, ou seção:título em leituras antigas.</summary>
    public string Key { get; set; } = "";
    /// <summary>Seção de origem.</summary>
    public string Section { get; set; } = "";
    /// <summary>Título.</summary>
    public string Title { get; set; } = "";
    /// <summary>Gravidade.</summary>
    public Severity Sev { get; set; }
    /// <summary>Evidência medida.</summary>
    public string Evidence { get; set; } = "";
}

/// <summary>Resumo de uma leitura, gravado em JSON.</summary>
public sealed class ScanSummary
{
    /// <summary>Data e hora da leitura.</summary>
    public DateTime When { get; set; }
    /// <summary>Versão do WinDiag.</summary>
    public string Version { get; set; } = "";
    /// <summary>Fatos escolhidos, por chave estável independente de idioma ("cpu", "gpu", "board"...). Arquivos antigos usam rótulos em português como chave; <see cref="ScanHistory.NormalizeFacts"/> converte. Os valores ficam em formato neutro (português) e só são traduzidos na exibição.</summary>
    public Dictionary<string, string> Facts { get; set; } = new();
    /// <summary>Todos os achados.</summary>
    public List<FindingSummary> Findings { get; set; } = new();
    /// <summary>Nota de configuração da leitura.</summary>
    public int Score { get; set; }
}

/// <summary>Mudança de um fato.</summary>
/// <param name="Label">Rótulo no idioma atual.</param>
/// <param name="Old">Valor anterior.</param>
/// <param name="New">Valor atual.</param>
public sealed record FactChange(string Label, string Old, string New);

/// <summary>Mudança de um achado.</summary>
/// <param name="Title">Título.</param>
/// <param name="Section">Seção.</param>
/// <param name="OldSev">Gravidade anterior (null se não existia).</param>
/// <param name="NewSev">Gravidade atual (null se sumiu).</param>
/// <param name="OldEvidence">Evidência anterior.</param>
/// <param name="NewEvidence">Evidência atual.</param>
public sealed record FindingChange(string Title, string Section, Severity? OldSev, Severity? NewSev, string OldEvidence, string NewEvidence);

/// <summary>Diferença entre duas leituras.</summary>
public sealed class ScanDiff
{
    /// <summary>Data da leitura anterior.</summary>
    public DateTime PreviousWhen { get; init; }
    /// <summary>Nota anterior.</summary>
    public int PreviousScore { get; init; }
    /// <summary>Nota atual.</summary>
    public int CurrentScore { get; init; }
    /// <summary>Achados que eram AVISO/ATENÇÃO e deixaram de ser.</summary>
    public List<FindingChange> Resolved { get; } = new();
    /// <summary>Achados que baixaram de gravidade mas seguem em AVISO ou mais.</summary>
    public List<FindingChange> Improved { get; } = new();
    /// <summary>Achados que subiram de gravidade.</summary>
    public List<FindingChange> Worsened { get; } = new();
    /// <summary>AVISO/ATENÇÃO que não existiam antes.</summary>
    public List<FindingChange> New { get; } = new();
    /// <summary>Fatos que mudaram.</summary>
    public List<FactChange> FactChanges { get; } = new();
    /// <summary>true se nada mudou.</summary>
    public bool IsEmpty => Resolved.Count + Improved.Count + Worsened.Count + New.Count + FactChanges.Count == 0;
}

/// <summary>Histórico de leituras em %LocalAppData%\WinDiag\history, com comparação entre leituras.</summary>
public static class ScanHistory
{
    private const int MaxFiles = 30;
    private const string AdminKey = "Admin";
    private const string SensorSourcesKey = "sensorsources";
    private static readonly string[] AdminSections = { "armazenamento", "sistema", "seguranca" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Pasta padrão do histórico.</summary>
    public static string DefaultDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDiag", "history");

    /// <summary>Resume a varredura atual (fatos escolhidos, achados e nota).</summary>
    /// <param name="engine">Motor já executado.</param>
    public static ScanSummary Summarize(ScanEngine engine)
    {
        var all = engine.AllFindings().ToList();
        var s = new ScanSummary
        {
            When = DateTime.Now,
            Version = AppInfo.Version,
            Facts = BuildFacts(engine.Facts),
            Score = GameScore.Compute(all).Score,
        };
        foreach (var f in all)
            s.Findings.Add(new FindingSummary { Key = string.IsNullOrWhiteSpace(f.Id) ? f.Section + ":" + f.Title : f.Id, Section = f.Section, Title = f.Title, Sev = f.Sev, Evidence = f.Evidence });
        return s;
    }

    // Chaves estáveis dos fatos (gravadas no JSON) e o rótulo de cada uma nos dois idiomas.
    private static readonly (string Key, string Legacy, string Pt, string En)[] FactDefs =
    {
        ("cpu", "CPU", "CPU", "CPU"),
        ("gpu", "GPU", "GPU", "GPU"),
        ("board", "Placa-mãe", "Placa-mãe", "Motherboard"),
        ("bios", "BIOS", "BIOS", "BIOS"),
        ("ram", "RAM instalada", "RAM instalada", "Installed RAM"),
        ("ramspeed", "RAM velocidade", "RAM velocidade", "RAM speed"),
        ("ramchannels", "RAM canais", "RAM canais", "RAM channels"),
        ("pcie", "PCIe", "PCIe", "PCIe"),
        ("rebar", "Resizable BAR", "Resizable BAR", "Resizable BAR"),
        ("monitors", "Monitores", "Monitores", "Monitors"),
        ("memcomp", "Compressão de memória", "Compressão de memória", "Memory compression"),
        ("boot", "Modo de boot", "Modo de boot", "Boot mode"),
        (SensorSourcesKey, "Fontes de sensores", "Fontes de sensores", "Sensor sources"),
    };

    /// <summary>Converte chaves antigas (rótulos em português) para as chaves estáveis atuais; chaves desconhecidas ficam como estão. Devolve um novo dicionário.</summary>
    /// <param name="facts">Fatos gravados em qualquer versão.</param>
    public static Dictionary<string, string> NormalizeFacts(Dictionary<string, string> facts)
    {
        var map = FactDefs.ToDictionary(d => d.Legacy, d => d.Key, StringComparer.Ordinal);
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in facts) res[map.TryGetValue(k, out var nk) ? nk : k] = v;
        return res;
    }

    private static string FactLabel(string key)
    {
        foreach (var d in FactDefs) if (d.Key == key) return Loc.T(d.Pt, d.En);
        return key;
    }

    // Valores ficam gravados em português (formato neutro); para exibir em inglês, troca os termos conhecidos.
    private static string FactValue(string key, string v)
    {
        if (Loc.IsPt) return v;
        if (v == "n/d") return "n/a";
        string r = v;
        switch (key)
        {
            case "rebar": r = v == "ativo" ? "active" : v == "inativo" ? "inactive" : v; break;
            case "memcomp": r = v == "ligada" ? "on" : v == "desligada" ? "off" : v; break;
            case "pcie": r = v.Replace("host máx.", "host max").Replace("atual", "current"); break;
            case "boot": r = v.Replace("BIOS legado", "legacy BIOS").Replace("ligado", "on").Replace("desligado", "off"); break;
            case "monitors": case "ram": r = System.Text.RegularExpressions.Regex.Replace(v, @"(\d),(\d)", "$1.$2"); break;
        }
        return r;
    }

    private static Dictionary<string, string> BuildFacts(SystemFacts f)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        string V(object? o) { var s = o?.ToString(); return string.IsNullOrWhiteSpace(s) ? "n/d" : s; }
        string Gen(int? g) => g is > 0 ? "Gen " + g : "n/d";
        string pcie = (f.GpuPcieHostMax is null && f.GpuPcieGenCurrent is null) ? "n/d"
            : $"host máx. {Gen(f.GpuPcieHostMax)}; atual {Gen(f.GpuPcieGenCurrent)}" + (f.GpuPcieWidthCurrent is > 0 ? $" x{f.GpuPcieWidthCurrent}" : "");
        string mons = f.Displays.Count == 0 ? "n/d"
            : string.Join(" + ", f.Displays.Select(d => $"{d.Width}x{d.Height}@{Math.Round(d.RefreshHz, 2).ToString("0.##", pt)}"));
        string boot = f.FirmwareUefi is null ? "n/d"
            : (f.FirmwareUefi.Value ? "UEFI" : "BIOS legado") + (f.SecureBoot is null ? "" : f.SecureBoot.Value ? ", Secure Boot ligado" : ", Secure Boot desligado");
        string ram = f.RamForThresholdGB > 0 ? Math.Round(f.RamForThresholdGB, 1).ToString("0.#", pt) + " GB" : "n/d";
        return new Dictionary<string, string>
        {
            ["cpu"] = V(f.CpuName),
            ["gpu"] = V(f.GpuName),
            ["board"] = V(f.BoardModel),
            ["bios"] = V(f.BiosVersion),
            ["ram"] = ram,
            ["ramspeed"] = f.RamConfiguredMTs is > 0 ? f.RamConfiguredMTs + " MT/s" : "n/d",
            ["ramchannels"] = f.RamChannels is > 0 ? f.RamChannels.ToString()! : "n/d",
            ["pcie"] = pcie,
            ["rebar"] = f.ReBarActive is null ? "n/d" : f.ReBarActive.Value ? "ativo" : "inativo",
            ["monitors"] = mons,
            ["memcomp"] = f.MemCompression is null ? "n/d" : f.MemCompression.Value ? "ligada" : "desligada",
            ["boot"] = boot,
            [SensorSourcesKey] = V(f.SensorSources),
            [AdminKey] = f.IsAdmin ? "sim" : "não",
        };
    }
    /// <summary>Compara duas leituras. Se o modo de administrador difere, achados das seções que dependem dele são ignorados.</summary>
    /// <param name="previous">Leitura anterior.</param>
    /// <param name="current">Leitura atual.</param>
    public static ScanDiff Compare(ScanSummary previous, ScanSummary current)
    {
        var diff = new ScanDiff { PreviousWhen = previous.When, PreviousScore = previous.Score, CurrentScore = current.Score };
        var prevFacts = NormalizeFacts(previous.Facts);
        var curFacts = NormalizeFacts(current.Facts);
        prevFacts.TryGetValue(AdminKey, out var pa);
        curFacts.TryGetValue(AdminKey, out var ca);
        bool modeDiffers = pa != null && ca != null && pa != ca;

        if (modeDiffers)
            diff.FactChanges.Add(new FactChange(Loc.T("Modo de leitura", "Scan mode"),
                pa == "sim" ? Loc.T("administrador", "administrator") : Loc.T("usuário padrão", "standard user"),
                ca == "sim" ? Loc.T("administrador", "administrator") : Loc.T("usuário padrão", "standard user")));

        foreach (var (k, v) in curFacts)
        {
            if (k == AdminKey || !prevFacts.TryGetValue(k, out var old) || old == v) continue;
            diff.FactChanges.Add(new FactChange(FactLabel(k), FactValue(k, old), FactValue(k, v)));
        }

        // Fontes de sensores diferentes (ex.: HWiNFO fechou) não permitem comparar a seção "sensores": evita "resolvido" falso.
        prevFacts.TryGetValue(SensorSourcesKey, out var ps);
        curFacts.TryGetValue(SensorSourcesKey, out var cs);
        bool sensorsDiffer = !string.IsNullOrEmpty(ps) && !string.IsNullOrEmpty(cs) && ps != "n/d" && cs != "n/d" && ps != cs;

        bool Skip(string section) => (modeDiffers && AdminSections.Contains(section)) || (sensorsDiffer && section == "sensores");
        var prev = Index(previous.Findings.Where(f => !Skip(f.Section)));
        var cur = Index(current.Findings.Where(f => !Skip(f.Section)));

        foreach (var (key, p) in prev)
        {
            cur.TryGetValue(key, out var c);
            if (p.Sev >= Severity.Aviso)
            {
                if (c is null || c.Sev <= Severity.Info) diff.Resolved.Add(Change(p, c));
                else if (c.Sev < p.Sev) diff.Improved.Add(Change(p, c));
                else if (c.Sev > p.Sev) diff.Worsened.Add(Change(p, c));
            }
            else if (c is not null && c.Sev > p.Sev && c.Sev >= Severity.Aviso)
                diff.Worsened.Add(Change(p, c));
        }
        foreach (var (key, c) in cur)
            if (!prev.ContainsKey(key) && c.Sev >= Severity.Aviso)
                diff.New.Add(Change(null, c));
        return diff;
    }

    private static Dictionary<string, FindingSummary> Index(IEnumerable<FindingSummary> items)
    {
        // Indexa por Id; havendo Ids repetidos na mesma leitura (conteúdo diferente), esses passam a ser indexados por Id|Título.
        var list = items.ToList();
        var counts = list.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var d = new Dictionary<string, FindingSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            string k = counts[f.Key] > 1 ? f.Key + "|" + f.Title : f.Key;
            if (!d.TryGetValue(k, out var e) || f.Sev > e.Sev) d[k] = f;
        }
        return d;
    }

    private static FindingChange Change(FindingSummary? p, FindingSummary? c)
    {
        var x = c ?? p!;
        return new FindingChange(x.Title, x.Section, p?.Sev, c?.Sev, p?.Evidence ?? "", c?.Evidence ?? "");
    }

    /// <summary>Compara a leitura atual com a anterior mais recente, grava a atual e mantém os 30 arquivos mais novos. Devolve null se não havia anterior.</summary>
    /// <param name="engine">Motor já executado.</param>
    /// <param name="dir">Pasta do histórico (padrão: <see cref="DefaultDir"/>).</param>
    public static ScanDiff? RecordAndCompare(ScanEngine engine, string? dir = null)
    {
        dir ??= DefaultDir;
        var current = Summarize(engine);
        ScanSummary? previous = LoadLatest(dir);
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"scan_{current.When:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(current, Json), new UTF8Encoding(false));
            Rotate(dir);
        }
        catch { /* histórico é opcional: falha de gravação não derruba o app */ }
        return previous is null ? null : Compare(previous, current);
    }

    /// <summary>Carrega a leitura mais recente da pasta (ignora arquivos ilegíveis). Null se não houver.</summary>
    /// <param name="dir">Pasta do histórico (padrão: <see cref="DefaultDir"/>).</param>
    public static ScanSummary? LoadLatest(string? dir = null)
    {
        dir ??= DefaultDir;
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var file in Directory.GetFiles(dir, "scan_*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<ScanSummary>(File.ReadAllText(file, Encoding.UTF8), Json);
                    if (s != null) { s.Facts = NormalizeFacts(s.Facts); return s; }
                }
                catch { /* arquivo ilegível: tenta o próximo */ }
            }
        }
        catch { }
        return null;
    }

    private static void Rotate(string dir)
    {
        foreach (var old in Directory.GetFiles(dir, "scan_*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(MaxFiles))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
