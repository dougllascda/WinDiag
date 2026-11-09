using WinDiag.Core.Model;

namespace WinDiag.Core.Rules;

/// <summary>Seleção dos "gargalos prováveis": achados AVISO/ATENÇÃO com impacto, maior impacto primeiro.</summary>
public static class Bottlenecks
{
    /// <summary>Até <paramref name="max"/> achados de maior impacto (desempate pela gravidade).</summary>
    public static List<Finding> Top(IEnumerable<Finding> findings, int max = 5) => Top(findings, UsageProfile.Gaming, max);

    /// <summary>Até <paramref name="max"/> achados de maior impacto que valem para o perfil (escopo Both ou do perfil).</summary>
    /// <param name="findings">Achados da varredura.</param>
    /// <param name="profile">Perfil de uso.</param>
    /// <param name="max">Quantidade máxima.</param>
    public static List<Finding> Top(IEnumerable<Finding> findings, UsageProfile profile, int max = 5) =>
        findings.Where(f => f.Sev >= Severity.Aviso && f.Impact > 0 && ScopeRules.Matches(f.Scope, profile))
                .OrderByDescending(f => f.Impact)
                .ThenByDescending(f => f.Sev)
                .Take(max)
                .ToList();

    /// <summary>Conta os achados por gravidade.</summary>
    public static (int Ok, int Info, int Aviso, int Atencao) Count(IEnumerable<Finding> findings)
    {
        var l = findings.ToList();
        return (l.Count(f => f.Sev == Severity.Ok), l.Count(f => f.Sev == Severity.Info),
                l.Count(f => f.Sev == Severity.Aviso), l.Count(f => f.Sev == Severity.Atencao));
    }
}
