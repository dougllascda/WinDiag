using System.Globalization;

namespace WinDiag.Core;

/// <summary>
/// Formatação de valores no idioma atual (<see cref="Loc"/>): números (15,87 / 15.87), datas
/// (dd/MM/yyyy em português; yyyy-MM-dd em inglês, sem a ambiguidade mês/dia) e a convenção para valores ausentes.
/// </summary>
public static class Fmt
{
    /// <summary>Cultura de formatação do idioma atual (igual a <see cref="Loc.Culture"/>).</summary>
    public static CultureInfo Culture => Loc.Culture;

    /// <summary>Mesma coisa que <see cref="Culture"/>; nome mantido por compatibilidade com o código anterior ao suporte a idiomas.</summary>
    public static CultureInfo PtBr => Loc.Culture;

    /// <summary>Texto para valor indisponível no idioma atual ("n/d" ou "n/a").</summary>
    public static string NA => Loc.IsPt ? "n/d" : "n/a";

    private static string DateFmt => Loc.IsPt ? "dd/MM/yyyy" : "yyyy-MM-dd";

    /// <summary>Formata qualquer valor: null/vazio vira <see cref="NA"/>, booleano vira "sim"/"não" ("yes"/"no"), números e datas seguem o idioma.</summary>
    public static string Val(object? v) => v switch
    {
        null => NA,
        string s => string.IsNullOrWhiteSpace(s) ? NA : s,
        DateTime d => DateTime(d),
        double d => Num(d),
        float f => Num(f),
        decimal m => Num((double)m),
        bool b => b ? Loc.T("sim", "yes") : Loc.T("não", "no"),
        IFormattable f => f.ToString(null, Culture),
        _ => v.ToString() ?? NA
    };

    /// <summary>Data (dd/MM/yyyy ou yyyy-MM-dd) ou <see cref="NA"/>.</summary>
    public static string Date(DateTime? d) => d is null ? NA : d.Value.ToString(DateFmt, CultureInfo.InvariantCulture);
    /// <summary>Data e hora (HH:mm) ou <see cref="NA"/>.</summary>
    public static string DateTime(DateTime? d) => d is null ? NA : d.Value.ToString(DateFmt + " HH:mm", CultureInfo.InvariantCulture);
    /// <summary>Data e hora com segundos ou <see cref="NA"/>.</summary>
    public static string DateTimeSec(DateTime? d) => d is null ? NA : d.Value.ToString(DateFmt + " HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Número com até <paramref name="decimals"/> casas no separador decimal do idioma (15,87 / 15.87).</summary>
    public static string Num(double v, int decimals = 2) =>
        Math.Round(v, decimals).ToString("0." + new string('#', Math.Max(decimals, 0)), Culture);

    /// <summary>Bytes convertidos em GB (base 1024) com unidade.</summary>
    public static string GB(double bytes, int decimals = 2) => Num(bytes / 1073741824d, decimals) + " GB";
    /// <summary>Bytes convertidos em MB (base 1024) com unidade.</summary>
    public static string MB(double bytes, int decimals = 0) => Num(bytes / 1048576d, decimals) + " MB";

    /// <summary>Valor com unidade, ou <see cref="NA"/> sem unidade.</summary>
    public static string WithUnit(object? v, string unit)
    {
        var s = Val(v);
        return s == NA ? NA : s + " " + unit;
    }

    /// <summary>Converte datas WMI (yyyyMMddHHmmss.ffffff+zzz) ou DateTime; null se vazio ou inválido.</summary>
    public static DateTime? ParseWmiDate(object? v)
    {
        if (v is DateTime d) return d;
        var s = v?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return System.Management.ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
