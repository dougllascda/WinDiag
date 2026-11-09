using System.Globalization;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Util;

/// <summary>Leitura do nvidia-smi (somente consulta), sem janela e com timeout. Não exige administrador; sem a ferramenta (ou sem GPU NVIDIA) devolve null/vazio.</summary>
public static class NvidiaSmi
{
    /// <summary>Caminho resolvido do nvidia-smi.exe (null = não encontrado). Protegido por <see cref="Lock"/>.</summary>
    private static string? _path;
    /// <summary>Indica que a busca do executável já foi feita, para não repetir o acesso ao disco a cada leitura.</summary>
    private static bool _resolved;
    /// <summary>Sincroniza a resolução do caminho, pois o monitoramento ao vivo e os coletores chamam em threads diferentes.</summary>
    private static readonly object Lock = new();

    /// <summary>Caminho do nvidia-smi.exe (PATH, System32, NVSMI). Resolvido uma vez e guardado em cache.</summary>
    public static string? Path
    {
        get
        {
            lock (Lock)
            {
                if (_resolved) return _path;
                _resolved = true;
                try { _path = Resolve(); } catch { _path = null; }
                return _path;
            }
        }
    }

    /// <summary>Procura o nvidia-smi.exe no PATH, depois em System32 e em Program Files\NVIDIA Corporation\NVSMI (instalações antigas). Retorna null se não existir.</summary>
    private static string? Resolve()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = System.IO.Path.Combine(dir.Trim().Trim('"'), "nvidia-smi.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var candidates = new[]
        {
            System.IO.Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Divide uma linha CSV do nvidia-smi (--format=csv,noheader[,nounits]). Campos vazios, "[N/A]",
    /// "[Not Supported]", "N/A" ou qualquer valor entre colchetes viram null.
    /// </summary>
    public static string?[] ParseCsvLine(string? line)
    {
        if (line is null) return Array.Empty<string?>();
        var parts = line.Split(',');
        var res = new string?[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var t = parts[i].Trim();
            bool bad = t.Length == 0 || t.StartsWith('[') ||
                       t.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                       t.Equals("Not Supported", StringComparison.OrdinalIgnoreCase) ||
                       t.Contains("not a valid", StringComparison.OrdinalIgnoreCase);
            res[i] = bad ? null : t;
        }
        return res;
    }

    /// <summary>Executa --query-gpu com os campos e devolve a primeira linha (primeira GPU), ou null em falha.</summary>
    public static string?[]? QueryRow(string smi, IReadOnlyList<string> fields, bool nounits, int timeoutMs)
    {
        // nounits remove "MiB"/"W"/"MHz" dos valores para permitir conversão numérica direta.
        var args = "--query-gpu=" + string.Join(',', fields) + (nounits ? " --format=csv,noheader,nounits" : " --format=csv,noheader");
        var r = Proc.Run(smi, args, timeoutMs, System.Text.Encoding.UTF8);
        if (!r.Ok) return null;
        // Só a primeira linha não vazia: em máquinas com várias GPUs NVIDIA os dados da GPU 0 são usados.
        var line = r.Lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null || line.Contains("not a valid", StringComparison.OrdinalIgnoreCase)) return null;
        var row = ParseCsvLine(line);
        return row.Length == fields.Count ? row : null;
    }

    /// <summary>
    /// Consulta vários campos. Tenta uma chamada só; se algum campo não existir nesta versão do driver,
    /// cai para uma chamada por campo. Campos indisponíveis ficam null no dicionário.
    /// </summary>
    public static Dictionary<string, string?> QueryFields(string smi, IReadOnlyList<string> fields, int timeoutMs = 8000)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var row = QueryRow(smi, fields, true, timeoutMs);
            if (row is not null)
            {
                for (int i = 0; i < fields.Count; i++) d[fields[i]] = row[i];
                return d;
            }
            foreach (var f in fields)
            {
                string? v = null;
                try { v = QueryRow(smi, new[] { f }, true, timeoutMs)?[0]; } catch { }
                d[f] = v;
            }
        }
        catch { }
        return d;
    }

    /// <summary>Campos clocks_event_reasons.* (ou clocks_throttle_reasons.* em drivers antigos) com o texto exibido em cada idioma. Cada campo vale "Active"/"Not Active".</summary>
    private static readonly (string Key, string Pt, string En)[] ThrottleNames =
    {
        ("gpu_idle", "GPU ocioso", "GPU idle"),
        ("applications_clocks_setting", "Clocks de aplicação definidos", "Application clocks set"),
        ("sw_power_cap", "Limite de potência (software)", "Power cap (software)"),
        ("hw_slowdown", "Redução de hardware", "Hardware slowdown"),
        ("sw_thermal_slowdown", "Redução térmica (software)", "Thermal slowdown (software)"),
        ("hw_thermal_slowdown", "Redução térmica (hardware)", "Thermal slowdown (hardware)"),
        ("hw_power_brake_slowdown", "Freio de potência (hardware)", "Power brake (hardware)"),
        ("sync_boost", "Sync boost", "Sync boost"),
    };

    /// <summary>Motivos de limitação de clock ativos agora; lista vazia = nenhuma; null = não suportado.</summary>
    public static List<string>? ThrottleReasons(string smi, int timeoutMs = 8000)
    {
        // Drivers novos renomearam clocks_throttle_reasons para clocks_event_reasons; tenta o nome novo e depois o antigo.
        foreach (var prefix in new[] { "clocks_event_reasons.", "clocks_throttle_reasons." })
        {
            try
            {
                var fields = ThrottleNames.Select(n => prefix + n.Key).ToList();
                var row = QueryRow(smi, fields, false, timeoutMs);
                if (row is null) continue;
                var act = new List<string>();
                for (int i = 0; i < row.Length; i++)
                    if (string.Equals(row[i], "Active", StringComparison.OrdinalIgnoreCase)) act.Add(Loc.T(ThrottleNames[i].Pt, ThrottleNames[i].En));
                return act;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Converte a máscara de clocks_event_reasons.active (hexadecimal, ex.: 0x0000000000000004) em nomes.
    /// Bits conforme o NVML (nvmlClocksEventReason*). 0 = "nenhuma"; null = null.
    /// </summary>
    public static string? DescribeEventReasons(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return null;
        var t = mask.Trim();
        ulong v;
        try
        {
            v = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(t, CultureInfo.InvariantCulture);
        }
        catch { return null; }
        if (v == 0) return Loc.T("nenhuma", "none");
        var names = new List<string>();
        // Consome cada bit conhecido; o que sobrar em v é tratado como código desconhecido.
        void Chk(ulong bit, string n) { if ((v & bit) != 0) { names.Add(n); v &= ~bit; } }
        Chk(0x1, Loc.T("GPU ocioso", "GPU idle"));
        Chk(0x2, Loc.T("Clocks de aplicação definidos", "Application clocks set"));
        Chk(0x4, Loc.T("Limite de potência (software)", "Power cap (software)"));
        Chk(0x8, Loc.T("Redução de hardware", "Hardware slowdown"));
        Chk(0x10, "Sync boost");
        Chk(0x20, Loc.T("Redução térmica (software)", "Thermal slowdown (software)"));
        Chk(0x40, Loc.T("Redução térmica (hardware)", "Thermal slowdown (hardware)"));
        Chk(0x80, Loc.T("Freio de potência (hardware)", "Power brake (hardware)"));
        Chk(0x100, Loc.T("Clock de vídeo definido", "Video clock set"));
        // Bits fora da lista documentada: mostrados só como código, sem afirmar causa.
        if (v != 0 && names.Count == 0) return Loc.T($"nenhuma conhecida (código 0x{v:X})", $"none known (code 0x{v:X})");
        if (v != 0) names.Add(Loc.T($"código 0x{v:X}", $"code 0x{v:X}"));
        return string.Join(", ", names);
    }

    /// <summary>Tamanho total do BAR1 em MiB (nvidia-smi -q -d MEMORY), ou null.</summary>
    public static double? Bar1TotalMiB(string smi, int timeoutMs = 8000)
    {
        try
        {
            var r = Proc.Run(smi, "-q -d MEMORY", timeoutMs, System.Text.Encoding.UTF8);
            if (!r.Ok) return null;
            var m = Regex.Match(r.StdOut, @"BAR1 Memory Usage\s+Total\s*:\s*(\d+)\s*MiB");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }
        catch { return null; }
    }

    /// <summary>Subsystem vendor ID PCI (16 bits baixos de pci.sub_device_id) para o fabricante da placa (parceiro da NVIDIA). Lista de IDs públicos do PCI-SIG; ID ausente vira "n/d".</summary>
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1043"] = "ASUS", ["1458"] = "Gigabyte", ["1462"] = "MSI", ["3842"] = "EVGA", ["19DA"] = "Zotac",
        ["1569"] = "Palit", ["10B0"] = "Gainward", ["1B4C"] = "Galax/KFA2", ["7377"] = "Colorful", ["196E"] = "PNY",
        ["10DE"] = "NVIDIA (Founders)", ["1682"] = "XFX", ["174B"] = "Sapphire", ["1DA2"] = "Sapphire",
        ["148C"] = "PowerColor", ["1849"] = "ASRock",
    };

    /// <summary>Fabricante da placa pelos 4 últimos dígitos hexadecimais de pci.sub_device_id (ex.: 0x50801462 = MSI).</summary>
    public static string BoardVendor(string? subId)
    {
        if (string.IsNullOrWhiteSpace(subId)) return Fmt.NA;
        var h = subId.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length < 4) return Fmt.NA;
        return Vendors.TryGetValue(h[^4..], out var v) ? v : Fmt.NA;
    }

    /// <summary>"32.0.16.1742" -> "617.42" (versão NVIDIA a partir da versão do driver do Windows).</summary>
    public static string FormatNvidiaDriver(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return Fmt.NA;
        var d = v.Split('.');
        if (d.Length == 4 && d[2].Length >= 1)
        {
            // Formato do driver do Windows: os 5 últimos dígitos (último dígito do 3º campo + 4º campo) formam a versão NVIDIA, ex.: 6 + 1742 = 617.42.
            var n = d[2][^1] + d[3];
            if (n.Length == 5) return n[..3] + "." + n[3..];
        }
        return v;
    }
}
