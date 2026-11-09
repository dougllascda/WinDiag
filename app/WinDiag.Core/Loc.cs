using System.Globalization;

namespace WinDiag.Core;

/// <summary>
/// Idioma da interface e dos textos gerados (pt-BR ou inglês).
/// Os dois textos ficam lado a lado no código, em <see cref="T"/>, para que cada mensagem seja traduzida
/// junto com a lógica que a gera (incluindo interpolação de valores). O idioma é definido uma vez,
/// no início (preferência do usuário ou idioma do Windows), antes da varredura.
/// </summary>
public static class Loc
{
    /// <summary>Cultura de formatação para português do Brasil.</summary>
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    /// <summary>Cultura de formatação para inglês (EUA).</summary>
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    /// <summary>Idioma atual. <c>volatile</c> porque os coletores rodam em threads de segundo plano e leem o valor enquanto a interface pode alterá-lo. Começa no idioma do Windows.</summary>
    private static volatile bool _isPt = ResolveAuto();

    /// <summary>true quando o idioma atual é português (Brasil).</summary>
    public static bool IsPt => _isPt;

    /// <summary>Código do idioma atual: "pt-BR" ou "en".</summary>
    public static string Language => _isPt ? "pt-BR" : "en";

    /// <summary>Cultura usada para formatar números (15,87 em pt-BR; 15.87 em inglês).</summary>
    public static CultureInfo Culture => _isPt ? Pt : En;

    /// <summary>
    /// Define o idioma. Aceita "Auto" (ou vazio/null: português se o Windows estiver em português,
    /// inglês caso contrário), "pt", "pt-BR", "en" ou "en-US".
    /// </summary>
    public static void Set(string? preference)
    {
        var p = (preference ?? "").Trim();
        if (p.Length == 0 || p.Equals("Auto", StringComparison.OrdinalIgnoreCase)) _isPt = ResolveAuto();
        else _isPt = p.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Escolhe o texto do idioma atual.</summary>
    /// <param name="pt">Texto em português (Brasil).</param>
    /// <param name="en">Texto em inglês.</param>
    public static string T(string pt, string en) => _isPt ? pt : en;

    /// <summary>Decide o idioma pelo idioma de interface do Windows: português se for "pt" (qualquer região), inglês nos demais casos.</summary>
    private static bool ResolveAuto() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);
}
