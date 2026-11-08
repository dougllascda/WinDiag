using System.Reflection;

namespace WinDiag.Core;

/// <summary>
/// Identidade do aplicativo. A versão tem uma única fonte: a propriedade <c>Version</c>
/// em <c>Directory.Build.props</c>, que o MSBuild grava em todos os assemblies.
/// </summary>
public static class AppInfo
{
    /// <summary>Nome exibido do produto.</summary>
    public const string Name = "WinDiag";

    /// <summary>Endereço do repositório público (exibido em "Sobre" e no relatório).</summary>
    public const string RepoUrl = "https://github.com/dougllascda/WinDiag";

    /// <summary>Versão semântica (ex.: "1.0.0"), lida do assembly.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Nome e versão juntos (ex.: "WinDiag 1.0.0").</summary>
    public static string Title => $"{Name} {Version}";

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Remove metadados de build ("1.0.0+abc123").
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
