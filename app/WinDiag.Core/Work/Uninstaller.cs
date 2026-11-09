using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;

namespace WinDiag.Core.Work;

/// <summary>
/// Abre o desinstalador OFICIAL de um programa, sempre de forma interativa (o usuário vê e confirma cada passo).
/// Usa o <c>UninstallString</c> do registro (nunca o <c>QuietUninstallString</c>), descarta as opções silenciosas conhecidas (/S, /SILENT, /VERYSILENT, /quiet, /qn, /passive etc.),
/// converte <c>MsiExec /I{GUID}</c> em <c>/X{GUID}</c> e, para pacotes Appx/MSIX, apenas abre Configurações &gt; Aplicativos. Só executa um <c>.exe</c> com caminho absoluto existente
/// (ou o msiexec do sistema); scripts, atalhos, rundll32, interpretadores e caminhos de rede (UNC) são recusados. Entradas do registro do usuário (HKCU) não são lançadas:
/// o WinDiag roda elevado e o desinstalador herdaria a elevação; a pessoa é orientada a usar Configurações &gt; Aplicativos. O WinDiag não remove nada sozinho.
/// </summary>
public static class Uninstaller
{
    /// <summary>Reconhece um GUID de produto MSI entre chaves.</summary>
    private static readonly Regex MsiGuid = new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);
    /// <summary>Reconhece o executável do Windows Installer (msiexec), com ou sem caminho e extensão.</summary>
    private static readonly Regex MsiExe = new(@"^(?:.*[\\/])?msiexec(?:\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    /// <summary>Opções que fariam o desinstalador rodar sem interface; são descartadas para o usuário ver e confirmar cada passo.</summary>
    private static readonly HashSet<string> SilentFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "/S", "/SILENT", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/quiet", "/qn", "/qb-", "/passive", "--silent", "--force-uninstall", "-silent",
    };
    /// <summary>Executáveis recusados mesmo sendo .exe: interpretadores de script e carregadores que executariam código arbitrário.</summary>
    private static readonly HashSet<string> BlockedExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "regsvr32.exe", "explorer.exe",
    };

    /// <summary>Endereço que abre Configurações &gt; Aplicativos instalados (usado para pacotes Appx).</summary>
    public const string AppsSettingsUri = "ms-settings:appsfeatures";

    /// <summary>
    /// Separa o <c>UninstallString</c> em programa e argumentos. Aceita caminho entre aspas (com ou sem argumentos), caminho sem aspas (inclusive com espaços: testa prefixos
    /// crescentes terminados em .exe até achar um arquivo existente) e MsiExec: nesse caso devolve <c>msiexec.exe</c> com <c>/X{GUID}</c> apenas (o modo interativo).
    /// As opções silenciosas conhecidas são descartadas dos argumentos. Devolve null se o texto estiver vazio, se não for um .exe de caminho absoluto existente,
    /// se for caminho de rede (UNC), script, atalho, rundll32 ou interpretador, ou se for um MsiExec sem GUID.
    /// </summary>
    /// <param name="command">Texto do UninstallString.</param>
    public static (string FileName, string Arguments)? Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        string file, args;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) { file = text[1..]; args = ""; }
            else { file = text[1..end]; args = text[(end + 1)..].Trim(); }
        }
        else
        {
            int sp = text.AsSpan().IndexOfAny(' ', '\t');
            var first = sp < 0 ? text : text[..sp];
            if (MsiExe.IsMatch(first)) { file = first; args = sp < 0 ? "" : text[(sp + 1)..].Trim(); }
            else
            {
                string? found = null;
                int from = 0;
                while (found is null)
                {
                    int exe = text.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
                    if (exe < 0) break;
                    int cut = exe + 4;
                    from = cut;
                    if (cut < text.Length && !char.IsWhiteSpace(text[cut])) continue;
                    var cand = text[..cut];
                    if (Path.IsPathFullyQualified(cand) && File.Exists(cand)) found = cand;
                }
                if (found is null) return null;
                file = found;
                args = text[found.Length..].Trim();
            }
        }
        file = file.Trim();
        if (file.Length == 0) return null;
        if (MsiExe.IsMatch(file))
        {
            var g = MsiGuid.Match(args);
            if (!g.Success) return null;
            return ("msiexec.exe", "/X" + g.Value);
        }
        if (file.StartsWith(@"\\", StringComparison.Ordinal) || file.StartsWith("//", StringComparison.Ordinal)) return null;
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathFullyQualified(file)) return null;
        if (BlockedExe.Contains(Path.GetFileName(file))) return null;
        if (!File.Exists(file)) return null;
        return (file, StripSilentFlags(args));
    }

    /// <summary>Remove os argumentos silenciosos conhecidos (<see cref="SilentFlags"/>), preservando o restante, inclusive trechos entre aspas.</summary>
    /// <param name="args">Argumentos originais.</param>
    private static string StripSilentFlags(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "";
        var kept = new List<string>();
        foreach (Match m in Regex.Matches(args, "\"[^\"]*\"|\\S+"))
            if (!SilentFlags.Contains(m.Value)) kept.Add(m.Value);
        return string.Join(" ", kept);
    }
    /// <summary>
    /// Abre o desinstalador oficial do item (interativo). Para Appx abre Configurações &gt; Aplicativos. Nunca executa silencioso: opções silenciosas são descartadas (veja <see cref="Parse"/>). Itens vindos do registro do usuário (<see cref="BloatItem.FromUserHive"/>) não são lançados.
    /// Devolve falha com mensagem clara se não houver comando de desinstalação ou o programa não existir. Registra a abertura no changes.log.
    /// </summary>
    /// <param name="item">Programa classificado.</param>
    public static OpResult Launch(BloatItem item)
    {
        var r = new OpResult();
        try
        {
            ProcessStartInfo psi;
            if (item.FromUserHive && !item.IsAppx)
            {
                r.Ok = false;
                r.Error = Loc.T($"\"{item.Name}\" foi instalado só para o seu usuário. O WinDiag roda como administrador e não abre esse desinstalador por segurança; abra Configurações > Aplicativos > Aplicativos instalados e desinstale por lá.",
                    $"\"{item.Name}\" was installed for your user only. WinDiag runs as administrator and does not open that uninstaller for safety; open Settings > Apps > Installed apps and uninstall it there.");
                return r;
            }
            if (item.IsAppx || (string.IsNullOrWhiteSpace(item.UninstallCommand) && !string.IsNullOrEmpty(item.PackageFullName)))
            {
                psi = new ProcessStartInfo(AppsSettingsUri) { UseShellExecute = true };
                r.Messages.Add(Loc.T($"Aplicativo da Loja: abrindo Configurações > Aplicativos para remover \"{item.Name}\" por lá.", $"Store app: opening Settings > Apps so you can remove \"{item.Name}\" there."));
            }
            else
            {
                var parsed = Parse(item.UninstallCommand);
                if (parsed is null)
                {
                    r.Ok = false;
                    r.Error = Loc.T($"\"{item.Name}\" não informa um desinstalador utilizável (precisa ser um .exe com caminho completo existente). Use Configurações > Aplicativos.", $"\"{item.Name}\" does not provide a usable uninstaller (it must be an existing .exe with a full path). Use Settings > Apps.");
                    return r;
                }
                var (file, args) = parsed.Value;
                bool isMsi = file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
                if (!isMsi && !File.Exists(file))
                {
                    r.Ok = false;
                    r.Error = Loc.T($"O desinstalador de \"{item.Name}\" não foi encontrado ({file}). Use Configurações > Aplicativos.", $"The uninstaller of \"{item.Name}\" was not found ({file}). Use Settings > Apps.");
                    return r;
                }
                psi = new ProcessStartInfo(isMsi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : file, args) { UseShellExecute = true };
                r.Messages.Add(Loc.T($"Desinstalador de \"{item.Name}\" aberto. Conclua os passos na janela dele.", $"Uninstaller for \"{item.Name}\" opened. Complete the steps in its window."));
            }
            using var p = Process.Start(psi);
            try { new SnapshotStore().Log($"UNINSTALL: abriu o desinstalador oficial de \"{item.Name}\" ({psi.FileName})"); } catch (Exception) { /* registro é um extra */ }
        }
        catch (Exception ex)
        {
            r.Ok = false;
            r.Error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? Loc.T("Operação cancelada no pedido de permissão do Windows.", "Operation cancelled at the Windows permission prompt.")
                : ex.Message;
        }
        return r;
    }
}
