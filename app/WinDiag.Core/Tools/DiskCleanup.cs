using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinDiag.Core.Tools;

/// <summary>Resultado de uma limpeza de pasta.</summary>
/// <param name="FreedBytes">Bytes liberados (soma do tamanho dos arquivos apagados).</param>
/// <param name="Deleted">Arquivos apagados.</param>
/// <param name="Skipped">Itens pulados: arquivos em uso, sem permissão ou links de pasta (junctions e links simbólicos).</param>
public sealed record CleanResult(long FreedBytes, int Deleted, int Skipped)
{
    /// <summary>Soma dois resultados.</summary>
    /// <param name="o">Outro resultado.</param>
    public CleanResult Plus(CleanResult o) => new(FreedBytes + o.FreedBytes, Deleted + o.Deleted, Skipped + o.Skipped);
}

/// <summary>Alvo de uma opção da limpeza: pasta, filtro de arquivo e regras de idade e exclusão.</summary>
/// <param name="Dir">Pasta alvo (caminho totalmente qualificado).</param>
/// <param name="Pattern">Filtro de nome de arquivo (ex.: "thumbcache_*.db"); "*" = tudo.</param>
/// <param name="Recurse">true para entrar nas subpastas.</param>
/// <param name="MinAgeHours">Só considera arquivos cuja última gravação foi há mais de tantas horas (0 = todos).</param>
/// <param name="ExcludeTopDir">Nome de uma subpasta do primeiro nível que nunca é tocada (ex.: ".net"); null = nenhuma.</param>
public sealed record CleanTarget(string Dir, string Pattern, bool Recurse, int MinAgeHours = 0, string? ExcludeTopDir = null);

/// <summary>
/// Limpeza de pastas descartáveis. Regras de segurança: cada alvo é validado antes de medir ou apagar (caminho qualificado, não é raiz de unidade nem a pasta do
/// perfil, do Windows, de Arquivos de Programas ou do ProgramData, nem ancestral delas); só apaga dentro da pasta alvo; nunca entra em junctions nem links
/// simbólicos (cada pasta é aberta por handle e o caminho final confirmado sob o alvo; todos os componentes do alvo são conferidos); arquivo em uso ou sem
/// acesso é pulado e contado. Nunca lança exceção.
/// </summary>
public static class DiskCleanup
{
    /// <summary>Id da opção: temporários do usuário.</summary>
    public const string UserTemp = "user_temp";
    /// <summary>Id da opção: temporários do Windows.</summary>
    public const string WinTemp = "win_temp";
    /// <summary>Id da opção: cache de download do Windows Update.</summary>
    public const string WuCache = "wu_cache";
    /// <summary>Id da opção: cache da Otimização de Entrega.</summary>
    public const string DoCache = "do_cache";
    /// <summary>Id da opção: miniaturas do Explorador.</summary>
    public const string Thumbnails = "thumbnails";
    /// <summary>Id da opção: relatórios de erro do Windows.</summary>
    public const string Wer = "wer";
    /// <summary>Id da opção: Lixeira.</summary>
    public const string Recycle = "recycle";
    /// <summary>Idade mínima, em horas, dos arquivos temporários apagados.</summary>
    public const int TempMinAgeHours = 24;

    /// <summary>Todos os Ids de opção, na ordem de exibição.</summary>
    public static readonly string[] AllOptionIds = { UserTemp, WinTemp, WuCache, DoCache, Thumbnails, Wer, Recycle };
    /// <summary>Variáveis de ambiente que definem a pasta temporária do usuário, em ordem de preferência.</summary>
    private static readonly string[] TempVars = { "TMP", "TEMP" };

    /// <summary>true se a opção só funciona com administrador (pastas do Windows).</summary>
    /// <param name="id">Id da opção.</param>
    public static bool NeedsAdmin(string id) => id is WinTemp or WuCache or DoCache;

    /// <summary>true se a opção mexe em dados do perfil do usuário (HKCU, %TEMP%, %LOCALAPPDATA%, Lixeira): não vale quando o WinDiag foi elevado com outra conta.</summary>
    /// <param name="id">Id da opção.</param>
    public static bool IsPerUser(string id) => id is UserTemp or Thumbnails or Recycle;

    /// <summary>true se a opção vem marcada por padrão (todas, menos a Lixeira).</summary>
    /// <param name="id">Id da opção.</param>
    public static bool DefaultOn(string id) => id != Recycle;

    /// <summary>Rótulo da opção no idioma atual.</summary>
    /// <param name="id">Id da opção.</param>
    public static string Label(string id) => id switch
    {
        UserTemp => Loc.T("Arquivos temporários do usuário (com mais de 24 horas)", "User temporary files (older than 24 hours)"),
        WinTemp => Loc.T("Arquivos temporários do Windows (com mais de 24 horas)", "Windows temporary files (older than 24 hours)"),
        WuCache => Loc.T("Cache de download do Windows Update", "Windows Update download cache"),
        DoCache => Loc.T("Cache da Otimização de Entrega", "Delivery Optimization cache"),
        Thumbnails => Loc.T("Miniaturas do Explorador de Arquivos", "File Explorer thumbnails"),
        Wer => Loc.T("Relatórios de erro do Windows", "Windows error reports"),
        Recycle => Loc.T("Lixeira", "Recycle Bin"),
        _ => id,
    };

    /// <summary>Junta uma pasta base e partes; null se a base for vazia (ex.: Environment.GetFolderPath devolveu "").</summary>
    /// <param name="root">Pasta base.</param>
    /// <param name="parts">Partes do caminho.</param>
    private static string? Join(string? root, params string[] parts) =>
        string.IsNullOrWhiteSpace(root) ? null : Path.Combine(new[] { root }.Concat(parts).ToArray());

    /// <summary>Pasta temporária do usuário a partir de TMP ou TEMP; null se ambas estiverem vazias ou inválidas (Path.GetTempPath cairia para o perfil ou o Windows).</summary>
    private static string? UserTempDir()
    {
        foreach (var v in TempVars)
        {
            var s = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(s) && Path.IsPathFullyQualified(s)) return s;
        }
        return null;
    }

    /// <summary>Pastas e filtros de uma opção (vazio para a Lixeira, que usa a API do Windows, e para alvos que não passam na validação).</summary>
    /// <param name="id">Id da opção.</param>
    /// <param name="includePerUser">false omite as pastas do perfil do usuário (usado quando o WinDiag foi elevado com outra conta).</param>
    public static IReadOnlyList<CleanTarget> Targets(string id, bool includePerUser = true)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var raw = new List<CleanTarget?>();
        switch (id)
        {
            case UserTemp:
                if (includePerUser && UserTempDir() is { } ut) raw.Add(new CleanTarget(ut, "*", true, TempMinAgeHours, ".net"));
                break;
            case WinTemp:
                if (Join(win, "Temp") is { } wt) raw.Add(new CleanTarget(wt, "*", true, TempMinAgeHours, ".net"));
                break;
            case WuCache:
                if (Join(win, "SoftwareDistribution", "Download") is { } wu) raw.Add(new CleanTarget(wu, "*", true));
                break;
            case DoCache:
                if (Join(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") is { } d) raw.Add(new CleanTarget(d, "*", true));
                break;
            case Thumbnails:
                if (includePerUser && Join(local, "Microsoft", "Windows", "Explorer") is { } th) raw.Add(new CleanTarget(th, "thumbcache_*.db", false));
                break;
            case Wer:
                foreach (var p in new[] { Join(common, "Microsoft", "Windows", "WER", "ReportArchive"), Join(common, "Microsoft", "Windows", "WER", "ReportQueue") })
                    if (p is not null) raw.Add(new CleanTarget(p, "*", true));
                if (includePerUser)
                    foreach (var p in new[] { Join(local, "Microsoft", "Windows", "WER", "ReportArchive"), Join(local, "Microsoft", "Windows", "WER", "ReportQueue") })
                        if (p is not null) raw.Add(new CleanTarget(p, "*", true));
                break;
        }
        return raw.Where(t => t is not null && IsSafeTarget(t.Dir)).Select(t => t!).ToList();
    }

    /// <summary>Pastas do cache de shaders (DirectX, NVIDIA e AMD) em %LOCALAPPDATA%; vazio se essa pasta não puder ser resolvida.</summary>
    public static IReadOnlyList<CleanTarget> ShaderTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new[]
        {
            Join(local, "D3DSCache"), Join(local, "NVIDIA", "DXCache"), Join(local, "NVIDIA", "GLCache"),
            Join(local, "AMD", "DxCache"), Join(local, "AMD", "DxcCache"),
        };
        return list.Where(p => p is not null && IsSafeTarget(p)).Select(p => new CleanTarget(p!, "*", true)).ToList();
    }

    // ------------------------------------------------------------------ validação do alvo

    /// <summary>true se <paramref name="ancestor"/> é igual a <paramref name="path"/> ou uma pasta que a contém (comparação sem diferenciar maiúsculas e sem barra final).</summary>
    /// <param name="ancestor">Possível ancestral.</param>
    /// <param name="path">Caminho.</param>
    public static bool IsSameOrAncestor(string ancestor, string path)
    {
        var a = ancestor.TrimEnd('\\', '/') + "\\";
        var p = path.TrimEnd('\\', '/') + "\\";
        return p.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pastas que nunca podem ser alvo (nem ancestrais de alvo): perfil do usuário e a raiz dos perfis, Windows, Arquivos de Programas, ProgramData e a raiz da unidade do sistema.</summary>
    private static List<string> Forbidden()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { list.Add(Path.GetFullPath(p).TrimEnd('\\')); } catch (Exception) { }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(profile);
        if (!string.IsNullOrWhiteSpace(profile)) { try { Add(Path.GetDirectoryName(profile.TrimEnd('\\'))); } catch (Exception) { } }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Path.GetPathRoot(Environment.SystemDirectory));
        return list;
    }

    /// <summary>
    /// true se a pasta pode ser alvo de limpeza: caminho totalmente qualificado e não vazio, não é raiz de unidade, e não é igual nem ancestral do perfil do usuário,
    /// da raiz dos perfis, do Windows, de Arquivos de Programas, do ProgramData ou da raiz do sistema.
    /// </summary>
    /// <param name="dir">Pasta candidata.</param>
    public static bool IsSafeTarget(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) return false;
            var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            var root = Path.GetPathRoot(full)?.TrimEnd('\\', '/') ?? "";
            if (full.Length == 0 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
            if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Count(c => c == '\\') < 4) return false; // raiz de compartilhamento
            foreach (var f in Forbidden())
                if (IsSameOrAncestor(full, f)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>true se nenhum componente do alvo, abaixo da pasta de base conhecida (LocalAppData, ProgramData ou Windows; sem base, só o alvo e seu pai), é ponto de reanálise.</summary>
    /// <param name="full">Caminho completo do alvo.</param>
    private static bool ComponentsOk(string full)
    {
        try
        {
            full = Path.GetFullPath(full).TrimEnd('\\');
            string? best = null;
            foreach (var b in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows })
            {
                var bp = Environment.GetFolderPath(b);
                if (string.IsNullOrWhiteSpace(bp)) continue;
                bp = Path.GetFullPath(bp).TrimEnd('\\');
                if (IsSameOrAncestor(bp, full) && (best is null || bp.Length > best.Length)) best = bp;
            }
            var chain = new List<string>();
            if (best is null)
            {
                chain.Add(full);
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) chain.Add(parent);
            }
            else
            {
                var cur = full;
                while (cur.Length > best.Length && !string.Equals(cur, best, StringComparison.OrdinalIgnoreCase))
                {
                    chain.Add(cur);
                    cur = Path.GetDirectoryName(cur) ?? best;
                }
            }
            foreach (var c in chain)
                if (Directory.Exists(c) && (File.GetAttributes(c) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ handles (kernel32)

    /// <summary>Estrutura BY_HANDLE_FILE_INFORMATION da Win32 (horários como pares de uint).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInfo
    {
        /// <summary>Atributos do arquivo ou pasta.</summary>
        public uint dwFileAttributes;
        /// <summary>Horário de criação (parte baixa).</summary>
        public uint ftCreationLow;
        /// <summary>Horário de criação (parte alta).</summary>
        public uint ftCreationHigh;
        /// <summary>Último acesso (parte baixa).</summary>
        public uint ftAccessLow;
        /// <summary>Último acesso (parte alta).</summary>
        public uint ftAccessHigh;
        /// <summary>Última gravação (parte baixa).</summary>
        public uint ftWriteLow;
        /// <summary>Última gravação (parte alta).</summary>
        public uint ftWriteHigh;
        /// <summary>Número de série do volume.</summary>
        public uint dwVolumeSerialNumber;
        /// <summary>Tamanho (parte alta).</summary>
        public uint nFileSizeHigh;
        /// <summary>Tamanho (parte baixa).</summary>
        public uint nFileSizeLow;
        /// <summary>Número de links.</summary>
        public uint nNumberOfLinks;
        /// <summary>Índice (parte alta).</summary>
        public uint nFileIndexHigh;
        /// <summary>Índice (parte baixa).</summary>
        public uint nFileIndexLow;
    }

    /// <summary>Abre um arquivo ou pasta (CreateFileW).</summary>
    /// <param name="lpFileName">Caminho.</param>
    /// <param name="dwDesiredAccess">Acesso desejado.</param>
    /// <param name="dwShareMode">Compartilhamento.</param>
    /// <param name="lpSecurityAttributes">Atributos de segurança (zero).</param>
    /// <param name="dwCreationDisposition">Disposição (OPEN_EXISTING = 3).</param>
    /// <param name="dwFlagsAndAttributes">Opções (BACKUP_SEMANTICS e OPEN_REPARSE_POINT).</param>
    /// <param name="hTemplateFile">Modelo (zero).</param>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>Lê os atributos de um arquivo aberto.</summary>
    /// <param name="hFile">Handle aberto.</param>
    /// <param name="lpFileInformation">Informações devolvidas.</param>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInfo lpFileInformation);

    /// <summary>Lê o caminho final (já resolvido) de um handle aberto.</summary>
    /// <param name="hFile">Handle aberto.</param>
    /// <param name="lpszFilePath">Buffer de saída.</param>
    /// <param name="cchFilePath">Tamanho do buffer.</param>
    /// <param name="dwFlags">Opções (0 = nome DOS).</param>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>
    /// Abre a pasta por handle sem seguir links (BACKUP_SEMANTICS | OPEN_REPARSE_POINT) e sem permitir que ela seja renomeada ou apagada enquanto o handle estiver aberto,
    /// confirma que não é ponto de reanálise e devolve o caminho final. Null se não abriu ou se for um link.
    /// </summary>
    /// <param name="path">Pasta.</param>
    /// <param name="finalPath">Caminho final resolvido (sem o prefixo \\?\).</param>
    private static SafeFileHandle? OpenDirChecked(string path, out string finalPath)
    {
        finalPath = "";
        SafeFileHandle? h = null;
        try
        {
            h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 0x1 | 0x2 /* leitura e escrita, sem DELETE */, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            if (!GetFileInformationByHandle(h, out var info) || (info.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.dwFileAttributes & (uint)FileAttributes.Directory) == 0)
            {
                h.Dispose(); return null;
            }
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) { h.Dispose(); return null; }
            var s = sb.ToString();
            if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) s = @"\\" + s[8..];
            else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
            finalPath = s.TrimEnd('\\');
            return h;
        }
        catch (Exception) { h?.Dispose(); return null; }
    }

    // ------------------------------------------------------------------ medir e limpar

    /// <summary>true se o arquivo passa na regra de idade do alvo (última gravação há mais de <see cref="CleanTarget.MinAgeHours"/> horas).</summary>
    /// <param name="f">Arquivo.</param>
    /// <param name="t">Alvo.</param>
    private static bool OldEnough(FileInfo f, CleanTarget t) => t.MinAgeHours <= 0 || f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-t.MinAgeHours);

    /// <summary>true se a subpasta do primeiro nível é a excluída pelo alvo.</summary>
    /// <param name="name">Nome da subpasta.</param>
    /// <param name="t">Alvo.</param>
    /// <param name="depth">Profundidade (0 = filhos do alvo).</param>
    private static bool Excluded(string name, CleanTarget t, int depth) => depth == 0 && t.ExcludeTopDir is not null && string.Equals(name, t.ExcludeTopDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Soma o tamanho dos arquivos que casam com o alvo (respeitando idade e exclusão), sem seguir links. Null se o alvo é inválido ou não pôde ser lido; 0 se não existe.</summary>
    /// <param name="t">Alvo.</param>
    public static long? Measure(CleanTarget t)
    {
        try
        {
            if (!IsSafeTarget(t.Dir)) return null;
            if (!Directory.Exists(t.Dir)) return 0;
            if (!ComponentsOk(t.Dir)) return 0;
            return MeasureDir(new DirectoryInfo(t.Dir), t, 0);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Soma recursiva, pulando subpastas que são links.</summary>
    /// <param name="dir">Pasta atual.</param>
    /// <param name="t">Alvo (filtro, idade, exclusão e recursão).</param>
    /// <param name="depth">Profundidade.</param>
    private static long MeasureDir(DirectoryInfo dir, CleanTarget t, int depth)
    {
        long sum = 0;
        try
        {
            foreach (var f in dir.EnumerateFiles(t.Pattern))
            {
                try { if ((f.Attributes & FileAttributes.ReparsePoint) == 0 && OldEnough(f, t)) sum += f.Length; } catch (Exception) { }
            }
            if (!t.Recurse) return sum;
            foreach (var d in dir.EnumerateDirectories())
            {
                try { if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Excluded(d.Name, t, depth)) sum += MeasureDir(d, t, depth + 1); } catch (Exception) { }
            }
        }
        catch (Exception) { /* pasta sem acesso: conta o que deu */ }
        return sum;
    }

    /// <summary>Apaga os arquivos do alvo (e as subpastas que ficarem vazias). Valida o alvo, confere os componentes e abre cada pasta por handle antes de apagar o conteúdo. Pula e conta o que estiver em uso, sem permissão, for link ou fora do alvo. A própria pasta alvo nunca é apagada.</summary>
    /// <param name="t">Alvo.</param>
    /// <param name="ct">Cancelamento (interrompe entre arquivos).</param>
    public static CleanResult Clean(CleanTarget t, CancellationToken ct = default)
    {
        try
        {
            if (!IsSafeTarget(t.Dir)) return new CleanResult(0, 0, 1);
            if (!Directory.Exists(t.Dir)) return new CleanResult(0, 0, 0);
            var full = Path.GetFullPath(t.Dir).TrimEnd('\\');
            if (!ComponentsOk(full)) return new CleanResult(0, 0, 1);
            using var root = OpenDirChecked(full, out var rootFinal);
            if (root is null) return new CleanResult(0, 0, 1);
            return CleanDir(full, rootFinal, t, 0, ct);
        }
        catch (Exception) { return new CleanResult(0, 0, 1); }
    }

    /// <summary>Limpeza recursiva de uma pasta: abre por handle, confirma que não é link e que o caminho final continua sob a raiz do alvo, e só então apaga o conteúdo.</summary>
    /// <param name="dirPath">Pasta atual.</param>
    /// <param name="rootFinal">Caminho final (resolvido) do alvo.</param>
    /// <param name="t">Alvo (filtro, idade, exclusão e recursão).</param>
    /// <param name="depth">Profundidade (0 = o próprio alvo).</param>
    /// <param name="ct">Cancelamento.</param>
    private static CleanResult CleanDir(string dirPath, string rootFinal, CleanTarget t, int depth, CancellationToken ct)
    {
        long freed = 0; int deleted = 0, skipped = 0;
        using var h = OpenDirChecked(dirPath, out var final);
        if (h is null || !IsSameOrAncestor(rootFinal, final)) return new CleanResult(0, 0, 1);
        var dir = new DirectoryInfo(dirPath);
        try
        {
            foreach (var f in dir.EnumerateFiles(t.Pattern))
            {
                if (ct.IsCancellationRequested) return new CleanResult(freed, deleted, skipped);
                try
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if (!OldEnough(f, t)) continue;
                    long len = f.Length;
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    freed += len; deleted++;
                }
                catch (Exception) { skipped++; }
            }
            if (t.Recurse)
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (Excluded(d.Name, t, depth)) continue;
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        var r = CleanDir(d.FullName, rootFinal, t, depth + 1, ct);
                        freed += r.FreedBytes; deleted += r.Deleted; skipped += r.Skipped;
                        try { d.Delete(false); } catch (Exception) { /* não está vazia ou em uso */ }
                    }
                    catch (Exception) { skipped++; }
                }
            }
        }
        catch (Exception) { skipped++; }
        return new CleanResult(freed, deleted, skipped);
    }

    /// <summary>Apaga uma pasta e seu conteúdo sem nunca seguir links: subpastas que são pontos de reanálise são deixadas como estão (e, por isso, a pasta pai pode não ser removida). Devolve true se a pasta deixou de existir. Nunca lança exceção.</summary>
    /// <param name="dir">Pasta a apagar.</param>
    public static bool DeleteTreeNoFollow(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch (Exception) { }
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                try { DeleteTreeNoFollow(d); } catch (Exception) { }
            }
            try { Directory.Delete(dir, false); } catch (Exception) { }
            return !Directory.Exists(dir);
        }
        catch (Exception) { return false; }
    }
    // ------------------------------------------------------------------ Lixeira (shell32)

    /// <summary>Estrutura SHQUERYRBINFO da Win32.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        /// <summary>Tamanho da estrutura em bytes.</summary>
        public uint cbSize;
        /// <summary>Tamanho total dos itens da Lixeira, em bytes.</summary>
        public long i64Size;
        /// <summary>Quantidade de itens.</summary>
        public long i64NumItems;
    }

    /// <summary>Consulta o tamanho da Lixeira (todas as unidades).</summary>
    /// <param name="pszRootPath">Unidade (null = todas).</param>
    /// <param name="pSHQueryRBInfo">Estrutura de saída.</param>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    /// <summary>Esvazia a Lixeira.</summary>
    /// <param name="hwnd">Janela dona (zero).</param>
    /// <param name="pszRootPath">Unidade (null = todas).</param>
    /// <param name="dwFlags">SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND.</param>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>Tamanho atual da Lixeira em bytes; null se não foi possível ler.</summary>
    public static long? RecycleBinBytes()
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Esvazia a Lixeira de todas as unidades, sem janelas de confirmação. Devolve (bytes liberados, sucesso). Lixeira já vazia conta como sucesso.</summary>
    public static (long Freed, bool Ok) EmptyRecycleBin()
    {
        try
        {
            long before = RecycleBinBytes() ?? 0;
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
            // 0x8000FFFF-like retornos aparecem quando já está vazia (S_FALSE/erro benigno): confere o tamanho depois.
            bool ok = hr == 0 || (RecycleBinBytes() ?? long.MaxValue) == 0;
            return (ok ? before : 0, ok);
        }
        catch (Exception) { return (0, false); }
    }

    /// <summary>Formata bytes no idioma atual (KB, MB ou GB).</summary>
    /// <param name="bytes">Tamanho em bytes.</param>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1073741824L) return Fmt.GB(bytes);
        if (bytes >= 1048576L) return Fmt.MB(bytes, 1);
        return Fmt.Num(bytes / 1024d, 0) + " KB";
    }
}
