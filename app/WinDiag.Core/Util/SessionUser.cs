using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinDiag.Core.Util;

/// <summary>
/// Usuário da sessão interativa. Quando um usuário padrão digita a senha de OUTRO administrador na elevação (UAC), o WinDiag roda com a conta do administrador:
/// HKCU, %LOCALAPPDATA% e %TEMP% passam a ser os do administrador, não os de quem está usando o PC. Esta classe detecta o caso; em dúvida, diz que não.
/// </summary>
public static class SessionUser
{
    /// <summary>Classe de informação WTSUserName.</summary>
    private const int WtsUserName = 5;
    /// <summary>Classe de informação WTSDomainName.</summary>
    private const int WtsDomainName = 7;

    /// <summary>Consulta uma informação da sessão (wtsapi32). Devolve um buffer a liberar com <see cref="WTSFreeMemory"/>.</summary>
    /// <param name="hServer">Servidor (zero = local).</param>
    /// <param name="sessionId">Número da sessão.</param>
    /// <param name="infoClass">Classe de informação.</param>
    /// <param name="buffer">Texto devolvido (Unicode).</param>
    /// <param name="bytes">Tamanho do buffer.</param>
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    /// <summary>Libera um buffer devolvido pelo wtsapi32.</summary>
    /// <param name="pointer">Buffer.</param>
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    /// <summary>Lê um texto de informação da sessão atual; null em falha.</summary>
    /// <param name="infoClass">Classe de informação.</param>
    private static string? Query(int infoClass)
    {
        try
        {
            int sid = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            if (!WTSQuerySessionInformation(IntPtr.Zero, sid, infoClass, out var buf, out _)) return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { WTSFreeMemory(buf); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Nome do usuário da sessão interativa no formato DOMÍNIO\usuário; null se não foi possível ler.</summary>
    public static string? SessionUserName
    {
        get
        {
            var user = Query(WtsUserName);
            if (string.IsNullOrWhiteSpace(user)) return null;
            var dom = Query(WtsDomainName);
            return string.IsNullOrWhiteSpace(dom) ? user : dom + "\\" + user;
        }
    }

    /// <summary>Decide a partir dos SIDs: true só se os dois são conhecidos e diferentes. Em dúvida (algum desconhecido), false.</summary>
    /// <param name="processSid">SID do usuário do processo.</param>
    /// <param name="sessionSid">SID do usuário da sessão interativa.</param>
    public static bool IsOtherUser(string? processSid, string? sessionSid) =>
        !string.IsNullOrWhiteSpace(processSid) && !string.IsNullOrWhiteSpace(sessionSid)
        && !string.Equals(processSid, sessionSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>true se o processo roda com uma conta diferente da do usuário da sessão interativa (elevação com outra conta de administrador). Em dúvida, false. Nunca lança exceção.</summary>
    public static bool ElevatedAsOtherUser
    {
        get
        {
            try
            {
                var name = SessionUserName;
                if (name is null) return false;
                string? sessionSid = new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value;
                using var id = WindowsIdentity.GetCurrent();
                return IsOtherUser(id.User?.Value, sessionSid);
            }
            catch (Exception) { return false; }
        }
    }
}