using Microsoft.Win32;
using WinDiag.Core.Model;

namespace WinDiag.Core.Fixes;

/// <summary>Risco de uma otimização, exibido ao usuário antes de aplicar.</summary>
public enum FixRisk
{
    /// <summary>Efeito pequeno e totalmente reversível.</summary>
    Baixo,
    /// <summary>Altera um recurso de segurança ou serviço; reversível, mas com contrapartidas.</summary>
    Medio,
    /// <summary>Pode quebrar outros recursos (ex.: virtualização).</summary>
    Alto
}

/// <summary>Valor de registro a gravar (caminho no estilo "HKLM:\...").</summary>
/// <param name="Path">Chave do registro.</param>
/// <param name="Name">Nome do valor.</param>
/// <param name="Value">Dado a gravar.</param>
/// <param name="Kind">Tipo do valor.</param>
public sealed record RegSet(string Path, string Name, object Value, RegistryValueKind Kind);

/// <summary>Valor do BCD a definir via bcdedit.</summary>
/// <param name="Name">Nome do valor (ex.: "useplatformclock").</param>
/// <param name="Value">Novo valor; null remove o valor.</param>
public sealed record BcdSet(string Name, string? Value);

/// <summary>Ajuste de plano de energia (powercfg, plano ativo, na tomada).</summary>
/// <param name="Sub">GUID do subgrupo.</param>
/// <param name="Set">GUID da configuração.</param>
/// <param name="Value">Valor a definir.</param>
public sealed record PowerSet(string Sub, string Set, int Value);

/// <summary>
/// Um item do catálogo de otimizações. Cada item descreve o que alterar (registro, serviços, BCD, energia ou ações especiais);
/// a aplicação e o desfazer ficam no <see cref="FixEngine"/>, sempre com snapshot.
/// Applicable: a otimização faz sentido e ainda não está em vigor.
/// IsActive: a otimização está em vigor (estado configurado, mesmo que falte reiniciar).
/// </summary>
public sealed class FixDefinition
{
    /// <summary>Identificador estável (ex.: "GAMEDVR"), usado nos achados e nos snapshots.</summary>
    public required string Id { get; init; }
    /// <summary>Nome da ação de ligar nos dois idiomas (português, inglês); resolvido a cada leitura em <see cref="Name"/>.</summary>
    public required (string Pt, string En) NameText { get; init; }
    /// <summary>Nome curto do estado ligado nos dois idiomas (português, inglês); veja <see cref="ShortName"/>.</summary>
    public required (string Pt, string En) ShortNameText { get; init; }
    /// <summary>Texto explicativo nos dois idiomas (português, inglês); veja <see cref="Explain"/>.</summary>
    public required (string Pt, string En) ExplainText { get; init; }

    /// <summary>Nome da ação de ligar no idioma atual (ex.: "Desativar captura do Xbox Game DVR").</summary>
    public string Name => Loc.T(NameText.Pt, NameText.En);
    /// <summary>Nome curto do estado ligado no idioma atual, para o cartão (ex.: "Game DVR desativado").</summary>
    public string ShortName => Loc.T(ShortNameText.Pt, ShortNameText.En);
    /// <summary>Texto explicativo no idioma atual: o que muda e quais as contrapartidas.</summary>
    public string Explain => Loc.T(ExplainText.Pt, ExplainText.En);
    /// <summary>Risco exibido ao usuário.</summary>
    public FixRisk Risk { get; init; } = FixRisk.Baixo;
    /// <summary>true se o efeito completo exige reiniciar o Windows.</summary>
    public bool Reboot { get; init; }
    /// <summary>false se não há como desfazer (ex.: limpeza de temporários).</summary>
    public bool Reversible { get; init; } = true;
    /// <summary>Indica se a otimização faz sentido para os fatos coletados e ainda não está em vigor.</summary>
    public Func<SystemFacts, bool> Applicable { get; init; } = _ => false;
    /// <summary>Indica se a otimização já está em vigor (estado configurado).</summary>
    public Func<SystemFacts, bool> IsActive { get; init; } = _ => false;

    /// <summary>Valores de registro a gravar.</summary>
    public List<RegSet> Reg { get; init; } = new();
    /// <summary>Serviços a desativar (nomes de serviço).</summary>
    public List<string> Svc { get; init; } = new();
    /// <summary>Valores do BCD a alterar.</summary>
    public List<BcdSet> Bcd { get; init; } = new();
    /// <summary>Ajustes do plano de energia.</summary>
    public List<PowerSet> Power { get; init; } = new();
    /// <summary>Ação especial: ajustar SwapEffectUpgradeEnable preservando as demais opções do DirectX.</summary>
    public bool DxString { get; init; }
    /// <summary>Ação especial: alterar a compressão de memória (MMAgent).</summary>
    public bool Mmc { get; init; }
    /// <summary>Com <see cref="Mmc"/>: true liga a compressão, false desliga.</summary>
    public bool MmcOn { get; init; }
    /// <summary>Ação especial: desativar hibernação e Inicialização Rápida.</summary>
    public bool Hib { get; init; }
    /// <summary>Ação especial: limpar arquivos temporários.</summary>
    public bool Temp { get; init; }
    /// <summary>Ação especial: desativar itens de inicialização escolhidos pelo usuário.</summary>
    public bool StartupApps { get; init; }

    /// <summary>Rótulo do risco no idioma atual (Baixo/Médio/Alto ou Low/Medium/High).</summary>
    public string RiskLabel => Risk switch
    {
        FixRisk.Alto => Loc.T("Alto", "High"),
        FixRisk.Medio => Loc.T("Médio", "Medium"),
        _ => Loc.T("Baixo", "Low"),
    };
}
