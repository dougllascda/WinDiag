using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Atributo S.M.A.R.T. decodificado (bloco de 12 bytes do VendorSpecific).</summary>
/// <param name="Id">ID do atributo.</param>
/// <param name="Value">Valor normalizado atual.</param>
/// <param name="Worst">Pior valor normalizado.</param>
/// <param name="Raw">Valor bruto (48 bits).</param>
public sealed record SmartAttr(int Id, int Value, int Worst, long Raw);

/// <summary>Entrada do histórico (mesmo formato JSON da v7: Date, A5, A197, A198, A199).</summary>
public sealed class SmartHistoryEntry
{
    /// <summary>Data da leitura (dd/MM/yyyy).</summary>
    public string Date { get; set; } = "";
    /// <summary>Setores realocados (atributo 5).</summary>
    public long? A5 { get; set; }
    /// <summary>Setores pendentes (atributo 197).</summary>
    public long? A197 { get; set; }
    /// <summary>Incorrigíveis offline (atributo 198).</summary>
    public long? A198 { get; set; }
    /// <summary>Erros de CRC UDMA (atributo 199).</summary>
    public long? A199 { get; set; }
}

/// <summary>Leitura S.M.A.R.T. de uma instância WMI (um disco).</summary>
/// <param name="Instance">InstanceName da WMI (contém "Prod_modelo").</param>
/// <param name="Predict">PredictFailure informado pelo disco.</param>
/// <param name="Attrs">Atributos por ID.</param>
public sealed record SmartTable(string Instance, bool Predict, Dictionary<int, SmartAttr> Attrs);

/// <summary>Decodificação do S.M.A.R.T. bruto (ATA) e histórico de leituras para detectar atributos que crescem. Fonte: WMI root\wmi (exige administrador); o histórico fica em %ProgramData%\WinDiagMaint\smart_history.json.</summary>
public static class Smart
{
    /// <summary>Opções do JSON do histórico: indentado para facilitar inspeção manual.</summary>
    private static readonly JsonSerializerOptions HistoryJson = new() { WriteIndented = true };

    /// <summary>Nomes em pt-BR dos atributos exibidos.</summary>
    public static readonly Dictionary<int, string> Names = new()
    {
        [5] = "Realocados", [9] = "Horas ligado", [12] = "Ciclos de energia", [177] = "Desgaste (SSD)",
        [187] = "Incorrigíveis reportados", [188] = "Timeout de comando", [194] = "Temperatura",
        [196] = "Eventos de realocação", [197] = "Pendentes", [198] = "Incorrigíveis offline",
        [199] = "CRC UDMA", [231] = "Vida restante (SSD)", [233] = "Média de wearout", [241] = "Total gravado (LBAs)"
    };

    /// <summary>Versão em inglês de <see cref="Names"/>.</summary>
    private static readonly Dictionary<int, string> NamesEn = new()
    {
        [5] = "Reallocated", [9] = "Power-on hours", [12] = "Power cycles", [177] = "Wear (SSD)",
        [187] = "Reported uncorrectable", [188] = "Command timeout", [194] = "Temperature",
        [196] = "Reallocation events", [197] = "Pending", [198] = "Offline uncorrectable",
        [199] = "UDMA CRC", [231] = "Remaining life (SSD)", [233] = "Wearout average", [241] = "Total written (LBAs)"
    };

    /// <summary>Atributos cujo bruto é específico do fabricante.</summary>
    public static readonly int[] VendorSpecificRaw = { 177, 231, 233, 241 };

    /// <summary>Nota exibida quando há atributos de bruto específico do fabricante.</summary>
    public const string VendorRawNote = "177/231/233/241: o bruto é específico do fabricante; use o valor normalizado (100 = novo).";

    /// <summary>Nota exibida (no idioma atual) quando há atributos de bruto específico do fabricante.</summary>
    public static string VendorRawNoteText => Loc.T(VendorRawNote, "177/231/233/241: the raw value is vendor-specific; use the normalized value (100 = new).");

    /// <summary>Nome do atributo (no idioma atual) ou "outro" / "other".</summary>
    /// <param name="id">ID do atributo S.M.A.R.T.</param>
    public static string NameOf(int id) => (Loc.IsPt ? Names : NamesEn).TryGetValue(id, out var n) ? n : Loc.T("outro", "other");

    /// <summary>Valor bruto exibido: para 194 só o byte baixo (temperatura atual).</summary>
    public static long RawShown(SmartAttr a) => a.Id == 194 ? (a.Raw & 0xFF) : a.Raw;

    /// <summary>Decodifica o VendorSpecific (blocos de 12 bytes a partir do offset 2, até 362).</summary>
    public static Dictionary<int, SmartAttr> Parse(byte[]? vs)
    {
        var attrs = new Dictionary<int, SmartAttr>();
        if (vs is null) return attrs;
        // Layout ATA SMART: 2 bytes de revisão, depois até 30 blocos de 12 bytes: [0]=ID, [3]=valor normalizado, [4]=pior, [5..10]=bruto de 48 bits (little-endian). ID 0 = bloco vazio.
        for (int i = 2; i + 11 < vs.Length && i < 362; i += 12)
        {
            int id = vs[i];
            if (id == 0) continue;
            long raw = 0;
            for (int b = 5; b >= 0; b--) raw = (raw << 8) | vs[i + 5 + b];
            attrs[id] = new SmartAttr(id, vs[i + 3], vs[i + 4], raw);
        }
        return attrs;
    }

    /// <summary>Mantém só letras e dígitos, em maiúsculas (para comparar modelos).</summary>
    public static string Norm(string? s) => Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();

    /// <summary>Extrai o "Prod_xxx" do InstanceName e compara com o modelo do disco.</summary>
    public static bool InstanceMatches(string? instance, string? model)
    {
        var m = Regex.Match(instance ?? "", @"Prod_([^\\&]+)");
        if (!m.Success) return false;
        var p = Norm(m.Groups[1].Value);
        return p.Length > 0 && Norm(model).Contains(p, StringComparison.Ordinal);
    }

    /// <summary>Normaliza um InstanceName/PNPDeviceID para comparação: maiúsculas e sem o sufixo "_N" que a WMI acrescenta à instância.</summary>
    /// <param name="s">InstanceName do MSStorageDriver ou PNPDeviceID do Win32_DiskDrive.</param>
    public static string NormInstance(string? s) => Regex.Replace((s ?? "").Trim().ToUpperInvariant(), @"_\d+$", "");

    /// <summary>
    /// Associa cada disco à sua instância de S.M.A.R.T. Primeiro pela instância do dispositivo (PNPDeviceID = InstanceName sem "_N"),
    /// marcando as instâncias já usadas; depois, pelo modelo, mas só quando a correspondência é única (um disco e uma instância livres).
    /// Se for ambígua (ex.: dois discos do mesmo modelo sem PNP), não atribui e <paramref name="ambiguous"/> fica true.
    /// </summary>
    /// <param name="disks">Discos: PNPDeviceID (pode ser nulo) e modelo.</param>
    /// <param name="instances">InstanceName de cada leitura S.M.A.R.T.</param>
    /// <param name="ambiguous">true se algum disco ficou sem atribuição por ambiguidade.</param>
    /// <returns>Para cada disco, o índice da instância ou null.</returns>
    public static int?[] MatchInstances(IReadOnlyList<(string? Pnp, string? Model)> disks, IReadOnlyList<string> instances, out bool ambiguous)
    {
        ambiguous = false;
        var result = new int?[disks.Count];
        var used = new bool[instances.Count];
        for (int d = 0; d < disks.Count; d++)
        {
            var pnp = NormInstance(disks[d].Pnp);
            if (pnp.Length == 0) continue;
            for (int i = 0; i < instances.Count; i++)
            {
                if (used[i] || NormInstance(instances[i]) != pnp) continue;
                result[d] = i; used[i] = true; break;
            }
        }
        var pending = Enumerable.Range(0, disks.Count).Where(d => result[d] is null).ToList();
        // Segunda passada (por modelo): só atribui quando há exatamente um candidato e nenhum outro disco pendente disputa a mesma instância.
        var plan = new List<(int Disk, int Inst)>();
        foreach (var d in pending)
        {
            var cands = Enumerable.Range(0, instances.Count).Where(i => !used[i] && InstanceMatches(instances[i], disks[d].Model)).ToList();
            if (cands.Count == 0) continue;
            bool rival = pending.Any(o => o != d && cands.Any(i => InstanceMatches(instances[i], disks[o].Model)));
            if (cands.Count == 1 && !rival) plan.Add((d, cands[0]));
            else ambiguous = true;
        }
        foreach (var (d, i) in plan) result[d] = i;
        return result;
    }

    /// <summary>SHA-256 do número de série (o histórico nunca guarda o serial em claro); null se vazio.</summary>
    public static string? SerialHash(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(serial.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Pasta de dados do WinDiag em %ProgramData%.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinDiagMaint");

    /// <summary>Caminho do histórico S.M.A.R.T.</summary>
    public static string HistoryFile => Path.Combine(DataDir, "smart_history.json");

    /// <summary>Lê o histórico. Em falha devolve o que conseguiu e informa o motivo em <paramref name="error"/>.</summary>
    public static Dictionary<string, SmartHistoryEntry> LoadHistory(out string? error, string? file = null)
    {
        error = null;
        var h = new Dictionary<string, SmartHistoryEntry>();
        var f = file ?? HistoryFile;
        try
        {
            if (!File.Exists(f)) return h;
            var bytes = File.ReadAllBytes(f);
            // O histórico da versão em PowerShell foi gravado com BOM UTF-8, que o JsonDocument rejeita; descarta o BOM.
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, skip, bytes.Length - skip));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return h;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                h[p.Name] = new SmartHistoryEntry
                {
                    Date = p.Value.TryGetProperty("Date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "",
                    A5 = Num(p.Value, "A5"),
                    A197 = Num(p.Value, "A197"),
                    A198 = Num(p.Value, "A198"),
                    A199 = Num(p.Value, "A199"),
                };
            }
        }
        catch (Exception ex)
        {
            error = ex.Message.Trim();
        }
        return h;
    }

    /// <summary>Lê um número inteiro opcional do JSON; ausente ou de outro tipo vira null (entradas antigas/corrompidas não derrubam a leitura).</summary>
    private static long? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        return null;
    }

    /// <summary>Grava o histórico (só deve ser chamado como administrador). Devolve null em sucesso ou a mensagem de erro.</summary>
    public static string? SaveHistory(Dictionary<string, SmartHistoryEntry> h, string? file = null)
    {
        try
        {
            var f = file ?? HistoryFile;
            var dir = Path.GetDirectoryName(f);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(h, HistoryJson);
            File.WriteAllText(f, json, new UTF8Encoding(true));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message.Trim();
        }
    }
}
