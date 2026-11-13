using System.Globalization;
using System.Runtime.InteropServices;

namespace WinDiag.Core.Util;

/// <summary>Aplica na sessão atual os parâmetros do mouse gravados no registro (SystemParametersInfo documentada da Win32), para a mudança valer sem sair da conta.</summary>
public static class MouseApi
{
    /// <summary>Chave do registro com os parâmetros do mouse do usuário.</summary>
    public const string MousePath = @"HKCU:\Control Panel\Mouse";
    /// <summary>SPI_SETMOUSE: define limiar 1, limiar 2 e aceleração.</summary>
    private const uint SpiSetMouse = 0x0004;
    /// <summary>SPIF_SENDCHANGE: avisa as janelas da mudança (o registro já foi gravado por quem chama).</summary>
    private const uint SpifSendChange = 0x0002;

    /// <summary>Chama SystemParametersInfoW da user32.</summary>
    /// <param name="uiAction">Ação (SPI_*).</param>
    /// <param name="uiParam">Parâmetro inteiro da ação.</param>
    /// <param name="pvParam">Vetor com limiar 1, limiar 2 e aceleração.</param>
    /// <param name="fWinIni">Opções de gravação e difusão.</param>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    /// <summary>Lê um inteiro guardado como texto no registro do mouse; devolve o padrão do Windows se ausente ou inválido.</summary>
    /// <param name="name">Nome do valor.</param>
    /// <param name="fallback">Valor padrão do Windows.</param>
    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Reg.GetString(MousePath, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Lê MouseSpeed, MouseThreshold1 e MouseThreshold2 do registro (padrões do Windows: 1, 6 e 10 se ausentes) e os aplica agora. Devolve false se a chamada falhar. Nunca lança exceção.</summary>
    public static bool ApplyFromRegistry()
    {
        try
        {
            var p = new[] { ReadInt("MouseThreshold1", 6), ReadInt("MouseThreshold2", 10), ReadInt("MouseSpeed", 1) };
            return SystemParametersInfo(SpiSetMouse, 0, p, SpifSendChange);
        }
        catch (Exception) { return false; }
    }
}
