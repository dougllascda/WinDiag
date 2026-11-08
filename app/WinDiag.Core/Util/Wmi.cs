using System.Management;

namespace WinDiag.Core.Util;

/// <summary>
/// Consultas WMI (System.Management) que nunca lançam exceção: em falha devolvem lista vazia e informam o motivo ao chamador.
/// Os objetos devolvidos são <see cref="IDisposable"/>; chamadores de rotina repetida (amostragem ao vivo) devem liberá-los com <see cref="DisposeAll"/>.
/// </summary>
public static class Wmi
{
    /// <summary>Executa uma consulta WQL e devolve todos os objetos. Não exige administrador (depende da classe consultada). Em falha devolve lista vazia e chama <paramref name="onError"/>.</summary>
    /// <param name="wql">Consulta WQL.</param>
    /// <param name="ns">Namespace WMI (padrão root\cimv2).</param>
    /// <param name="onError">Recebe a mensagem de falha, se houver.</param>
    public static List<ManagementBaseObject> Query(string wql, string ns = @"root\cimv2", Action<string>? onError = null)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, wql);
            using var results = searcher.Get();
            foreach (var o in results) list.Add(o);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"{ns}: {ex.Message.Trim()}");
        }
        return list;
    }

    /// <summary>Primeiro objeto da consulta, ou null se não houver resultado ou a consulta falhar. Os demais objetos são liberados.</summary>
    public static ManagementBaseObject? First(string wql, string ns = @"root\cimv2", Action<string>? onError = null)
    {
        var all = Query(wql, ns, onError);
        if (all.Count == 0) return null;
        for (int i = 1; i < all.Count; i++) all[i].Dispose();
        return all[0];
    }

    /// <summary>Libera todos os objetos de uma lista devolvida por <see cref="Query"/>. Nunca lança exceção.</summary>
    public static void DisposeAll(IEnumerable<ManagementBaseObject>? objects)
    {
        if (objects is null) return;
        foreach (var o in objects)
        {
            try { o.Dispose(); } catch (Exception) { /* já liberado */ }
        }
    }

    /// <summary>Lê uma propriedade sem lançar exceção.</summary>
    public static object? P(this ManagementBaseObject o, string name)
    {
        try { return o[name]; } catch (Exception) { return null; }
    }

    /// <summary>Propriedade como texto sem espaços nas pontas, ou null.</summary>
    public static string? S(this ManagementBaseObject o, string name) => o.P(name)?.ToString()?.Trim();

    /// <summary>Propriedade como inteiro de 64 bits, ou null se ausente ou não numérica.</summary>
    public static long? L(this ManagementBaseObject o, string name)
    {
        var v = o.P(name);
        if (v is null) return null;
        try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return null; }
    }

    /// <summary>Propriedade como número de ponto flutuante, ou null.</summary>
    public static double? D(this ManagementBaseObject o, string name)
    {
        var v = o.P(name);
        if (v is null) return null;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return null; }
    }

    /// <summary>Propriedade como booleano, ou null.</summary>
    public static bool? B(this ManagementBaseObject o, string name)
    {
        var v = o.P(name);
        if (v is null) return null;
        try { return Convert.ToBoolean(v); } catch (Exception) { return null; }
    }
}
