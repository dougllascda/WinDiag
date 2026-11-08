using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinDiag.Core.Fixes;

/// <summary>Leitura e gravação de snapshots (JSON) e do changes.log, em %ProgramData%\WinDiagMaint (mesmas pastas da versão 7).</summary>
public sealed class SnapshotStore
{
    /// <summary>Pasta de dados (padrão: %ProgramData%\WinDiagMaint).</summary>
    public string DataDir { get; }
    /// <summary>Pasta dos snapshots.</summary>
    public string SnapDir => System.IO.Path.Combine(DataDir, "snapshots");
    /// <summary>Arquivo de registro de alterações.</summary>
    public string ChangeLog => System.IO.Path.Combine(DataDir, "changes.log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Cria o repositório.</summary>
    /// <param name="dataDir">Pasta de dados; null usa %ProgramData%\WinDiagMaint (os testes passam uma pasta temporária).</param>
    public SnapshotStore(string? dataDir = null)
    {
        DataDir = dataDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinDiagMaint");
    }

    /// <summary>Todos os snapshots, mais recente primeiro. Arquivos ilegíveis são ignorados.</summary>
    public List<SnapshotFile> List()
    {
        var list = new List<SnapshotFile>();
        if (!Directory.Exists(SnapDir)) return list;
        foreach (var f in Directory.GetFiles(SnapDir, "snapshot_*.json"))
        {
            try
            {
                var text = File.ReadAllText(f); // detecta BOM (a v7 gravava UTF-8 com BOM)
                var snap = JsonSerializer.Deserialize<Snapshot>(text, JsonOpts);
                if (snap is null) continue;
                var created = DateTime.TryParse(snap.Created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
                    ? dt : File.GetLastWriteTime(f);
                list.Add(new SnapshotFile(f, created, snap));
            }
            catch { }
        }
        return list.OrderByDescending(s => s.Created)
            .ThenByDescending(s => FileSuffix(s.Path))
            .ThenByDescending(s => LastWriteUtc(s.Path))
            .ToList();
    }

    /// <summary>Sufixo numérico de desempate do nome (snapshot_yyyyMMdd_HHmmss_N.json → N; sem sufixo → 0).</summary>
    /// <param name="path">Caminho ou nome do arquivo.</param>
    public static int FileSuffix(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileNameWithoutExtension(path), @"^snapshot_\d{8}_\d{6}_(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static DateTime LastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>Regrava um snapshot já salvo (usado para acrescentar o estado resultante depois de aplicar). Mantém o campo Created.</summary>
    /// <param name="path">Arquivo devolvido por <see cref="Save"/>.</param>
    /// <param name="snap">Conteúdo atualizado.</param>
    public void Rewrite(string path, Snapshot snap)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.Equals(dir, System.IO.Path.GetFullPath(SnapDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O snapshot a regravar não está na pasta de snapshots.");
        File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonOpts), new UTF8Encoding(false));
    }

    /// <summary>Grava o snapshot (UTF-8 sem BOM) e devolve o caminho. Lança exceção se não houver permissão de escrita.</summary>
    public string Save(Snapshot snap)
    {
        Directory.CreateDirectory(SnapDir);
        var now = DateTime.Now;
        snap.Created = now.ToString("s", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(SnapDir, $"snapshot_{now:yyyyMMdd_HHmmss}.json");
        int n = 1;
        while (File.Exists(path)) path = System.IO.Path.Combine(SnapDir, $"snapshot_{now:yyyyMMdd_HHmmss}_{n++}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonOpts), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Acrescenta uma linha com data/hora ao changes.log. Falhas de gravação são ignoradas.</summary>
    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(ChangeLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Entrada mais recente (Aplicar ou Desfazer) de um Id, com o snapshot de origem.
    /// Usada para saber se um item ativo foi ligado pelo WinDiag.
    /// </summary>
    public static (SnapshotFix Fix, SnapshotFile File)? LatestEntry(IEnumerable<SnapshotFile> snaps, string id)
    {
        foreach (var s in snaps) // já ordenado do mais recente
        {
            var fx = s.Data.Fixes.LastOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (fx is not null) return (fx, s);
        }
        return null;
    }
}
