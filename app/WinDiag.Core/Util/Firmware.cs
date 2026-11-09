using System.Runtime.InteropServices;

namespace WinDiag.Core.Util;

/// <summary>
/// Leitura somente consulta do modo de firmware (UEFI x BIOS legado) e do Secure Boot.
/// Usa a API documentada GetFirmwareType (kernel32, Windows 8+) e o valor de registro
/// HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled. Nunca lança exceção.
/// </summary>
public static class Firmware
{
    // FIRMWARE_TYPE: 0 = Unknown, 1 = Bios, 2 = Uefi, 3 = Max
    /// <summary>API documentada kernel32!GetFirmwareType (Windows 8+). Preenche o enum FIRMWARE_TYPE; devolve false em falha.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    /// <summary>true = boot UEFI; false = BIOS legado; null = não determinado.</summary>
    public static bool? IsUefi()
    {
        try
        {
            if (!GetFirmwareType(out var t)) return null;
            // 0 (desconhecido) e 3 (máximo) não são resultado válido; viram null para o chamador mostrar "n/d".
            return t switch { 2 => true, 1 => false, _ => null };
        }
        catch { return null; }
    }

    /// <summary>true/false = Secure Boot ligado/desligado; null = indisponível (BIOS legado ou chave ausente).</summary>
    public static bool? IsSecureBootEnabled()
    {
        try
        {
            // A chave só existe em UEFI; ausente = BIOS legado ou leitura negada, por isso null e não false.
            var v = Reg.GetInt(@"HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            return v is null ? null : v != 0;
        }
        catch { return null; }
    }
}
