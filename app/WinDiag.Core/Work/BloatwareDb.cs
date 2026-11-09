using System.Text.RegularExpressions;
using WinDiag.Core.Model;

namespace WinDiag.Core.Work;

/// <summary>
/// Lista conservadora de programas instalados de fábrica, classificados em Remover, Opcional e Manter.
/// Regras: (1) só classifica o que reconhece pelo nome (e, em poucos casos, pelo fabricante); o desconhecido devolve null e nunca é sugerido;
/// (2) drivers, painéis e ferramentas de suporte/BIOS do fabricante, e utilitários de teclas Fn/OSD/ventoinha/perfis de energia (Lenovo Utility, Armoury Crate, MyASUS, HP Hotkey Support, Dell Power Manager...) são sempre "Manter" e têm prioridade sobre as demais regras;
/// (3) "Remover" é só para testes de antivírus (variantes claramente de avaliação; antivírus pago ou corporativo é sempre "Opcional", com aviso de confirmar a licença)/VPN, jogos casuais de propaganda e atalhos promocionais; como o nome não prova que o programa
/// veio de fábrica, o motivo sempre pede que o usuário confirme antes de desinstalar; (4) "Opcional" é para apps de serviços e assistentes redundantes,
/// cuja decisão é do usuário. O WinDiag não remove nada sozinho: abre o desinstalador oficial (<see cref="Uninstaller"/>).
/// </summary>
public static class BloatwareDb
{
    /// <summary>Regra de classificação: nome (e, às vezes, fabricante) que casa, veredito e motivo nos dois idiomas.</summary>
    /// <param name="Name">Padrão do nome do programa.</param>
    /// <param name="Publisher">Padrão do fabricante, ou null para ignorar.</param>
    /// <param name="Verdict">Veredito.</param>
    /// <param name="Pt">Motivo em português.</param>
    /// <param name="En">Motivo em inglês.</param>
    private sealed record Rule(Regex Name, Regex? Publisher, BloatVerdict Verdict, string Pt, string En);

    /// <summary>Opções padrão dos padrões: sem diferenciar maiúsculas, independentes da cultura e compilados (a lista é consultada para cada programa instalado).</summary>
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Cria um padrão de nome com as opções padrão.</summary>
    /// <param name="pattern">Expressão regular.</param>
    private static Regex N(string pattern) => new(pattern, Rx);

    /// <summary>Motivo padrão (pt) para ferramentas e drivers do fabricante, sempre classificados como Manter.</summary>
    private const string KeepPt = "Driver ou ferramenta de suporte do fabricante (atualiza drivers e BIOS). Mantenha.";
    /// <summary>Motivo padrão (en) para ferramentas e drivers do fabricante.</summary>
    private const string KeepEn = "Manufacturer driver or support tool (updates drivers and BIOS). Keep it.";
    /// <summary>Motivo (pt) para antivírus de avaliação; pede confirmar que não há licença paga antes de remover.</summary>
    private const string AvPt = "Antivírus de avaliação instalado de fábrica; o Microsoft Defender já protege o PC sem custo. Confirme que você não tem licença paga antes de remover.";
    /// <summary>Motivo (en) para antivírus de avaliação.</summary>
    private const string AvEn = "Factory-installed antivirus trial; Microsoft Defender already protects the PC at no cost. Make sure you do not have a paid licence before removing it.";

    /// <summary>Motivo (pt) para ferramentas do fabricante que costumam controlar teclas Fn, indicadores na tela (OSD), perfis de energia ou ventoinhas. Não afirma nada medido neste PC.</summary>
    private const string HwPt = "Ferramenta do fabricante que costuma controlar teclas Fn, indicadores na tela (OSD), perfis de energia ou ventoinhas neste tipo de notebook. Remover pode desativar essas funções; mantenha, a menos que você confirme que não as usa.";
    /// <summary>Motivo (en) para ferramentas do fabricante que costumam controlar teclas Fn, OSD, perfis de energia ou ventoinhas.</summary>
    private const string HwEn = "Manufacturer tool that usually handles Fn keys, on-screen indicators (OSD), power profiles or fans on this kind of laptop. Removing it may disable those functions; keep it unless you confirm you do not use them.";
    /// <summary>Motivo (pt) para antivírus que podem ter licença paga ou ser gerenciados pela empresa.</summary>
    private const string PaidAvPt = "Pode ser licença paga ou gerenciada pela empresa; confirme antes de remover. O Microsoft Defender protege o PC se você não precisar deste programa.";
    /// <summary>Motivo (en) para antivírus que podem ter licença paga ou ser gerenciados pela empresa.</summary>
    private const string PaidAvEn = "May be a paid or company-managed licence; confirm before removing. Microsoft Defender protects the PC if you do not need this program.";

    // Ordem importa: "Manter" primeiro, depois "Remover", depois "Opcional", depois regras por fabricante.
    /// <summary>Regras em ordem de prioridade: a primeira que casar define o veredito (Manter, Remover, Opcional e regras por fabricante).</summary>
    private static readonly Rule[] Rules =
    {
        // ---------------- Manter: ferramentas de suporte/BIOS e drivers
        new(N(@"Lenovo\s+(Utility|Hotkeys?|Hot\s*Key)|Lenovo\s+Vantage\s+Service|Armoury\s+Crate|ROG\s+Live\s+Service|\bMyASUS\b|ASUS\s+System\s+Control\s+Interface|ASUS\s+Framework|ASUS\s+Hotkey|ATK\s*(Package|Hotkey)|HP\s+(Hotkey|Smart\s+Sense|Power\s+Manager|OMEN)|Dell\s+Power\s+Manager|Dell\s+Optimizer|Alienware\s+Command\s+Center|Acer\s+(Quick\s+Access|Care\s+Center)|Predator\s*Sense|Nitro\s*Sense|MSI\s+(Center|Dragon\s+Center)|Samsung\s+(Settings|Update)"), null, BloatVerdict.Keep, HwPt, HwEn),
        new(N(@"Lenovo\s+(Commercial\s+)?Vantage|Lenovo\s+System\s+Update|Lenovo\s+Service\s+Bridge"), null, BloatVerdict.Keep, KeepPt, KeepEn),
        new(N(@"Dell\s+SupportAssist|Dell\s+Command|Dell\s+Update|Dell\s+Peripheral\s+Manager"), null, BloatVerdict.Keep, KeepPt, KeepEn),
        new(N(@"HP\s+Support\s+Assistant|HP\s+Support\s+Solutions|HP\s+System\s+Event\s+Utility"), null, BloatVerdict.Keep, KeepPt, KeepEn),
        new(N(@"\b(Intel|NVIDIA|AMD|Realtek)\b.*\b(driver|graphics|control\s+panel|command\s+center|software|audio|ethernet|wireless|wi-?fi|bluetooth|management\s+engine|chipset|card\s+reader|GeForce|Radeon|Adrenalin)\b"), null, BloatVerdict.Keep,
            "Driver ou painel do componente (vídeo, som, rede ou chipset). Mantenha.", "Component driver or control panel (video, sound, network or chipset). Keep it."),

        // ---------------- Remover: testes, jogos casuais e atalhos promocionais
        // Antivírus: pagos ou corporativos NUNCA são "Remover". Ordem: gerenciado/corporativo (Opcional), pago sem "Trial" (Opcional),
        // variante claramente de avaliação (Remover) e, por fim, qualquer outro produto dessas marcas (Opcional).
        new(N(@"^(?=.*(?:McAfee|\bNorton\b|Avast|Symantec|Trellix))(?=.*\b(?:Agent|Endpoint|Enterprise|Business|Security\s*Center|Management|ePolicy|ePO)\b)"), null, BloatVerdict.Optional, PaidAvPt, PaidAvEn),
        new(N(@"^(?=.*(?:McAfee|\bNorton\b|Avast))(?=.*(?:Norton\s*360|Total\s+(?:Security|Protection)|Internet\s+Security))(?!.*\bTrial\b)"), null, BloatVerdict.Optional, PaidAvPt, PaidAvEn),
        new(N(@"^(?=.*(?:McAfee|\bNorton\b|Avast))(?=.*(?:\bTrial\b|\bLiveSafe\b|\bEvaluation\b|Avalia[cç][aã]o))"), null, BloatVerdict.Remove, AvPt, AvEn),
        new(N(@"McAfee|\bNorton\b|Avast"), null, BloatVerdict.Optional, PaidAvPt, PaidAvEn),
        new(N(@"ExpressVPN"), null, BloatVerdict.Remove,
            "VPN de avaliação costuma vir instalada de fábrica. Se você não assina o serviço, não há motivo para mantê-la.",
            "A VPN trial often ships preinstalled. If you do not subscribe to the service, there is no reason to keep it."),
        new(N(@"WildTangent"), null, BloatVerdict.Remove,
            "Loja de jogos de propaganda instalada de fábrica.", "Promotional games store installed at the factory."),
        new(N(@"Candy\s*Crush|Bubble\s*Witch|March\s*of\s*Empires|Hidden\s*City|Farm\s*Heroes|Gardenscapes|Township|Royal\s*Revolt|Asphalt\s*\d|Disney\s*Magic\s*Kingdoms"), null, BloatVerdict.Remove,
            "Jogo casual de propaganda instalado de fábrica.", "Promotional casual game installed at the factory."),
        new(N(@"^(Booking\.com|eBay|Amazon(\.com|\s+Assistant|\s+Shopping)?)$"), null, BloatVerdict.Remove,
            "Atalho promocional de loja ou site, sem função no Windows. O desinstalador remove só o atalho ou o aplicativo.",
            "Promotional shortcut to a store or website with no role in Windows. The uninstaller removes only the shortcut or app."),
        new(N(@"CyberLink\s+(PowerDVD|YouCam|Media\s+Suite|Power2Go|PowerDirector)"), null, BloatVerdict.Remove,
            "Programa de mídia de avaliação instalado de fábrica. Se você não tem licença, ele expira e pede compra.",
            "Media program trial installed at the factory. Without a licence it expires and asks for a purchase."),
        new(N(@"Dropbox\s+(promotion|offer|promo)|Dropbox\s+\d+\s*GB"), null, BloatVerdict.Remove,
            "Oferta promocional do Dropbox instalada de fábrica. Seus arquivos não dependem dela.",
            "Dropbox promotional offer installed at the factory. Your files do not depend on it."),

        // ---------------- Opcional: apps de serviços e assistentes redundantes
        new(N(@"\bSpotify"), null, BloatVerdict.Optional,
            "Serviço de música que costuma vir instalado de fábrica. Remova se você não usa.", "Music service that often ships preinstalled. Remove it if you do not use it."),
        new(N(@"Disney\s*(\+|Plus)|^Disney\.\w+$"), null, BloatVerdict.Optional,
            "Serviço de streaming que costuma vir instalado de fábrica. Remova se você não assina.", "Streaming service that often ships preinstalled. Remove it if you do not subscribe."),
        new(N(@"\bNetflix\b"), null, BloatVerdict.Optional,
            "Serviço de streaming que costuma vir instalado ou como atalho de fábrica. Remova se você não usa.", "Streaming service that often ships preinstalled or as a shortcut. Remove it if you do not use it."),
        new(N(@"TikTok|^Instagram\w*$|^Facebook$|Prime\s*Video"), null, BloatVerdict.Optional,
            "Aplicativo de rede social ou streaming que costuma vir instalado de fábrica. Remova se você não usa.", "Social network or streaming app that often ships preinstalled. Remove it if you do not use it."),
        new(N(@"\bEvernote\b"), null, BloatVerdict.Optional,
            "Aplicativo de notas que às vezes vem instalado de fábrica. Mantenha só se você usa; suas notas ficam na conta online.", "Notes app that sometimes ships preinstalled. Keep it only if you use it; your notes live in the online account."),
        new(N(@"HP\s+JumpStart|HP\s+Documentation|HP\s+Privacy\s+Settings|HP\s+Welcome"), null, BloatVerdict.Optional,
            "Assistente ou guia de boas-vindas do fabricante, redundante com o Windows. Pode ser removido se você não usa.", "Manufacturer welcome assistant or guide, redundant with Windows. It can be removed if you do not use it."),
        new(N(@"Dell\s+Customer\s+Connect|Dell\s+Digital\s+Delivery|Dell\s+Mobile\s+Connect|Dell\s+Help\s+(&|and)\s+Support"), null, BloatVerdict.Optional,
            "Assistente ou serviço de cadastro do fabricante, redundante com o Windows. Pode ser removido se você não usa.", "Manufacturer registration assistant or service, redundant with Windows. It can be removed if you do not use it."),
        new(N(@"Lenovo\s+Welcome|Lenovo\s+Companion"), null, BloatVerdict.Optional,
            "Assistente de boas-vindas ou vitrine do fabricante, redundante com o Windows. Pode ser removido se você não usa.", "Manufacturer welcome assistant or storefront, redundant with Windows. It can be removed if you do not use it."),

        // ---------------- Manter por fabricante (drivers e painéis não listados acima)
        new(N(@".*"), N(@"^(Intel|NVIDIA|Advanced\s+Micro\s+Devices|AMD|Realtek)\b"), BloatVerdict.Keep,
            "Software do fabricante do componente (driver ou painel). Mantenha.", "Component manufacturer software (driver or control panel). Keep it."),
    };

    /// <summary>
    /// Classifica um programa pelo nome (e fabricante, quando necessário). Devolve null se o programa não é reconhecido.
    /// O motivo vem no idioma atual (<see cref="Loc"/>).
    /// </summary>
    /// <param name="name">Nome exibido do programa.</param>
    /// <param name="publisher">Fabricante/publicador, se conhecido.</param>
    public static (BloatVerdict Verdict, string Reason)? Classify(string name, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        var p = publisher?.Trim() ?? "";
        foreach (var r in Rules)
        {
            if (!r.Name.IsMatch(n)) continue;
            if (r.Publisher is not null && !r.Publisher.IsMatch(p)) continue;
            return (r.Verdict, Loc.T(r.Pt, r.En));
        }
        return null;
    }

    /// <summary>Rótulo da recomendação no idioma atual (Remover/Opcional/Manter).</summary>
    /// <param name="verdict">Veredito.</param>
    public static string Label(BloatVerdict verdict) => verdict switch
    {
        BloatVerdict.Remove => Loc.T("Remover", "Remove"),
        BloatVerdict.Optional => Loc.T("Opcional", "Optional"),
        _ => Loc.T("Manter", "Keep")
    };
}
