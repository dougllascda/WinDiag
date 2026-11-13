using WinDiag.Core.Util;

namespace WinDiag.Core.Tools;

/// <summary>Confirmação de que um ponto de restauração foi realmente criado (a chamada do WMI pode devolver sucesso sem criar nada, por causa do limite de um ponto a cada 24 horas).</summary>
public static class RestorePointCheck
{
    /// <summary>Janela, em minutos, em que o ponto é considerado "recém-criado".</summary>
    public const int RecentMinutes = 10;

    /// <summary>Lê a lista de pontos de restauração (WMI root\default, SystemRestore): descrição e data de criação. Lista vazia em falha.</summary>
    public static List<(string Description, DateTime? Created)> Read()
    {
        var list = new List<(string, DateTime?)>();
        var objs = Wmi.Query("SELECT Description, CreationTime FROM SystemRestore", @"root\default");
        try
        {
            foreach (var o in objs) list.Add((o.S("Description") ?? "", Fmt.ParseWmiDate(o.P("CreationTime"))));
        }
        finally { Wmi.DisposeAll(objs); }
        return list;
    }

    /// <summary>true se há um ponto com exatamente a descrição informada criado nos últimos <see cref="RecentMinutes"/> minutos.</summary>
    /// <param name="points">Pontos lidos.</param>
    /// <param name="description">Descrição esperada.</param>
    /// <param name="now">Agora (hora local).</param>
    public static bool HasRecent(IEnumerable<(string Description, DateTime? Created)> points, string description, DateTime now) =>
        points.Any(p => string.Equals(p.Description, description, StringComparison.Ordinal) && p.Created is { } c && now - c >= TimeSpan.FromMinutes(-2) && now - c <= TimeSpan.FromMinutes(RecentMinutes));
}