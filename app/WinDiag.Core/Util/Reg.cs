using Microsoft.Win32;
using System.Text.Json.Serialization;

namespace WinDiag.Core.Util;

/// <summary>
/// Estado de um valor de registro. Mesmo formato JSON do Get-RegState da v7
/// (Path no estilo "HKLM:\...", Kind = nome do RegistryValueKind), para manter os snapshots compatíveis.
/// </summary>
public sealed class RegState
{
    /// <summary>Chave, no estilo "HKLM:\...".</summary>
    public string Path { get; set; } = "";
    /// <summary>Nome do valor.</summary>
    public string Name { get; set; } = "";
    /// <summary>true se o valor existia.</summary>
    public bool Exists { get; set; }
    /// <summary>int/long para DWord/QWord, string para String/ExpandString, int[] para Binary, string[] para MultiString.</summary>
    public object? Value { get; set; }
    /// <summary>Nome do <see cref="RegistryValueKind"/>.</summary>
    public string Kind { get; set; } = "DWord";
}

/// <summary>Acesso ao registro por caminhos no estilo "HKLM:\..." . Leituras nunca lançam exceção (devolvem null); gravações exigem permissão e lançam em falha.</summary>
public static class Reg
{
    /// <summary>Abre uma chave a partir de "HKLM:\...", "HKCU:\...", "HKEY_LOCAL_MACHINE\..." etc.</summary>
    public static RegistryKey? Open(string path, bool writable = false, bool create = false)
    {
        var (root, sub) = Split(path);
        if (root is null) return null;
        try
        {
            if (create) return root.CreateSubKey(sub, writable);
            return root.OpenSubKey(sub, writable);
        }
        catch { return null; }
    }

    /// <summary>Separa o caminho em chave raiz e subcaminho. Raiz desconhecida devolve null.</summary>
    public static (RegistryKey? Root, string Sub) Split(string path)
    {
        var p = path.Replace('/', '\\');
        int i = p.IndexOf('\\');
        var head = (i < 0 ? p : p[..i]).TrimEnd(':').ToUpperInvariant();
        var sub = i < 0 ? "" : p[(i + 1)..];
        RegistryKey? root = head switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKU" or "HKEY_USERS" => Registry.Users,
            _ => null
        };
        return (root, sub);
    }

    /// <summary>Lê um valor sem expandir variáveis de ambiente; null se ausente ou ilegível.</summary>
    public static object? Get(string path, string name)
    {
        using var k = Open(path);
        try { return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { return null; }
    }

    /// <summary>Lê um valor como inteiro; null se ausente ou não numérico.</summary>
    public static int? GetInt(string path, string name)
    {
        var v = Get(path, name);
        if (v is null) return null;
        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
    }

    /// <summary>Lê um valor como texto; null se ausente.</summary>
    public static string? GetString(string path, string name) => Get(path, name)?.ToString();

    /// <summary>Captura o estado (existência, tipo e dado) de um valor, para snapshots. Não lança exceção.</summary>
    public static RegState State(string path, string name)
    {
        var st = new RegState { Path = path, Name = name };
        using var k = Open(path);
        if (k is null) return st;
        try
        {
            if (!k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return st;
            st.Exists = true;
            var kind = k.GetValueKind(name);
            st.Kind = kind.ToString();
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            st.Value = v is byte[] b ? b.Select(x => (int)x).ToArray() : v;
        }
        catch { }
        return st;
    }

    /// <summary>Grava um valor. Lança exceção em falha (o FixEngine trata).</summary>
    public static void Set(string path, string name, object value, RegistryValueKind kind)
    {
        using var k = Open(path, writable: true, create: true) ?? throw new InvalidOperationException(Loc.T("Não foi possível abrir ", "Could not open ") + path);
        k.SetValue(name, value, kind);
    }

    /// <summary>Remove um valor (sem erro se a chave ou o valor não existirem). Lança exceção se a chave existe mas não abre para escrita (sem permissão).</summary>
    public static void Delete(string path, string name)
    {
        using var k = Open(path, writable: true);
        if (k is null)
        {
            using var ro = Open(path);
            if (ro is not null) throw new UnauthorizedAccessException(Loc.T("Sem permissão para alterar ", "No permission to change ") + path);
            return; // chave inexistente: nada a remover
        }
        k.DeleteValue(name, throwOnMissingValue: false);
    }
}
