using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Knowledge;

/// <summary>
/// Verbete do glossário para leigos, com as versões em português e em inglês.
/// <see cref="Title"/> e <see cref="Text"/> devolvem o idioma atual (<see cref="Loc"/>).
/// </summary>
/// <param name="Key">Chave estável (ASCII, minúscula).</param>
/// <param name="TitlePt">Título em pt-BR.</param>
/// <param name="TextPt">Explicação em pt-BR, em 2 a 4 frases: o que é, por que importa para jogos e qual valor é bom.</param>
/// <param name="TitleEn">Título em inglês.</param>
/// <param name="TextEn">Explicação em inglês.</param>
public sealed record GlossaryEntry(string Key, string TitlePt, string TextPt, string TitleEn, string TextEn)
{
    /// <summary>Título no idioma atual.</summary>
    public string Title => Loc.T(TitlePt, TitleEn);

    /// <summary>Explicação no idioma atual.</summary>
    public string Text => Loc.T(TextPt, TextEn);
}

/// <summary>
/// Glossário em linguagem simples. Cobre os títulos de seção e de grupo usados pelos coletores e os conceitos
/// que aparecem nos achados. <see cref="Lookup(string)"/> casa por chave, por título exato e por título de grupo normalizado.
/// </summary>
public static partial class Glossary
{
    private static readonly Dictionary<string, (string Title, string Text)> En = BuildEnglish();

    private static GlossaryEntry E(string key, string title, string text)
    {
        var (tEn, xEn) = En.TryGetValue(key, out var v) ? v : (title, text);
        return new(key, title, text, tEn, xEn);
    }

    /// <summary>Todos os verbetes.</summary>
    public static IReadOnlyList<GlossaryEntry> All { get; } = new List<GlossaryEntry>
    {
        // ------------------------------------------------------------ seções e grupos dos coletores
        E("processador", "Processador (CPU)", "O processador é o \"cérebro\" do PC: ele executa a lógica do jogo, a física e prepara os quadros para a placa de vídeo. Mais núcleos e clock maior ajudam em jogos modernos, mas o que pesa é a combinação com a placa de vídeo. Para jogos atuais, 6 núcleos ou mais é um bom ponto de partida."),
        E("memoria", "Memória (RAM)", "A RAM guarda o que o jogo e o Windows estão usando agora. Se ela acaba, o Windows passa a usar o disco no lugar, e o jogo trava. Para jogos atuais, 16 GB é o mínimo confortável e 32 GB dá folga com outros programas abertos."),
        E("memoria_ram", "Memória RAM e velocidade", "Além da quantidade, importa a velocidade da RAM e se ela trabalha em dois canais. Memória rápida e em dual channel deixa o processador mais bem alimentado, o que ajuda principalmente em processadores de poucos núcleos. O valor bom é a velocidade máxima que o processador aceita."),
        E("placa_de_video", "Placa de vídeo (GPU)", "A placa de vídeo desenha as imagens do jogo. É o componente que mais pesa no FPS. O que importa é o modelo, a quantidade de memória de vídeo e se o driver está atualizado."),
        E("monitores", "Monitores", "O monitor define a resolução e quantas vezes por segundo a imagem é atualizada. Um monitor de 144 Hz só mostra 144 quadros por segundo se estiver configurado nessa taxa no Windows. Com dois monitores, taxas diferentes podem causar pequenos engasgos."),
        E("armazenamento", "Armazenamento (discos)", "É onde ficam o Windows e os jogos. SSD e NVMe são muito mais rápidos que HDD, o que reduz telas de carregamento e engasgos. O ideal é ter o Windows e os jogos em SSD, com pelo menos 20% de espaço livre."),
        E("disco", "Disco", "Cada disco físico do PC aparece em um bloco com modelo, tipo (HDD, SSD, NVMe), capacidade, temperatura e saúde. A saúde vem do Windows e dos dados S.M.A.R.T. do próprio disco. Saudável e abaixo de uns 55 °C é o esperado."),
        E("particoes", "Partições e volumes", "São as unidades que você vê no Windows, como C: e D:. Cada uma mostra quanto espaço está livre. Mantenha pelo menos 10% livre, e 20% se for SSD, para o disco não ficar lento."),
        E("steam_bibliotecas", "Bibliotecas Steam", "São as pastas onde a Steam instala os jogos. O app mostra em que disco cada uma está. Jogos em SSD carregam mais rápido e engasgam menos do que jogos em HDD."),
        E("sistema", "Sistema", "Reúne dados do Windows, da placa-mãe e da BIOS, e do plano de energia. É daqui que vêm vários ajustes que afetam a fluidez dos jogos."),
        E("placa_mae_bios", "Placa-mãe e BIOS", "A placa-mãe liga todas as peças do PC, e a BIOS é o programa dela que define como as peças funcionam. Várias melhorias de jogos, como XMP e Resizable BAR, são ligadas na BIOS. Ter a BIOS em uma versão estável e recente ajuda na compatibilidade."),
        E("energia", "Energia", "Mostra o plano de energia do Windows e seus ajustes, como o estado mínimo e máximo do processador e a suspensão de USB. Em desktop para jogos, o ideal é um plano que deixe o processador chegar ao máximo quando preciso."),
        E("timers", "Timers do sistema (BCD)", "São opções avançadas de inicialização do Windows que controlam o relógio interno. A maioria das pessoas não precisa mexer, e o padrão do Windows costuma ser o melhor. O app só avisa se algo fora do padrão estiver forçado."),
        E("seguranca", "Virtualização, antivírus e serviços", "Reúne recursos de segurança e virtualização do Windows, o antivírus e serviços em segundo plano. Alguns recursos de segurança protegem muito, mas custam um pouco de desempenho em jogos."),
        E("virtualizacao", "Virtualização e isolamento de núcleo (VBS / HVCI)", "O Windows pode usar a virtualização para proteger o núcleo do sistema contra drivers maliciosos. Isso aumenta a segurança, mas pode custar alguns por cento de desempenho em jogos, principalmente em processadores modestos. É uma troca entre segurança e velocidade."),
        E("antivirus", "Antivírus", "O antivírus verifica arquivos em tempo real. Ter dois antivírus ativos ao mesmo tempo deixa o PC mais lento e pode dar conflito. O ideal é um só, ativo e com as assinaturas em dia."),
        E("minifiltros", "Minifiltros do sistema de arquivos", "São pequenos drivers que ficam entre o Windows e o disco, usados por antivírus, backup e criptografia. Cada filtro soma um pequeno atraso em leituras e gravações. Poucos filtros e de programas que você realmente usa é o ideal."),
        E("servicos_temporarios", "Serviços e arquivos temporários", "Mostra serviços do Windows que rodam em segundo plano, como o SysMain e a busca, e quanto espaço os arquivos temporários ocupam. Temporários acima de uns 500 MB podem ser limpos com segurança."),
        E("processos", "Processos, inicialização, rede e áudio", "Mostra quais programas estão usando mais processador e memória agora, o que abre junto com o Windows e os adaptadores de rede e áudio. Programas abertos em segundo plano competem com o jogo por recursos."),
        E("processos_cpu", "Processos que mais usam a CPU", "É a lista dos programas que mais usam o processador no momento da leitura. Em repouso, o valor esperado é baixo. Se algo fora do jogo aparece alto, vale fechá-lo antes de jogar."),
        E("processos_ram", "Processos que mais usam a RAM", "É a lista dos programas que mais ocupam memória agora. Navegadores e launchers costumam estar no topo. Fechar o que você não usa libera RAM para o jogo."),
        E("inicializacao", "Programas de inicialização", "São os programas que abrem sozinhos quando o Windows liga. Muitos deles ficam o tempo todo gastando memória e processador. Você pode impedi-los de abrir na inicialização sem desinstalar nada."),
        E("rede_audio", "Rede e áudio", "Lista as placas de rede e os dispositivos de som ativos. Adaptadores virtuais, como os de VPN, podem somar latência em alguns casos. O app só aponta o que encontrou."),
        E("eventos", "Eventos do Windows", "O Windows guarda um diário de erros e avisos. O app procura nele sinais ligados a engasgos, como travas do driver de vídeo, erros de disco e desligamentos inesperados. Nenhum evento grave nos últimos 7 dias é o ideal."),
        E("erros_boot", "Erros desde o boot", "São os erros que o Windows registrou desde que o PC foi ligado. Muitos são inofensivos, como falhas de serviços que não afetam jogos. Importa mais a origem e a repetição do erro do que a quantidade."),
        E("eventos_engasgos", "Eventos ligados a engasgos", "São eventos do Windows que costumam estar ligados a travadas: o driver de vídeo reiniciando, erros de disco, erros de hardware e reinícios inesperados. Se aparecem de forma repetida, vale investigar driver, cabos, energia e temperatura."),
        E("analise", "Análise da plataforma", "Cruza as peças do seu PC para dizer onde está o limite: configuração da BIOS, placa-mãe, processador ou placa de vídeo. Para cada ponto, diz se a correção é grátis ou exige hardware."),
        E("cadeia_pcie", "Cadeia PCIe da placa de vídeo", "A placa de vídeo conversa com o resto do PC por uma ligação chamada PCIe. Essa ligação depende da própria placa, do processador, da placa-mãe e da BIOS, e a velocidade final é a do elo mais lento. O ideal é que o link chegue à geração que todas as peças suportam."),
        E("resizable_bar", "Resizable BAR", "É um recurso que deixa o processador acessar toda a memória da placa de vídeo de uma vez, em vez de em pedaços pequenos. O ganho é pequeno e varia por jogo, de nada a poucos por cento. Exige placa de vídeo e processador compatíveis e é ligado na BIOS, junto com o Above 4G Decoding."),
        E("equilibrio", "Equilíbrio do sistema", "Verifica se o processador e a placa de vídeo combinam entre si. Uma placa de vídeo muito forte com um processador de poucos núcleos faz o processador ser o limite. Nesse caso, limitar o FPS deixa o jogo mais estável."),
        E("upgrade", "Caminho de upgrade", "Sugere uma troca de peça que traz ganho sem trocar a placa-mãe, como um processador melhor que serve no mesmo encaixe. Confira sempre a lista de compatibilidade do fabricante da placa antes de comprar."),
        E("fonte", "Fonte de alimentação (PSU)", "A fonte entrega energia a todas as peças. Uma fonte fraca pode fazer o PC desligar ou reiniciar em jogos pesados. O app estima o consumo máximo e recomenda uma fonte com cerca de 50% de folga."),
        E("dados_nvidia", "Dados do driver NVIDIA", "São leituras feitas pelo driver da NVIDIA: versão da VBIOS, memória de vídeo, temperatura, potência, clocks e link PCIe. Servem para ver se a placa está trabalhando como deveria."),
        E("subsistema_grafico", "Subsistema gráfico", "Reúne opções do Windows que afetam jogos: HAGS, Game DVR, Modo de Jogo, MPO e as otimizações para jogos em janela. Cada uma pode ajudar ou atrapalhar conforme o jogo e o driver."),

        E("sensores", "Sensores", "São os medidores de temperatura, ventoinha, tensão e potência das peças do PC. O Windows sozinho não mostra, por exemplo, a temperatura do processador; para isso o WinDiag lê o HWiNFO ou a biblioteca LibreHardwareMonitor. É uma leitura de um instante só: para ver o comportamento no jogo, use a captura."),
        E("fontes_sensores", "Fontes de sensores", "Mostra de onde vieram as leituras: HWiNFO aberto com Shared Memory Support, a biblioteca LibreHardwareMonitor embutida e o driver PawnIO, se você o instalou. O WinDiag só lê e não baixa nem instala drivers. Sem uma dessas fontes, a temperatura da CPU fica indisponível."),

        // ------------------------------------------------------------ conceitos
        E("pcie", "PCIe", "É a \"estrada\" que liga a placa de vídeo e os SSDs NVMe ao resto do PC. Quanto mais nova a geração e mais linhas, maior a velocidade. Para jogos, o que importa é a placa de vídeo operar na geração e na largura que suporta."),
        E("pcie_geracao", "Geração do PCIe", "A geração (Gen 3, Gen 4, Gen 5) é a velocidade de cada linha da estrada PCIe; cada geração dobra a velocidade da anterior. Uma placa Gen 4 numa conexão Gen 3 ainda funciona, mas com metade da velocidade de ligação. Em repouso o link cai de geração para economizar energia, o que é normal."),
        E("pcie_linhas", "Linhas do PCIe (x16, x8)", "As linhas são as faixas da estrada PCIe: x16 tem 16 faixas e x8 tem 8. Algumas placas de vídeo de entrada usam x8 de propósito e isso é normal. Já uma placa x16 trabalhando em x4 ou x8 pode indicar slot errado ou riser."),
        E("rebar", "Resizable BAR (ReBAR)", "Deixa o processador acessar toda a memória da placa de vídeo de uma vez. O ganho em jogos costuma ser de nenhum a poucos por cento. É ligado na BIOS, e o Windows precisa estar em modo UEFI."),
        E("hags", "HAGS (agendamento de GPU acelerado por hardware)", "Permite que a placa de vídeo organize o próprio trabalho em vez de depender do processador. Em alguns jogos ajuda, em outros atrapalha, e o efeito depende do driver. Não é correção universal; o padrão do driver costuma ser bom."),
        E("mpo", "MPO (Multiplane Overlay)", "É um recurso do Windows para desenhar a imagem em camadas. Com vários monitores de taxas diferentes, já foi associado a piscadas e engasgos em alguns PCs. Desativar só vale como teste se você tem esses sintomas."),
        E("game_dvr", "Game DVR (captura do Xbox)", "É a gravação em segundo plano da Barra de Jogos do Xbox. Ela consome placa de vídeo e disco, mesmo quando você não usa. Se você não grava clipes pela Barra de Jogos, o ideal é deixar desligada."),
        E("modo_de_jogo", "Modo de Jogo", "É uma função do Windows que dá prioridade ao jogo e reduz atividades em segundo plano durante a partida. Em geral é inofensiva e ajuda um pouco. O ideal é deixá-la ligada."),
        E("vbs_hvci", "VBS e Integridade de Memória (HVCI)", "São proteções do Windows que isolam o núcleo do sistema usando virtualização. Aumentam a segurança, mas podem custar alguns por cento de desempenho em jogos. Desligar reduz a proteção contra drivers maliciosos, então é uma decisão sua."),
        E("hypervisor", "Hypervisor", "É a camada que permite rodar máquinas virtuais, como WSL, Hyper-V e Sandbox. Quando está ativa, o Windows roda por cima dela, o que custa pouco desempenho. Se você não usa máquinas virtuais, pode ficar sem."),
        E("smart", "S.M.A.R.T.", "É o autodiagnóstico que todo disco faz de si mesmo. Contadores como setores ruins e realocados mostram se o disco está se desgastando. O bom é ter zero setores pendentes; qualquer aumento merece backup."),
        E("trim", "TRIM", "É um comando que avisa ao SSD quais áreas não estão mais em uso, para ele se manter rápido. Sem TRIM, o SSD perde velocidade de gravação com o tempo. O valor bom é ativo, que é o padrão do Windows."),
        E("dual_channel", "Dual channel", "É quando dois pentes de memória trabalham juntos em canais separados, dobrando a velocidade de transferência da RAM. Com um pente só, ou dois no mesmo canal, a memória trabalha pela metade. Ajuda mais em processadores de poucos núcleos e em gráficos integrados."),
        E("xmp", "XMP / DOCP / EXPO", "É o perfil de velocidade gravado nos pentes de memória. Sem ligá-lo na BIOS, a RAM roda em velocidade básica, mais lenta do que o que você comprou. Ligar é grátis e costuma ser seguro, mas exige placa e processador que permitam."),
        E("pagefile", "Arquivo de paginação (pagefile)", "É um espaço no disco que o Windows usa quando a RAM não basta. É útil como rede de proteção, mas muito mais lento do que a memória. Se o uso do pagefile é alto durante jogos, falta RAM."),
        E("compressao_memoria", "Compressão de memória", "O Windows pode comprimir dados na RAM para evitar o uso do disco. Com pouca RAM, isso ajuda a evitar travadas. Com muita RAM sobrando, ela gasta um pouco de processador à toa."),
        E("hard_faults", "Hard faults (falhas de página)", "Acontecem quando o Windows precisa buscar no disco algo que deveria estar na RAM. Um número alto e constante durante o jogo indica falta de memória e causa engasgos. Picos curtos ao abrir programas são normais."),
        E("dpc", "DPC (latência de drivers)", "É o tempo que os drivers tomam do processador em tarefas curtas e urgentes. Quando esse tempo é alto, aparecem estalos no som e engasgos. O esperado é ficar bem abaixo de 1% do tempo do processador."),
        E("secure_boot_uefi", "Secure Boot e UEFI", "UEFI é o modo moderno de iniciar o PC, e o Secure Boot é uma proteção que só deixa iniciar sistemas assinados. Alguns recursos, como o Resizable BAR e certos jogos com antitrapaça, exigem UEFI. Ter UEFI ligado é o esperado em PCs atuais."),
        E("bios", "BIOS", "É o programa da placa-mãe que liga o PC e define como as peças funcionam. É nela que se liga XMP, Resizable BAR e se ajusta a geração do PCIe. Atualizar a BIOS só vale quando resolve um problema ou habilita um recurso que você precisa."),
        E("chipset", "Chipset", "É o conjunto de chips da placa-mãe que decide o que ela suporta: geração do PCIe, overclock de memória e quantidade de portas. Ele limita o que o processador consegue oferecer. Está no nome da placa, como B450, B560 ou X670."),
        E("vram", "VRAM (memória de vídeo)", "É a memória da placa de vídeo, onde ficam as texturas do jogo. Quando enche, o jogo busca dados na RAM do PC e engasga. Para 1080p, 8 GB costumam bastar, e para resoluções maiores mais é melhor."),
        E("taxa_atualizacao", "Taxa de atualização (Hz)", "É quantas vezes por segundo o monitor atualiza a imagem. Um monitor de 144 Hz só usa essa taxa se o Windows estiver configurado nela. O bom é usar a taxa máxima do monitor na resolução em uso."),
        E("steam_hdd", "Jogos Steam em HDD", "O HDD é bem mais lento que o SSD para acessar muitos arquivos pequenos, como os de jogos. Isso causa telas de carregamento longas e engasgos. A Steam permite mover o jogo para um SSD sem reinstalar."),
        E("sysmain", "SysMain (Superfetch)", "É um serviço do Windows que pré-carrega programas usados com frequência. Em SSD o ganho é pequeno e, em alguns PCs, ele causa uso de disco. O app só informa o estado; o padrão do Windows costuma ser adequado."),
        E("hibernacao", "Hibernação e Inicialização Rápida", "A Inicialização Rápida faz o \"Desligar\" salvar parte do sistema para ligar mais rápido. Por isso o PC pode ficar dias sem reiniciar de verdade, e problemas se acumulam. \"Reiniciar\" faz um boot limpo."),
        E("plano_energia", "Plano de energia", "É o conjunto de ajustes que diz ao Windows quanto economizar energia. Em desktop para jogos, o ideal é Alto desempenho ou Equilibrado sem limitar o processador a menos de 100%."),
        E("aspm", "ASPM (economia no link PCIe)", "É a função que reduz a energia da ligação PCIe quando ociosa. Ao voltar da economia, pode haver um pequeno atraso. O efeito costuma ser pequeno; desligá-la é uma opção para quem quer eliminar essa variável."),
        E("usb_suspend", "Suspensão seletiva de USB", "O Windows pode desligar portas USB paradas para economizar energia. Em alguns mouses, teclados e headsets isso causa atrasos ou desconexões. Em um desktop, pode ficar desligada sem problema."),
        E("ventoinha", "Ventoinha (fan)", "Move o ar para resfriar as peças. A velocidade sobe com a temperatura, e isso é normal. Se a ventoinha da placa de vídeo está parada em repouso, geralmente é o modo silencioso do fabricante."),
        E("temperatura", "Temperatura", "Peças esquentam sob carga e reduzem a própria velocidade ao ficar quentes demais. Para placa de vídeo e processador, abaixo de 80 °C em jogo é confortável e acima de 90 °C merece atenção. Discos devem ficar abaixo de uns 55 °C."),
        E("psu", "Fonte (PSU)", "A fonte converte a energia da tomada para as peças. O importante é ter potência com folga e boa qualidade, não só o número de watts. Uma fonte no limite pode causar reinícios sob carga."),
        E("tdp", "TDP e limite de potência", "TDP é a potência de referência do processador, e o limite de potência é o máximo que a placa de vídeo pode consumir. O app usa esses números para estimar a fonte necessária. Em picos de turbo o consumo real passa do TDP."),
        E("throttling", "Limitação de clock (throttling)", "É quando uma peça reduz a própria velocidade para não passar de um limite de temperatura, potência ou tensão. Aparece como queda de FPS sob carga. Se acontece o tempo todo, vale revisar a refrigeração."),
        E("hpet", "HPET e relógio do sistema", "O HPET é um relógio antigo da placa-mãe. Forçar seu uso no Windows pode aumentar a latência em jogos. O padrão do Windows, sem forçar, é o recomendado."),
        E("nucleos_threads", "Núcleos e threads", "Núcleos são as unidades de trabalho do processador, e threads são as tarefas que cada núcleo consegue tocar ao mesmo tempo. Jogos modernos usam bem 6 ou mais núcleos. Com 4 núcleos, programas em segundo plano competem com o jogo."),
        E("frametime", "FPS e frametime", "FPS é a média de quadros por segundo, e frametime é o tempo de cada quadro. Um FPS alto com frametime irregular dá sensação de engasgo. A estabilidade importa tanto quanto a média."),
        E("tipos_disco", "HDD, SSD e NVMe", "HDD tem partes móveis e é lento. SSD usa memória flash e é muito mais rápido, e NVMe é o SSD que se liga direto no PCIe, ainda mais rápido. Para jogos e Windows, SSD já faz grande diferença."),
        E("uptime", "Tempo ligado (uptime)", "É há quanto tempo o PC está ligado desde o último boot de verdade. Muitos dias sem reiniciar podem acumular problemas de drivers. Reiniciar de vez em quando mantém o Windows estável."),
        E("tdr", "TDR (driver de vídeo reiniciado)", "É quando o Windows reinicia o driver de vídeo porque a placa deixou de responder. A tela pisca ou fica preta por um instante. Se se repete, pode ser driver, energia, temperatura ou overclock."),
        E("whea", "WHEA (erros de hardware)", "É o registro de erros de hardware detectados pelo processador, como falhas de memória ou do barramento PCIe. Erros corrigidos ocasionais podem ser normais. Se são frequentes, indicam instabilidade."),
        E("crc_sata", "Erros de CRC do SATA", "Indicam falhas na comunicação entre disco e placa-mãe, quase sempre por cabo ruim ou mal encaixado. O contador só sobe. O que importa é se ele continua aumentando depois de trocar o cabo."),
        E("setores_ruins", "Setores realocados e pendentes", "São pedaços do disco que deixaram de funcionar. Poucos e estáveis são tolerados; pendentes ou em crescimento indicam desgaste e risco de perder dados. Nesse caso, faça backup."),
        E("wsl_vm", "WSL e máquinas virtuais", "O WSL e as máquinas virtuais reservam parte da RAM para si enquanto estão ativos. Aparecem como processo vmmem. Fechar o aplicativo dono libera a memória para o jogo."),
        E("atlasos", "AtlasOS", "É uma versão modificada do Windows que já aplica vários ajustes de desempenho. Por isso o WinDiag evita reaplicar ajustes por cima, para não gerar conflitos."),
        E("above_4g", "Above 4G Decoding", "É uma opção da BIOS que permite ao PC endereçar mais memória do que 4 GB para dispositivos. É pré-requisito do Resizable BAR. Deve estar ligada junto com ele."),
        E("csm", "CSM (modo legado)", "É um modo de compatibilidade da BIOS que imita PCs antigos. Com ele ligado, o Windows inicia no modo legado e o Resizable BAR não funciona. Em PCs atuais, o ideal é CSM desligado e boot UEFI."),
        E("overclock_memoria", "Overclock de memória", "É rodar a RAM mais rápido do que o padrão do processador. O XMP é o jeito mais simples e feito pelo fabricante da memória. Nem todo chipset permite."),
        E("estado_processador", "Estado do processador (mín / máx)", "É o limite que o plano de energia coloca no uso do processador. O máximo deve ser 100% para o processador usar todo o turbo. Valores menores reduzem desempenho em jogos."),
        E("game_bar", "Barra de Jogos do Xbox", "É a camada do Windows com atalhos de captura e overlays. Quando fica gravando em segundo plano, gasta recursos. Dá para desativar a captura sem perder o resto."),
    };

    private static readonly Dictionary<string, GlossaryEntry> ByKey =
        All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, GlossaryEntry> ByNormTitle = BuildTitleIndex();

    // Prefixos de títulos normalizados de grupos -> chave. Ordem importa: do mais específico ao mais geral.
    private static readonly (string Prefix, string Key)[] PrefixRules =
    {
        ("disco ", "disco"),
        ("top 5 processos", "processos_cpu"),
        ("top 8 processos", "processos_ram"),
        ("memoria de video", "vram"),
        ("virtualizacao e isolamento", "virtualizacao"),
        ("virtualizacao antivirus", "seguranca"),
        ("minifilters", "minifiltros"),
        ("minifiltros", "minifiltros"),
        ("servicos e temporarios", "servicos_temporarios"),
        ("timers do sistema", "timers"),
        ("eventos ligados", "eventos_engasgos"),
        ("rede e audio", "rede_audio"),
        ("processos inicializacao", "processos"),
        ("dados do driver nvidia", "dados_nvidia"),
        ("cadeia pcie", "cadeia_pcie"),
        ("fonte de alimentacao", "fonte"),
        ("bibliotecas steam", "steam_bibliotecas"),
        ("placa mae", "placa_mae_bios"),
        ("memoria ram", "memoria_ram"),
        ("pentes de memoria", "memoria"),
        ("sistema operacional", "sistema"),
        ("analise da plataforma", "analise"),
        ("equilibrio do sistema", "equilibrio"),
        ("caminho de upgrade", "upgrade"),
        ("erros desde o boot", "erros_boot"),
        ("subsistema grafico", "subsistema_grafico"),
        // Equivalentes em inglês (títulos de grupo gerados com Loc em en).
        ("disk ", "disco"),
        ("top 5 processes", "processos_cpu"),
        ("top 8 processes", "processos_ram"),
        ("video memory", "vram"),
        ("virtualization and core isolation", "virtualizacao"),
        ("virtualization antivirus", "seguranca"),
        ("file system minifilters", "minifiltros"),
        ("services and temp", "servicos_temporarios"),
        ("system timers", "timers"),
        ("stutter related events", "eventos_engasgos"),
        ("events related to stutter", "eventos_engasgos"),
        ("network and audio", "rede_audio"),
        ("processes startup", "processos"),
        ("nvidia driver data", "dados_nvidia"),
        ("pcie chain", "cadeia_pcie"),
        ("graphics card pcie chain", "cadeia_pcie"),
        ("power supply", "fonte"),
        ("steam libraries", "steam_bibliotecas"),
        ("motherboard", "placa_mae_bios"),
        ("ram ", "memoria_ram"),
        ("memory modules", "memoria"),
        ("operating system", "sistema"),
        ("platform analysis", "analise"),
        ("system balance", "equilibrio"),
        ("upgrade path", "upgrade"),
        ("errors since boot", "erros_boot"),
        ("graphics subsystem", "subsistema_grafico"),
    };

    private static readonly Regex GpuNameRx = new(@"NVIDIA|GeForce|\bRTX\b|\bGTX\b|Radeon|\bArc\b|Intel\(R\)\s*(UHD|Iris|HD)\s*Graphics|\bUHD Graphics\b|Vega\s*\d*\s*Graphics|AMD\s+Graphics", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MonitorNameRx = new(@"\b(AOC|LG|Samsung|Dell|BenQ|ASUS|Acer|ViewSonic|Philips|MSI|Gigabyte|HP|Lenovo|Sony|Eizo|Generic PnP|PnP Monitor|Monitor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Dictionary<string, GlossaryEntry> BuildTitleIndex()
    {
        var d = new Dictionary<string, GlossaryEntry>(StringComparer.Ordinal);
        foreach (var e in All) { d.TryAdd(Normalize(e.TitlePt), e); d.TryAdd(Normalize(e.TitleEn), e); }
        // Títulos de seção usados por ICollector.Title e variantes de grupos.
        void Alias(string title, string key) => d.TryAdd(Normalize(title), ByKey[key]);
        Alias("Processador", "processador");
        Alias("CPU", "processador");
        Alias("Memória", "memoria");
        Alias("Memória RAM", "memoria_ram");
        Alias("Placa de vídeo", "placa_de_video");
        Alias("Monitores", "monitores");
        Alias("Armazenamento", "armazenamento");
        Alias("Disco", "disco");
        Alias("Partições", "particoes");
        Alias("Sistema", "sistema");
        Alias("Energia", "energia");
        Alias("Antivírus", "antivirus");
        Alias("Resizable BAR", "resizable_bar");
        Alias("Virtualização, antivírus e serviços", "seguranca");
        Alias("Processos, inicialização, rede e áudio", "processos");
        Alias("Eventos do Windows", "eventos");
        Alias("Análise da plataforma", "analise");
        Alias("Programas de inicialização", "inicializacao");
        Alias("Rede e áudio ativos", "rede_audio");
        Alias("Memória de vídeo", "vram");
        Alias("Placa-mãe e BIOS", "placa_mae_bios");
        Alias("Fonte de alimentação", "fonte");
        Alias("Subsistema gráfico", "subsistema_grafico");
        Alias("Dados do driver NVIDIA (nvidia-smi)", "dados_nvidia");
        Alias("Cadeia PCIe da placa de vídeo", "cadeia_pcie");
        Alias("Equilíbrio do sistema", "equilibrio");
        Alias("Caminho de upgrade", "upgrade");
        Alias("Erros desde o boot", "erros_boot");
        Alias("Eventos ligados a engasgos (7 dias)", "eventos_engasgos");
        Alias("Serviços e temporários", "servicos_temporarios");
        Alias("Bibliotecas Steam", "steam_bibliotecas");
        Alias("Timers do sistema (BCD)", "timers");
        Alias("Minifilters de sistema de arquivos (fltmc filters)", "minifiltros");
        Alias("Top 5 processos por CPU agora (100% = 1 thread inteira)", "processos_cpu");
        Alias("Top 8 processos por RAM", "processos_ram");
        Alias("Virtualização e isolamento de núcleo (VBS / HVCI)", "virtualizacao");
        Alias("Hibernação", "hibernacao");
        Alias("Inicialização Rápida", "hibernacao");
        Alias("Fast Startup", "hibernacao");
        Alias("HAGS", "hags");
        Alias("Game DVR", "game_dvr");
        Alias("VBS", "vbs_hvci");
        Alias("HVCI", "vbs_hvci");
        Alias("SMART", "smart");
        Alias("Dual channel", "dual_channel");
        Alias("XMP", "xmp");
        Alias("Secure Boot", "secure_boot_uefi");
        Alias("UEFI", "secure_boot_uefi");
        Alias("ReBAR", "rebar");
        Alias("PSU", "psu");
        Alias("Fonte", "psu");
        // Aliases em inglês de títulos de seção usados pelos coletores.
        Alias("Processor", "processador");
        Alias("Memory", "memoria");
        Alias("RAM", "memoria_ram");
        Alias("Graphics card", "placa_de_video");
        Alias("Storage", "armazenamento");
        Alias("Partitions", "particoes");
        Alias("System", "sistema");
        Alias("Power", "energia");
        Alias("Virtualization, antivirus and services", "seguranca");
        Alias("Processes, startup, network and audio", "processos");
        Alias("Windows events", "eventos");
        Alias("Startup programs", "inicializacao");
        Alias("Video memory", "vram");
        Alias("Motherboard and BIOS", "placa_mae_bios");
        Alias("Power supply", "fonte");
        Alias("Hibernation", "hibernacao");
        Alias("Services and temporary files", "servicos_temporarios");
        Alias("Active network and audio", "rede_audio");
        return d;
    }

    /// <summary>Normaliza: sem acento, minúsculo, sem texto entre colchetes e sem pontuação.</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Procura o verbete por chave, por título exato e por título de grupo normalizado
    /// (ex.: "Disco 0 [D:]" resolve para "disco"; nomes de placa de vídeo resolvem para "placa_de_video").
    /// </summary>
    /// <param name="titleOrKey">Chave, título de verbete ou título de grupo/seção.</param>
    /// <returns>O verbete ou null se não houver correspondência.</returns>
    public static GlossaryEntry? Lookup(string titleOrKey)
    {
        if (string.IsNullOrWhiteSpace(titleOrKey)) return null;
        try
        {
            var raw = titleOrKey.Trim();
            if (ByKey.TryGetValue(raw, out var k)) return k;
            var e = All.FirstOrDefault(x => string.Equals(x.TitlePt, raw, StringComparison.OrdinalIgnoreCase) || string.Equals(x.TitleEn, raw, StringComparison.OrdinalIgnoreCase));
            if (e is not null) return e;

            var n = Normalize(raw);
            if (n.Length == 0) return null;
            if (ByNormTitle.TryGetValue(n, out var t)) return t;
            foreach (var (prefix, key) in PrefixRules)
                if (n.StartsWith(prefix, StringComparison.Ordinal) || n == prefix.Trim()) return ByKey[key];
            if (GpuNameRx.IsMatch(raw)) return ByKey["placa_de_video"];
            if (MonitorNameRx.IsMatch(raw)) return ByKey["monitores"];
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Como <see cref="Lookup(string)"/>, mas usa a seção para nomes dinâmicos: em "gpu" um título desconhecido é a placa de vídeo;
    /// em "monitores", um monitor; em "armazenamento", um disco.
    /// </summary>
    /// <param name="title">Título do grupo.</param>
    /// <param name="sectionKey">Chave da seção (ex.: "gpu", "monitores", "armazenamento").</param>
    public static GlossaryEntry? Lookup(string title, string? sectionKey)
    {
        var e = Lookup(title);
        if (e is not null) return e;
        return sectionKey switch
        {
            "gpu" => ByKey["placa_de_video"],
            "monitores" => ByKey["monitores"],
            "armazenamento" => ByKey["armazenamento"],
            "sensores" => ByKey["sensores"],
            _ => null
        };
    }
}
