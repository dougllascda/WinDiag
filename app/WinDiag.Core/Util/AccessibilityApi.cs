using System.Globalization;
using System.Runtime.InteropServices;

namespace WinDiag.Core.Util;

/// <summary>Aplica na sessão atual as flags de Teclas de Aderência, Filtro e Alternância gravadas no registro (SystemParametersInfo documentada da Win32).</summary>
public static class AccessibilityApi
{
    /// <summary>Prefixo das chaves de acessibilidade do usuário.</summary>
    public const string KeyPrefix = @"HKCU:\Control Panel\Accessibility";
    /// <summary>Chave das Teclas de Aderência.</summary>
    public const string StickyPath = KeyPrefix + @"\StickyKeys";
    /// <summary>Chave das Teclas de Filtro.</summary>
    public const string FilterPath = KeyPrefix + @"\Keyboard Response";
    /// <summary>Chave das Teclas de Alternância.</summary>
    public const string TogglePath = KeyPrefix + @"\ToggleKeys";
    /// <summary>SPI_GETFILTERKEYS.</summary>
    private const uint SpiGetFilterKeys = 0x0032;
    /// <summary>SPI_SETFILTERKEYS.</summary>
    private const uint SpiSetFilterKeys = 0x0033;
    /// <summary>SPI_SETSTICKYKEYS.</summary>
    private const uint SpiSetStickyKeys = 0x003B;
    /// <summary>SPI_SETTOGGLEKEYS.</summary>
    private const uint SpiSetToggleKeys = 0x0035;
    /// <summary>SPIF_SENDCHANGE.</summary>
    private const uint SpifSendChange = 0x0002;

    /// <summary>Chama SystemParametersInfoW com uma estrutura de inteiros (STICKYKEYS, FILTERKEYS ou TOGGLEKEYS).</summary>
    /// <param name="uiAction">Ação (SPI_*).</param>
    /// <param name="uiParam">Tamanho da estrutura em bytes.</param>
    /// <param name="pvParam">Estrutura como vetor de inteiros (o primeiro é cbSize).</param>
    /// <param name="fWinIni">Opções de difusão.</param>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    /// <summary>Lê o valor Flags (texto) de uma chave; o padrão do Windows se ausente ou inválido.</summary>
    /// <param name="path">Chave.</param>
    /// <param name="fallback">Padrão do Windows.</param>
    private static int Flags(string path, int fallback) =>
        int.TryParse(Reg.GetString(path, "Flags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Lê as três flags do registro (padrões do Windows 510, 126 e 62 se ausentes) e as aplica agora. Devolve false se alguma chamada falhar. Nunca lança exceção.</summary>
    public static bool ApplyFromRegistry()
    {
        try
        {
            bool ok = SystemParametersInfo(SpiSetStickyKeys, 8, new[] { 8, Flags(StickyPath, 510) }, SpifSendChange);
            var filter = new int[6];
            filter[0] = 24;
            SystemParametersInfo(SpiGetFilterKeys, 24, filter, 0); // mantém os tempos atuais; só troca as flags
            filter[1] = Flags(FilterPath, 126);
            ok &= SystemParametersInfo(SpiSetFilterKeys, 24, filter, SpifSendChange);
            ok &= SystemParametersInfo(SpiSetToggleKeys, 8, new[] { 8, Flags(TogglePath, 62) }, SpifSendChange);
            return ok;
        }
        catch (Exception) { return false; }
    }
}