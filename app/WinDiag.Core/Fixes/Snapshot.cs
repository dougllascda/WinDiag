using WinDiag.Core.Util;

namespace WinDiag.Core.Fixes;

/// <summary>
/// Snapshot em %ProgramData%\WinDiagMaint\snapshots\snapshot_yyyyMMdd_HHmmss.json.
/// Formato idêntico ao da v7 (Save-Snapshot), com o campo opcional Action ("Aplicar"/"Desfazer").
/// Snapshots antigos sem Action são tratados como "Aplicar".
/// </summary>
public sealed class Snapshot
{
    /// <summary>Data/hora de criação (ISO 8601 local, ex.: 2026-11-08T08:17:00).</summary>
    public string Created { get; set; } = "";
    /// <summary>Versão do WinDiag que gravou o snapshot.</summary>
    public string Version { get; set; } = "";
    /// <summary>Itens aplicados ou desfeitos neste snapshot.</summary>
    public List<SnapshotFix> Fixes { get; set; } = new();
}

/// <summary>Um item dentro de um snapshot, com o estado anterior à alteração.</summary>
public sealed class SnapshotFix
{
    /// <summary>Id do item do catálogo.</summary>
    public string Id { get; set; } = "";
    /// <summary>Nome do item no momento da gravação.</summary>
    public string Name { get; set; } = "";
    /// <summary>Se o item pode ser desfeito.</summary>
    public bool Reversible { get; set; }
    /// <summary>"Aplicar" ou "Desfazer" (vazio em snapshots antigos, tratado como Aplicar).</summary>
    public string? Action { get; set; }
    /// <summary>Estado anterior à alteração.</summary>
    public FixState State { get; set; } = new();
    /// <summary>Estado resultante capturado logo depois de aplicar (opcional; ausente em snapshots antigos). Serve de prova de que o ajuste ainda é o do WinDiag.</summary>
    public FixState? Applied { get; set; }

    /// <summary>true se o registro é de uma aplicação (e não de um desfazer).</summary>
    public bool IsApply => string.IsNullOrEmpty(Action) || Action == SnapshotActions.Apply;
}

/// <summary>Valores do campo <see cref="SnapshotFix.Action"/>.</summary>
public static class SnapshotActions
{
    /// <summary>Item aplicado.</summary>
    public const string Apply = "Aplicar";
    /// <summary>Item desfeito.</summary>
    public const string Undo = "Desfazer";
}

/// <summary>Estado "antes" de um item: tudo o que o desfazer precisa restaurar.</summary>
public sealed class FixState
{
    /// <summary>Valores de registro anteriores.</summary>
    public List<RegState> Reg { get; set; } = new();
    /// <summary>Estado anterior dos serviços.</summary>
    public List<SvcState> Svc { get; set; } = new();
    /// <summary>Compressão de memória anterior (null = não capturada).</summary>
    public bool? Mmc { get; set; }
    /// <summary>Valores do BCD anteriores.</summary>
    public List<BcdState> Bcd { get; set; } = new();
    /// <summary>HibernateEnabled anterior.</summary>
    public RegState? Hib { get; set; }
    /// <summary>Valores de energia anteriores.</summary>
    public List<PowerState> Power { get; set; } = new();
}

/// <summary>Estado anterior de um serviço do Windows.</summary>
public sealed class SvcState
{
    /// <summary>Nome do serviço.</summary>
    public string Name { get; set; } = "";
    /// <summary>Se estava em execução.</summary>
    public bool Running { get; set; }
    /// <summary>Valor "Start" do registro.</summary>
    public RegState Start { get; set; } = new();
    /// <summary>Valor "DelayedAutostart" do registro.</summary>
    public RegState Delayed { get; set; } = new();
}

/// <summary>Estado anterior de um valor do BCD.</summary>
public sealed class BcdState
{
    /// <summary>Nome do valor.</summary>
    public string Name { get; set; } = "";
    /// <summary>Valor anterior; null se não estava definido.</summary>
    public string? Old { get; set; }
}

/// <summary>Estado anterior de um ajuste do plano de energia.</summary>
public sealed class PowerState
{
    /// <summary>GUID do subgrupo.</summary>
    public string Sub { get; set; } = "";
    /// <summary>GUID da configuração.</summary>
    public string Set { get; set; } = "";
    /// <summary>Valor anterior em CA; null se não lido.</summary>
    public long? Old { get; set; }
    /// <summary>GUID do plano de energia ativo quando o valor foi lido (opcional; snapshots antigos usam SCHEME_CURRENT).</summary>
    public string? Scheme { get; set; }
}

/// <summary>Um snapshot lido do disco.</summary>
/// <param name="Path">Caminho do arquivo.</param>
/// <param name="Created">Data de criação (do conteúdo ou do arquivo).</param>
/// <param name="Data">Conteúdo.</param>
public sealed record SnapshotFile(string Path, DateTime Created, Snapshot Data);
