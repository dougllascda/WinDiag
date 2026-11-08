using WinDiag.Core.Model;

namespace WinDiag.Core.Util;

/// <summary>Itens de inicialização (chaves Run/Run32 e pastas Inicializar) com o estado de StartupApproved. Fonte: registro e sistema de arquivos; não exige administrador.</summary>
public static class Startup
{
    const string ApBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Primeiro byte ímpar = desabilitado (03...); par = habilitado (02...). Sem dados = habilitado.</summary>
    public static bool IsEnabled(byte[]? approved)
        => approved is null || approved.Length == 0 || (approved[0] & 1) == 0;

    static bool StateOf(string apPath, string valueName) => IsEnabled(Reg.Get(apPath, valueName) as byte[]);

    /// <summary>Lê os itens de inicialização. Locais ilegíveis são ignorados.</summary>
    public static List<StartupItem> Read()
    {
        var items = new List<StartupItem>();
        var runKeys = new (string Path, string Loc, string Ap)[]
        {
            (@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run", @"HKCU:\" + ApBase + @"\Run"),
            (@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run", @"HKLM:\" + ApBase + @"\Run"),
            (@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM Run32", @"HKLM:\" + ApBase + @"\Run32"),
        };
        foreach (var rk in runKeys)
        {
            try
            {
                using var k = Reg.Open(rk.Path);
                if (k is null) continue;
                foreach (var n in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    string cmd = "";
                    try { cmd = k.GetValue(n, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? ""; } catch { }
                    items.Add(new StartupItem { Name = n, Command = cmd, Location = rk.Loc, ApprovedPath = rk.Ap, ValueName = n, Enabled = StateOf(rk.Ap, n) });
                }
            }
            catch { }
        }
        var folders = new (Environment.SpecialFolder F, string Ap)[]
        {
            (Environment.SpecialFolder.Startup, @"HKCU:\" + ApBase + @"\StartupFolder"),
            (Environment.SpecialFolder.CommonStartup, @"HKLM:\" + ApBase + @"\StartupFolder"),
        };
        foreach (var fd in folders)
        {
            try
            {
                var dir = Environment.GetFolderPath(fd.F);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(new StartupItem
                    {
                        Name = Path.GetFileNameWithoutExtension(f.Name), Command = f.FullName, Location = "Pasta Inicializar",
                        ApprovedPath = fd.Ap, ValueName = f.Name, Enabled = StateOf(fd.Ap, f.Name)
                    });
                }
            }
            catch { }
        }
        return items;
    }
}
