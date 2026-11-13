namespace WinDiag.Core.Util;

/// <summary>Abertura de endereços e arquivos pelo shell do usuário.</summary>
public static class Shell
{
    /// <summary>
    /// Abre um endereço (http, https, ms-settings:) ou arquivo existente pelo explorer.exe, que repassa o pedido ao shell já em execução na sessão do usuário:
    /// o programa aberto (navegador, leitor de HTML) não herda a elevação do WinDiag. Lança <see cref="ArgumentException"/> se o destino for vazio, tiver aspas
    /// ou não for um endereço permitido nem um arquivo existente.
    /// </summary>
    /// <param name="urlOrFile">Endereço ou caminho do arquivo.</param>
    public static void OpenUnelevated(string urlOrFile)
    {
        if (!IsOpenable(urlOrFile)) throw new ArgumentException(Loc.T("Destino não permitido para abrir.", "Target not allowed to open."), nameof(urlOrFile));
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + urlOrFile + "\"")
        {
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi);
    }

    /// <summary>true se o destino pode ser aberto: não vazio, sem aspas nem caracteres de controle, e é http/https/ms-settings ou um arquivo existente.</summary>
    /// <param name="target">Endereço ou caminho.</param>
    public static bool IsOpenable(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains('"') || target.Any(char.IsControl)) return false;
        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return true;
        try { return Path.IsPathFullyQualified(target) && File.Exists(target); } catch (Exception) { return false; }
    }
}