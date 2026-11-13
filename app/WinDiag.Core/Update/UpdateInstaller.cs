using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using WinDiag.Core.Fixes;
using WinDiag.Core.Tools;

namespace WinDiag.Core.Update;

/// <summary>Forma como o WinDiag está instalado, que define qual arquivo da versão nova serve.</summary>
public enum InstallKind
{
    /// <summary>Instalado pelo instalador (existe unins000.exe ao lado do executável).</summary>
    Installer,
    /// <summary>Executável único autocontido (traz o .NET dentro).</summary>
    Portable,
    /// <summary>Executável que depende do .NET instalado no PC.</summary>
    FrameworkDependent,
}

/// <summary>
/// Atualização baixada e verificada, pronta para aplicar. Carrega o handle do arquivo aberto com FileShare.Read (ninguém consegue gravar nem apagar o arquivo enquanto ele
/// existir), do qual o SHA-256 foi calculado; ele só é fechado em <see cref="Dispose"/> (que <see cref="UpdateInstaller.Apply"/> chama depois de iniciar o processo).
/// </summary>
/// <param name="Path">Caminho do arquivo baixado.</param>
/// <param name="Version">Versão baixada.</param>
/// <param name="Kind">Tipo de instalação a que o arquivo se destina.</param>
public sealed record DownloadedUpdate(string Path, string Version, InstallKind Kind) : IDisposable
{
    /// <summary>Handle de leitura do arquivo verificado, mantido aberto até <see cref="Dispose"/>; null se não houver (ex.: testes).</summary>
    public FileStream? Handle { get; init; }

    /// <summary>Fecha o handle do arquivo verificado. Seguro chamar mais de uma vez.</summary>
    public void Dispose() => Handle?.Dispose();
}
/// <summary>
/// Baixa, verifica e aplica a atualização do próprio WinDiag. Segurança: só aceita arquivos do repositório oficial no GitHub
/// (https://github.com/dougllascda/WinDiag/releases/download/...), do mesmo release que o SHA256SUMS.txt, e só entrega o arquivo se o SHA-256
/// calculado bater com o do SHA256SUMS.txt. Nada acontece sozinho: o App chama cada etapa depois de o usuário pedir.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Prefixo obrigatório de qualquer endereço de download.</summary>
    public const string AllowedPrefix = "https://github.com/dougllascda/WinDiag/releases/download/";
    /// <summary>Nome do arquivo com os hashes SHA-256 do release.</summary>
    public const string SumsName = "SHA256SUMS.txt";
    /// <summary>Tamanho máximo aceito para o arquivo da atualização (400 MB).</summary>
    public const long MaxBytes = 400L * 1024 * 1024;
    /// <summary>Tamanho a partir do qual o executável é considerado autocontido (30 MB): heurística, pois não há um marcador oficial no arquivo.</summary>
    public const long SelfContainedMinBytes = 30L * 1024 * 1024;
    /// <summary>Argumentos do instalador (Inno Setup): silencioso, sem caixas de mensagem, sem reiniciar o Windows e reabrindo o app no fim.</summary>
    public const string InstallerArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";
    /// <summary>Argumento passado ao app novo depois da troca do executável.</summary>
    public const string UpdatedArg = "--updated";

    /// <summary>Tempo sem receber dados que cancela o download (60 s). Os testes podem reduzi-lo.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Nome de uma pasta de versão dentro da raiz de downloads (ex.: "1.3.0"). Só pastas com esse nome são apagadas pela limpeza.</summary>
    private static readonly System.Text.RegularExpressions.Regex VersionDir = new(@"^\d+(\.\d+){1,3}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Raiz antiga dos downloads: %LOCALAPPDATA%\WinDiag\update (gravável pelo usuário; só usada sem elevação e limpa na inicialização).</summary>
    public static string LegacyRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDiag", "update");

    /// <summary>Raiz protegida dos downloads: %ProgramData%\WinDiagMaint\update (só administradores e SYSTEM escrevem).</summary>
    public static string SecureRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinDiagMaint", "update");

    /// <summary>Pasta raiz dos downloads: a protegida (<see cref="SecureRoot"/>) quando o processo está elevado, para o arquivo não ser trocado por quem não é administrador entre baixar e executar; sem elevação (build de depuração), <see cref="LegacyRoot"/>.</summary>
    public static string UpdateRoot => ToolRunner.IsAdministrator() ? SecureRoot : LegacyRoot;
    // ------------------------------------------------------------------ tipo de instalação

    /// <summary>Detecta como o app está instalado: com unins000.exe ao lado do executável é Installer; senão Portable se o executável for grande (autocontido) e FrameworkDependent se for pequeno.</summary>
    public static InstallKind DetectKind()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return InstallKind.FrameworkDependent;
            long size = 0;
            try { size = new FileInfo(exe).Length; } catch (Exception) { }
            return DetectKind(System.IO.Path.GetDirectoryName(exe) ?? "", size);
        }
        catch (Exception) { return InstallKind.FrameworkDependent; }
    }

    /// <summary>Detecção a partir da pasta e do tamanho do executável (testável).</summary>
    /// <param name="exeDir">Pasta do executável.</param>
    /// <param name="exeSize">Tamanho do executável em bytes.</param>
    public static InstallKind DetectKind(string exeDir, long exeSize)
    {
        try { if (File.Exists(System.IO.Path.Combine(exeDir, "unins000.exe"))) return InstallKind.Installer; } catch (Exception) { }
        return exeSize > SelfContainedMinBytes ? InstallKind.Portable : InstallKind.FrameworkDependent;
    }

    /// <summary>Nome do arquivo da versão para o tipo de instalação (nomes reais do release).</summary>
    /// <param name="kind">Tipo de instalação.</param>
    /// <param name="version">Versão sem o "v" (ex.: "1.3.0").</param>
    public static string AssetNameFor(InstallKind kind, string version) => kind switch
    {
        InstallKind.Installer => $"WinDiag-Setup-{version}.exe",
        InstallKind.Portable => $"WinDiag-Portable-{version}.exe",
        _ => $"WinDiag-{version}-fx.exe",
    };

    // ------------------------------------------------------------------ validações puras

    /// <summary>true se o endereço é do repositório oficial: começa exatamente com <see cref="AllowedPrefix"/>, é https em github.com, sem ".." (nem codificado), sem barra invertida nem caracteres de controle.</summary>
    /// <param name="url">Endereço de download.</param>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(AllowedPrefix, StringComparison.Ordinal)) return false;
        if (url.Length == AllowedPrefix.Length) return false;
        if (url.Contains('\\') || url.Any(char.IsControl)) return false;
        string decoded;
        try { decoded = Uri.UnescapeDataString(url); } catch (Exception) { return false; }
        if (decoded.Contains("..", StringComparison.Ordinal) || decoded.Contains('\\')) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(u.UserInfo) && u.IsDefaultPort;
    }

    /// <summary>Pasta (até a última barra) de um endereço de download; usada para exigir que o arquivo e o SHA256SUMS.txt sejam do mesmo release.</summary>
    /// <param name="url">Endereço.</param>
    public static string ReleaseDir(string url) => url[..(url.LastIndexOf('/') + 1)];

    /// <summary>Lê o conteúdo de um SHA256SUMS.txt (formato do sha256sum: "hash  arquivo" ou "hash *arquivo"). Linhas inválidas são ignoradas; hashes saem em minúsculas; o nome é comparado sem diferenciar maiúsculas.</summary>
    /// <param name="text">Conteúdo do arquivo.</param>
    public static Dictionary<string, string> ParseSums(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return d;
        foreach (var raw in text.Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (!m.Success) continue;
            var name = m.Groups[2].Value.Trim();
            if (name.Contains('/') || name.Contains('\\')) name = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1)..];
            if (name.Length > 0) d[name] = m.Groups[1].Value.ToLowerInvariant();
        }
        return d;
    }

    // ------------------------------------------------------------------ pastas e proteção

    /// <summary>true se o caminho ou algum componente existente dele, até a raiz da unidade, é ponto de reanálise (junction ou link simbólico).</summary>
    /// <param name="path">Caminho.</param>
    public static bool HasReparseInChain(string path)
    {
        try
        {
            var cur = System.IO.Path.GetFullPath(path).TrimEnd('\\');
            var root = System.IO.Path.GetPathRoot(cur)?.TrimEnd('\\') ?? "";
            while (cur.Length > root.Length)
            {
                if ((Directory.Exists(cur) || File.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return true;
                cur = System.IO.Path.GetDirectoryName(cur) ?? root;
            }
            return false;
        }
        catch (Exception) { return true; /* sem como confirmar: trata como inseguro */ }
    }

    /// <summary>true se o dono da pasta é Administradores, SYSTEM ou (processo elevado) o usuário do próprio processo.</summary>
    /// <param name="dir">Pasta existente.</param>
    private static bool OwnerTrusted(string dir)
    {
        var owner = new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null) return false;
        if (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
        using var me = WindowsIdentity.GetCurrent();
        return ToolRunner.IsAdministrator() && me.User is not null && owner.Equals(me.User);
    }

    /// <summary>Garante uma pasta confiável: recusa ponto de reanálise (tenta remover o link e recriar), exige dono Administradores/SYSTEM e, ao criar, grava ACL explícita (herança desligada; Administradores e SYSTEM com controle total; nada para Usuários).</summary>
    /// <param name="path">Pasta.</param>
    /// <param name="applyAcl">true para criar/reaplicar a ACL restrita (a pasta de updates); false só valida (a pasta pai).</param>
    private static void EnsureTrustedDir(string path, bool applyAcl)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            try { Directory.Delete(path, false); } catch (Exception) { /* verificado abaixo */ }
            if (Directory.Exists(path))
                throw new InvalidOperationException(Loc.T($"A pasta {path} é um link e não pôde ser removida; atualização recusada.", $"The folder {path} is a link and could not be removed; update refused."));
        }
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var sid in new[] { admins, system })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.SetOwner(admins);
        if (!Directory.Exists(path))
        {
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (applyAcl) new DirectoryInfo(path).Create(sec); else Directory.CreateDirectory(path);
        }
        if (!OwnerTrusted(path))
            throw new InvalidOperationException(Loc.T($"A pasta {path} pertence a outra conta (não é Administradores nem SYSTEM); atualização recusada. Apague a pasta e tente de novo.", $"The folder {path} belongs to another account (not Administrators or SYSTEM); update refused. Delete the folder and try again."));
        if (applyAcl)
        {
            try { new DirectoryInfo(path).SetAccessControl(sec); }
            catch (Exception ex) { throw new InvalidOperationException(Loc.T("Não foi possível proteger a pasta de atualização: ", "Could not protect the update folder: ") + ex.Message, ex); }
        }
    }

    /// <summary>Prepara a raiz de downloads. Com <paramref name="secure"/>, valida a pasta pai e protege a raiz com ACL restrita; sempre recusa raiz (ou componente dela) que seja ponto de reanálise.</summary>
    /// <param name="root">Raiz dos downloads.</param>
    /// <param name="secure">true para aplicar a proteção de ACL (processo elevado e raiz padrão).</param>
    public static void PrepareRoot(string root, bool secure)
    {
        if (secure)
        {
            var parent = System.IO.Path.GetDirectoryName(root);
            if (!string.IsNullOrEmpty(parent)) EnsureTrustedDir(parent, applyAcl: false);
            EnsureTrustedDir(root, applyAcl: true);
        }
        else Directory.CreateDirectory(root);
        if (HasReparseInChain(root))
            throw new InvalidOperationException(Loc.T($"A pasta de atualização ({root}) passa por um link; atualização recusada.", $"The update folder ({root}) goes through a link; update refused."));
    }

    /// <summary>Apaga as subpastas de versão da raiz (só as de nome "N.N[.N[.N]]") para as quais <paramref name="shouldDelete"/> devolve true. Recusa raiz que passe por link e nunca segue links ao apagar. Nunca lança exceção.</summary>
    /// <param name="root">Raiz dos downloads.</param>
    /// <param name="shouldDelete">Decide, pelo nome da pasta, se ela deve ser apagada.</param>
    public static void CleanRoot(string root, Func<string, bool> shouldDelete)
    {
        try
        {
            if (!Directory.Exists(root) || HasReparseInChain(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                try
                {
                    var name = System.IO.Path.GetFileName(d);
                    if (!VersionDir.IsMatch(name) || !shouldDelete(name)) continue;
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    DiskCleanup.DeleteTreeNoFollow(d);
                }
                catch (Exception) { /* em uso: segue */ }
            }
        }
        catch (Exception) { /* limpeza é um extra */ }
    }

    // ------------------------------------------------------------------ download

    /// <summary>Baixa a atualização do tipo informado e confere o SHA-256. Usa a sobrecarga com manipulador HTTP e pasta alternativos, sem alterá-los.</summary>
    /// <param name="info">Versão a baixar (com a lista de arquivos).</param>
    /// <param name="kind">Tipo de instalação (define o arquivo).</param>
    /// <param name="progress">Progresso de 0 a 1.</param>
    /// <param name="ct">Cancelamento.</param>
    public static Task<DownloadedUpdate> DownloadAsync(UpdateInfo info, InstallKind kind, IProgress<double>? progress, CancellationToken ct)
        => DownloadAsync(info, kind, progress, null, null, ct);

    /// <summary>
    /// Baixa o arquivo da versão para <c>&lt;raiz&gt;\&lt;versão&gt;\</c> (apagando downloads antigos sem seguir links), exige o arquivo de nome exato e o SHA256SUMS.txt do MESMO release,
    /// respeita o limite de 400 MB e o tamanho informado, cancela se ficar <see cref="StallTimeout"/> sem receber dados e só devolve se o SHA-256 calculado bater com o do SHA256SUMS.txt.
    /// O SHA-256 é calculado sobre um handle aberto com FileShare.Read (ninguém grava nem apaga o arquivo depois) que fica aberto no <see cref="DownloadedUpdate"/> até ele ser descartado.
    /// Elevado e com a raiz padrão, a pasta fica em %ProgramData%\WinDiagMaint\update com ACL restrita. Em qualquer falha apaga o arquivo e lança <see cref="InvalidOperationException"/> com mensagem clara.
    /// </summary>
    /// <param name="info">Versão a baixar.</param>
    /// <param name="kind">Tipo de instalação.</param>
    /// <param name="progress">Progresso de 0 a 1.</param>
    /// <param name="handler">Manipulador HTTP alternativo (usado em testes; null = padrão, que segue o redirecionamento do GitHub).</param>
    /// <param name="root">Pasta raiz alternativa (testes; null = <see cref="UpdateRoot"/>).</param>
    /// <param name="ct">Cancelamento.</param>
    public static async Task<DownloadedUpdate> DownloadAsync(UpdateInfo info, InstallKind kind, IProgress<double>? progress, HttpMessageHandler? handler, string? root, CancellationToken ct)
    {
        string ver = info.Version ?? "";
        if (!VersionDir.IsMatch(ver))
            throw new InvalidOperationException(Loc.T("Versão da atualização em formato inesperado.", "Update version in an unexpected format."));
        string name = AssetNameFor(kind, ver);
        var asset = info.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(Loc.T($"A versão {ver} não traz o arquivo {name}.", $"Version {ver} does not include the file {name}."));
        var sums = info.Assets.FirstOrDefault(a => string.Equals(a.Name, SumsName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(Loc.T($"A versão {ver} não traz o {SumsName}; sem ele não dá para verificar o arquivo, então nada foi baixado.", $"Version {ver} does not include {SumsName}; without it the file cannot be verified, so nothing was downloaded."));
        if (!IsAllowedDownloadUrl(asset.DownloadUrl) || !IsAllowedDownloadUrl(sums.DownloadUrl))
            throw new InvalidOperationException(Loc.T("Endereço de download fora do repositório oficial do WinDiag; recusado.", "Download address outside the official WinDiag repository; refused."));
        if (!string.Equals(ReleaseDir(asset.DownloadUrl), ReleaseDir(sums.DownloadUrl), StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T($"O {SumsName} não é do mesmo release do arquivo; recusado.", $"{SumsName} is not from the same release as the file; refused."));
        if (asset.Size > MaxBytes)
            throw new InvalidOperationException(Loc.T("O arquivo informado passa do limite de 400 MB; recusado.", "The reported file is over the 400 MB limit; refused."));

        string baseDir = root ?? UpdateRoot;
        string dir = System.IO.Path.Combine(baseDir, ver);
        try
        {
            PrepareRoot(baseDir, secure: root is null && ToolRunner.IsAdministrator());
            CleanRoot(baseDir, _ => true);
            Directory.CreateDirectory(dir);
            if (HasReparseInChain(dir)) throw new InvalidOperationException(Loc.T("A pasta de download passa por um link; recusada.", "The download folder goes through a link; refused."));
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException(Loc.T("Não foi possível preparar a pasta de download: ", "Could not prepare the download folder: ") + ex.Message, ex); }

        string path = System.IO.Path.Combine(dir, name);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan; // o limite é por inatividade (StallTimeout), não pelo total
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinDiag");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        FileStream? held = null;
        try
        {
            try
            {
                stall.CancelAfter(StallTimeout);
                string sumsText;
                using (var rs = await http.GetAsync(sums.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false))
                {
                    rs.EnsureSuccessStatusCode();
                    if (rs.Content.Headers.ContentLength is > 1_000_000) throw new InvalidOperationException(Loc.T($"{SumsName} grande demais; recusado.", $"{SumsName} too large; refused."));
                    sumsText = await rs.Content.ReadAsStringAsync(stall.Token).ConfigureAwait(false);
                }
                if (!ParseSums(sumsText).TryGetValue(name, out var expected))
                    throw new InvalidOperationException(Loc.T($"O {SumsName} não lista {name}; o arquivo não pode ser verificado.", $"{SumsName} does not list {name}; the file cannot be verified."));

                long received = 0;
                stall.CancelAfter(StallTimeout);
                using (var resp = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? asset.Size;
                    if (total > MaxBytes) throw new InvalidOperationException(Loc.T("O arquivo passa do limite de 400 MB; recusado.", "The file is over the 400 MB limit; refused."));
                    await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                    await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
                    var buf = new byte[81920];
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout); // renovado a cada bloco: só dispara se parar de chegar dado
                        int n = await src.ReadAsync(buf.AsMemory(0, buf.Length), stall.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        received += n;
                        if (received > MaxBytes) throw new InvalidOperationException(Loc.T("O arquivo passou do limite de 400 MB; interrompido.", "The file went over the 400 MB limit; stopped."));
                        await dst.WriteAsync(buf.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                        if (total > 0) progress?.Report(Math.Min(1.0, (double)received / total));
                    }
                }
                if (asset.Size > 0 && received != asset.Size)
                    throw new InvalidOperationException(Loc.T($"Tamanho do arquivo baixado ({received} bytes) diferente do informado ({asset.Size}); o download foi descartado.", $"Downloaded size ({received} bytes) differs from the reported size ({asset.Size}); the download was discarded."));

                // O hash é calculado sobre o handle que FICA aberto (sem Write/Delete para os outros): o que foi verificado é exatamente o que será executado.
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (held.Length != received) throw new InvalidOperationException(Loc.T("O arquivo mudou depois de baixado; descartado.", "The file changed after download; discarded."));
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(held, CancellationToken.None).ConfigureAwait(false)).ToLowerInvariant();
                held.Position = 0;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Loc.T("O SHA-256 do arquivo não confere com o do SHA256SUMS.txt; o arquivo foi apagado e a atualização não será aplicada.", "The file's SHA-256 does not match the one in SHA256SUMS.txt; the file was deleted and the update will not be applied."));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(Loc.T($"O download travou: {(int)StallTimeout.TotalSeconds} s sem receber dados; foi cancelado.", $"The download stalled: {(int)StallTimeout.TotalSeconds} s without receiving data; it was cancelled."));
            }
            progress?.Report(1.0);
            return new DownloadedUpdate(path, ver, kind) { Handle = held };
        }
        catch
        {
            held?.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* melhor esforço */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ aplicar

    /// <summary>Aplica a atualização baixada e, depois de iniciar o processo, fecha o handle verificado (<see cref="DownloadedUpdate.Dispose"/>). Installer: inicia o instalador (sem shell; ele reabre o app). Portable e FrameworkDependent: troca o executável em uso copiando a partir do handle verificado e inicia o novo com "--updated". Não encerra o processo: o App chama e depois fecha.</summary>
    /// <param name="u">Atualização baixada e verificada.</param>
    public static OpResult Apply(DownloadedUpdate u)
    {
        var r = new OpResult();
        try
        {
            if (!File.Exists(u.Path)) { r.Ok = false; r.Error = Loc.T("O arquivo baixado não foi encontrado.", "The downloaded file was not found."); return r; }
            if (u.Kind == InstallKind.Installer)
            {
                using var p = Process.Start(new ProcessStartInfo(u.Path, InstallerArgs) { UseShellExecute = false });
                r.Messages.Add(Loc.T("Instalador iniciado; o WinDiag será reaberto ao terminar.", "Installer started; WinDiag will reopen when it finishes."));
                return r;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { r.Ok = false; r.Error = Loc.T("Não foi possível descobrir o executável em uso.", "Could not determine the running executable."); return r; }
            return SwapExecutable(exe, u.Path, launch: true, verified: u.Handle);
        }
        catch (Exception ex)
        {
            r.Ok = false; r.Error = ex.Message;
            return r;
        }
        finally { u.Dispose(); }
    }

    /// <summary>
    /// Troca o executável de forma atômica: prepara o novo em "&lt;exe&gt;.new" na mesma pasta (copiando de <paramref name="verified"/> se informado; senão movendo <paramref name="newFile"/>),
    /// usa File.Replace(novo, exe, "&lt;exe&gt;.old") e, se o Windows recusar, renomeia o atual para ".old" e põe o novo no lugar. Depois, se <paramref name="launch"/>, inicia o novo com "--updated".
    /// Se qualquer passo falhar, desfaz tudo e devolve o erro.
    /// </summary>
    /// <param name="currentExe">Executável em uso (pode ser renomeado enquanto roda).</param>
    /// <param name="newFile">Arquivo novo já verificado.</param>
    /// <param name="launch">true para iniciar o novo; false (testes) só troca.</param>
    /// <param name="verified">Handle verificado de onde copiar o novo (null = usa <paramref name="newFile"/>).</param>
    public static OpResult SwapExecutable(string currentExe, string newFile, bool launch, Stream? verified = null)
    {
        var r = new OpResult();
        string old = currentExe + ".old", stage = currentExe + ".new";
        bool renamed = false, replaced = false;
        try
        {
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(stage)) File.Delete(stage);
            if (verified is not null)
            {
                verified.Position = 0;
                using var o = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                verified.CopyTo(o);
            }
            else File.Move(newFile, stage);
            try
            {
                File.Replace(stage, currentExe, old);
                replaced = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(currentExe, old);
                renamed = true;
                File.Move(stage, currentExe);
                replaced = true;
            }
            r.Messages.Add(Loc.T($"Executável trocado ({System.IO.Path.GetFileName(currentExe)}); o anterior ficou como {System.IO.Path.GetFileName(old)}.", $"Executable replaced ({System.IO.Path.GetFileName(currentExe)}); the previous one was kept as {System.IO.Path.GetFileName(old)}."));
            if (launch)
            {
                using var p = Process.Start(new ProcessStartInfo(currentExe, UpdatedArg) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(currentExe) ?? "" });
                r.Messages.Add(Loc.T("Versão nova iniciada.", "New version started."));
            }
            return r;
        }
        catch (Exception ex)
        {
            try
            {
                if (replaced) File.Move(currentExe, stage, overwrite: true);
                if (replaced || renamed) File.Move(old, currentExe, overwrite: true);
                if (!replaced && File.Exists(stage)) { if (verified is null && !File.Exists(newFile)) File.Move(stage, newFile); else File.Delete(stage); }
            }
            catch (Exception rb)
            {
                r.Ok = false;
                r.Error = Loc.T($"A troca falhou ({ex.Message}) e a restauração também ({rb.Message}). O executável anterior está em {old}.", $"The swap failed ({ex.Message}) and so did the rollback ({rb.Message}). The previous executable is at {old}.");
                return r;
            }
            r.Ok = false;
            r.Error = Loc.T($"Não foi possível trocar o executável: {ex.Message}. Nada foi alterado.", $"Could not replace the executable: {ex.Message}. Nothing was changed.");
            return r;
        }
    }

    // ------------------------------------------------------------------ limpeza

    /// <summary>Na inicialização: restaura o executável se só existir o ".old", apaga ".old" e ".new" e as pastas de update de versões já instaladas, na raiz protegida e na antiga (com as mesmas guardas). Nunca lança exceção.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            CleanupLeftovers(exe, SecureRoot, AppInfo.Version);
            CleanupLeftovers("", LegacyRoot, AppInfo.Version);
        }
        catch (Exception) { /* limpeza é um extra */ }
    }

    /// <summary>
    /// Limpeza com caminhos informados (testável). Se o executável não existe e o ".old" existe, restaura o ".old". Senão apaga o ".old" e o ".new". Na pasta de updates, apaga só subpastas de nome de versão
    /// menores ou iguais à instalada, sem seguir links e recusando raiz que passe por link. Nunca lança exceção.
    /// </summary>
    /// <param name="exePath">Executável em uso (vazio para só limpar a pasta).</param>
    /// <param name="updateRoot">Pasta raiz dos downloads.</param>
    /// <param name="currentVersion">Versão instalada.</param>
    public static void CleanupLeftovers(string exePath, string updateRoot, string currentVersion)
    {
        try
        {
            if (exePath.Length > 0)
            {
                string old = exePath + ".old";
                if (!File.Exists(exePath) && File.Exists(old)) File.Move(old, exePath);
                else
                {
                    if (File.Exists(old)) File.Delete(old);
                    if (File.Exists(exePath + ".new")) File.Delete(exePath + ".new");
                }
            }
        }
        catch (Exception) { /* em uso: tenta na próxima */ }
        CleanRoot(updateRoot, name => !UpdateCheck.IsNewer(currentVersion, name));
    }
}