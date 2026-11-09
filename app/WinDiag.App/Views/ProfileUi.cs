using WinDiag.Core;
using WinDiag.Core.Model;

namespace WinDiag.App.Views;

/// <summary>
/// Perfil de uso ativo (Jogos ou Trabalho): leitura, gravação (via <see cref="Prefs.Update"/>) e aviso às telas.
/// Quem escolhe o perfil só chama <see cref="Set"/>; a janela principal reage a <see cref="Changed"/> e refaz a leitura.
/// </summary>
internal static class ProfileUi
{
    /// <summary>Cache do perfil ativo, para não reler o arquivo a cada consulta; nulo até a primeira leitura.</summary>
    private static UsageProfile? _current;

    /// <summary>Converte o texto gravado nas preferências ("Gaming"/"Work"; qualquer outro valor = Jogos).</summary>
    public static UsageProfile Parse(string? raw) =>
        string.Equals(raw?.Trim(), "Work", StringComparison.OrdinalIgnoreCase) ? UsageProfile.Work : UsageProfile.Gaming;

    /// <summary>true se o usuário ainda não escolheu o perfil (preferência vazia).</summary>
    public static bool NeedsChoice => !_chosenThisSession && string.IsNullOrWhiteSpace(Prefs.Read().Profile);

    /// <summary>true depois que o usuário escolheu o perfil nesta sessão (mesmo que a gravação tenha falhado); evita perguntar de novo.</summary>
    private static bool _chosenThisSession;

    /// <summary>
    /// Perfil a forçar na varredura quando o escolhido na interface difere do que as preferências devolvem
    /// (arquivo ilegível ou sem permissão de escrita); null quando não é preciso forçar nada.
    /// </summary>
    public static UsageProfile? OverrideForScan()
    {
        var cur = Current;
        return cur != Parse(Prefs.Read().Profile) ? cur : null;
    }

    /// <summary>Perfil ativo (lido das preferências na primeira consulta).</summary>
    public static UsageProfile Current => _current ??= Parse(Prefs.Read().Profile);

    /// <summary>true no perfil Trabalho.</summary>
    public static bool IsWork => Current == UsageProfile.Work;

    /// <summary>Disparado depois que o perfil é gravado.</summary>
    public static event Action? Changed;

    /// <summary>Grava o perfil escolhido e avisa as telas; não faz nada se já estiver gravado igual.</summary>
    public static void Set(UsageProfile profile)
    {
        string value = profile == UsageProfile.Work ? "Work" : "Gaming";
        if (_current == profile && string.Equals(Prefs.Read().Profile, value, StringComparison.Ordinal)) return;
        _current = profile;
        _chosenThisSession = true;
        Prefs.Update(p => p.Profile = value);
        Changed?.Invoke();
    }

    /// <summary>Nome do perfil no idioma atual.</summary>
    public static string Label(UsageProfile p) => p == UsageProfile.Work ? Loc.T("Trabalho", "Work") : Loc.T("Jogos", "Gaming");

    /// <summary>Nome do escopo de uma otimização ("Jogos"/"Trabalho"); null para Both.</summary>
    public static string? ScopeLabel(Scope s) => s switch
    {
        Scope.Gaming => Loc.T("Jogos", "Gaming"),
        Scope.Work => Loc.T("Trabalho", "Work"),
        _ => null
    };

    /// <summary>true se um item de escopo <paramref name="s"/> vale para o perfil informado.</summary>
    public static bool Matches(Scope s, UsageProfile p) =>
        s == Scope.Both || (s == Scope.Gaming && p == UsageProfile.Gaming) || (s == Scope.Work && p == UsageProfile.Work);

    /// <summary>Ícone (Segoe MDL2) do perfil: controle (Jogos) ou pasta (Trabalho).</summary>
    public static string Glyph(UsageProfile p) => p == UsageProfile.Work ? "" : "";
}
