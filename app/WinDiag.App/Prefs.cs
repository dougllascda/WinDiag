using WinDiag.Core.Settings;

namespace WinDiag.App;

/// <summary>
/// Acesso serializado às preferências do usuário (%AppData%\WinDiag\settings.json, via <see cref="UserPrefs"/>).
/// Cada alteração relê o arquivo, muda só o campo desejado e grava, para não sobrescrever campos alheios.
/// </summary>
internal static class Prefs
{
    /// <summary>Trava que serializa leitura-alteração-gravação, evitando que duas threads percam alterações uma da outra.</summary>
    private static readonly object Gate = new();

    /// <summary>Lê as preferências atuais; em falha devolve os padrões.</summary>
    public static UserPrefs Read()
    {
        lock (Gate)
        {
            try { return UserPrefs.Load(); }
            catch { return new UserPrefs(); }
        }
    }

    /// <summary>Disparado (em qualquer thread) quando uma gravação foi evitada porque o arquivo de preferências não pôde ser lido.</summary>
    public static event Action? SaveBlocked;

    /// <summary>
    /// Relê o arquivo, aplica a alteração e grava. Se a leitura caiu no padrão (<see cref="UserPrefs.IsFallback"/>),
    /// não grava, para não apagar dados existentes, e dispara <see cref="SaveBlocked"/>.
    /// Falhas de leitura ou gravação valem só para a sessão.
    /// </summary>
    public static void Update(Action<UserPrefs> change)
    {
        bool blocked = false;
        lock (Gate)
        {
            try
            {
                var p = UserPrefs.Load();
                if (p.IsFallback) blocked = true;
                else
                {
                    change(p);
                    p.Save();
                }
            }
            catch { blocked = true; /* sem permissão de escrita: a escolha vale só nesta sessão */ }
        }
        if (blocked)
        {
            try { SaveBlocked?.Invoke(); } catch { /* aviso é opcional */ }
        }
    }
}
