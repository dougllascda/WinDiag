namespace WinDiag.Core.Model;

/// <summary>Gravidade de uma verificação. A ordem importa (Ok &lt; Info &lt; Aviso &lt; Atencao).</summary>
public enum Severity
{
    /// <summary>Verificação sem problema.</summary>
    Ok,
    /// <summary>Informação, sem ação necessária.</summary>
    Info,
    /// <summary>Ponto de atenção moderado.</summary>
    Aviso,
    /// <summary>Problema relevante.</summary>
    Atencao
}

/// <summary>
/// Resultado de uma verificação.
/// Explain = "o que significa"; Action = "o que fazer"; FixId liga a um item do catálogo de otimizações;
/// Impact (0-100) ordena os "gargalos prováveis para jogos".
/// </summary>
/// <param name="Sev">Gravidade.</param>
/// <param name="Title">Título curto.</param>
/// <param name="Evidence">O que foi medido.</param>
/// <param name="Explain">O que significa.</param>
/// <param name="Action">O que fazer.</param>
/// <param name="FixId">Id da otimização relacionada ("" se nenhuma).</param>
/// <param name="Impact">Impacto de 0 a 100 para ordenar os gargalos.</param>
public sealed record Finding(
    Severity Sev,
    string Title,
    string Evidence,
    string Explain = "",
    string Action = "",
    string FixId = "",
    int Impact = 0)
{
    /// <summary>Chave da seção que gerou (preenchida pelo ScanEngine).</summary>
    public string Section { get; set; } = "";

    /// <summary>Id estável e independente de idioma (ex.: "pcie.config_limited"). Vazio só em achados criados fora de <see cref="SectionResult.Find"/>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Explicação de 1 a 2 frases, sem jargão, para leigos.</summary>
    public string Simple { get; init; } = "";

    /// <summary>Perfil a que o achado se aplica (Both = todos). Achados só de jogos têm <see cref="Model.Scope.Gaming"/>; os do perfil Trabalho, <see cref="Model.Scope.Work"/>.</summary>
    public Scope Scope { get; init; } = Scope.Both;

    /// <summary>Nome de um BiosTopic com o passo a passo de BIOS relacionado, ou "".</summary>
    public string GuideTopic { get; init; } = "";

    /// <summary>Rótulo da gravidade no idioma atual (OK, INFO, AVISO/WARNING, ATENÇÃO/ATTENTION).</summary>
    public string SevLabel => Sev switch
    {
        Severity.Ok => "OK",
        Severity.Info => "INFO",
        Severity.Aviso => Loc.T("AVISO", "WARNING"),
        _ => Loc.T("ATENÇÃO", "ATTENTION")
    };
}
