using WinDiag.Core.Model;

namespace WinDiag.Core.Rules;

/// <summary>
/// Mapa central de Ids de achados que só interessam a quem joga. O <see cref="SectionResult.Find"/> consulta este mapa quando
/// o coletor não informa o escopo, de modo que a classificação fica em um só lugar (e os Ids continuam estáveis).
/// Critério: o achado só faz sentido para desempenho em jogos (link PCIe da GPU, equilíbrio CPU/GPU, taxas dos monitores,
/// Steam em HDD, Game DVR, HAGS, MPO, otimizações em janela, Modo de Jogo, hypervisor/HVCI e apps competindo com o jogo).
/// </summary>
public static class ScopeMap
{
    /// <summary>Prefixos de Id de achados exclusivos de jogos. Um Id que comece por um deles recebe <see cref="Scope.Gaming"/>.</summary>
    private static readonly string[] GamingPrefixes =
    {
        "pcie.",                    // link e cadeia PCIe da GPU
        "analise.balance",          // equilíbrio CPU/GPU (analise.balance e analise.balance_cpu)
        "display.mixed_rates",      // monitores com taxas diferentes
        "display.same_rate",
        "storage.steam_hdd",        // biblioteca Steam em HDD (sufixo = letra da unidade)
        "gpu.gamedvr",
        "gpu.hags",
        "gpu.mpo",
        "gpu.windowed_opt",
        "gpu.game_mode",
        "seguranca.hypervisor",     // hypervisor e hypervisor pendente: custo para jogos
        "seguranca.hvci",
        "processos.background_apps", // apps em segundo plano competindo com o jogo
        "gpu.rebar_on",             // Resizable BAR (ganho só em jogos)
        "gpu.rebar_off",
        "display.below_max_rate",   // monitor abaixo da taxa máxima
        "display.low_resolution",   // resolução abaixo da nativa
        "sistema.hpet",             // relógio do sistema forçado
        "sistema.tick",             // ajustes de tick
        "sistema.aspm",             // economia de energia do link PCIe
        "sistema.usb_suspend",      // suspensão seletiva USB (periféricos de jogo)
        "processos.vpn_adapter",    // adaptador de VPN ativo (latência em jogos online)
        "memoria.below_rated"       // memória abaixo da velocidade nominal (XMP/EXPO)
        // Alertas "sensors.*" (temperaturas, ventoinhas, VRM) ficam em Both: temperatura importa nos dois perfis.
    };

    /// <summary>Escopo de um achado pelo Id estável: <see cref="Scope.Gaming"/> se o Id está no mapa de jogos; senão <see cref="Scope.Both"/>.</summary>
    /// <param name="id">Id do achado (ex.: "gpu.hags").</param>
    public static Scope For(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Scope.Both;
        foreach (var p in GamingPrefixes)
            if (id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return Scope.Gaming;
        return Scope.Both;
    }
}
