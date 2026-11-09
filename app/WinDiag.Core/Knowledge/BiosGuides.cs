namespace WinDiag.Core.Knowledge;

/// <summary>Assunto de um passo a passo de BIOS.</summary>
public enum BiosTopic
{
    /// <summary>Geração do slot PCIe da placa de vídeo.</summary>
    PcieSlot,
    /// <summary>Above 4G Decoding e Resizable BAR.</summary>
    ReBar,
    /// <summary>Perfil XMP / DOCP / EXPO da memória.</summary>
    Xmp,
    /// <summary>Virtualização (Intel VT-x / AMD SVM).</summary>
    Virtualization,
    /// <summary>Atualização da BIOS.</summary>
    BiosUpdate
}

/// <summary>Passo a passo para um ajuste de BIOS em um fabricante. Os textos seguem o idioma atual (<see cref="Loc"/>); nomes de menus da BIOS ficam em inglês nos dois idiomas.</summary>
/// <param name="Topic">Assunto.</param>
/// <param name="Vendor">Fabricante coberto ("Gigabyte / Aorus", "ASUS / ROG", "MSI", "ASRock" ou Loc.T("Genérico", "Generic")).</param>
/// <param name="Title">Título do guia.</param>
/// <param name="EnterKey">Tecla para entrar na BIOS.</param>
/// <param name="Steps">Passos numerados (texto sem a numeração).</param>
/// <param name="Revert">Como desfazer.</param>
/// <param name="Caution">Cuidado importante.</param>
public sealed record BiosGuide(BiosTopic Topic, string Vendor, string Title, string EnterKey, IReadOnlyList<string> Steps, string Revert, string Caution);

/// <summary>
/// Guias de BIOS por assunto e fabricante. Os nomes dos menus mudam conforme a placa e a versão da BIOS,
/// por isso cada guia avisa que os caminhos podem variar. Nada é alterado pelo WinDiag: é só texto.
/// </summary>
public static class BiosGuides
{
    /// <summary>Aviso de que os nomes de menu variam conforme a placa e a versão da BIOS. Entra em todo guia porque os caminhos são genéricos por fabricante, não por modelo.</summary>
    private static string Vary => Loc.T("Os nomes dos menus podem variar conforme o modelo da placa e a versão da BIOS; se não achar o item exato, procure por um nome parecido ou consulte o manual da placa.",
        "Menu names may vary depending on the board model and BIOS version; if you cannot find the exact item, look for a similar name or check the board manual.");
    /// <summary>Passo final comum a todos os guias: salvar e reiniciar.</summary>
    private static string Save => Loc.T("Aperte F10 para salvar e reiniciar, e confirme.", "Press F10 to save and restart, and confirm.");
    /// <summary>Orientação padrão para desfazer: anotar o valor antigo, carregar os padrões otimizados e, em último caso, limpar a CMOS. Não depende do fabricante.</summary>
    private static string RevertDefault => Loc.T("Anote o valor antigo antes de mudar. Se o PC não iniciar, entre de novo na BIOS e use Load Optimized Defaults; se nem assim entrar, limpe a CMOS (tire a bateria da placa por alguns minutos ou use o jumper/botão CLR_CMOS do manual).",
        "Write down the old value before changing it. If the PC does not start, enter the BIOS again and use Load Optimized Defaults; if you cannot get in even then, clear the CMOS (remove the board battery for a few minutes or use the CLR_CMOS jumper/button from the manual).");
    /// <summary>Fabricantes de placa-mãe com roteiro próprio; <see cref="Generic"/> cobre os demais.</summary>
    private enum Vendor
    {
        /// <summary>Gigabyte e a linha Aorus.</summary>
        Gigabyte,
        /// <summary>ASUS e as linhas ROG, TUF e PRIME.</summary>
        Asus,
        /// <summary>MSI (Micro-Star).</summary>
        Msi,
        /// <summary>ASRock.</summary>
        AsRock,
        /// <summary>Fabricante não identificado: guia com instruções gerais.</summary>
        Generic
    }

    /// <summary>Identifica o fabricante pelo texto do modelo/fabricante da placa (comparação sem diferenciar maiúsculas). Marcas de linha (ROG, TUF, PRIME, AORUS) também contam, porque o WMI nem sempre traz o nome da empresa.</summary>
    /// <param name="board">Fabricante ou modelo da placa; null ou vazio resulta em genérico.</param>
    private static Vendor Detect(string? board)
    {
        if (string.IsNullOrWhiteSpace(board)) return Vendor.Generic;
        var b = board.ToUpperInvariant();
        if (b.Contains("GIGABYTE") || b.Contains("AORUS")) return Vendor.Gigabyte;
        if (b.Contains("ASUS") || b.Contains("ROG") || b.Contains("TUF") || b.Contains("PRIME")) return Vendor.Asus;
        if (b.Contains("MSI") || b.Contains("MICRO-STAR") || b.Contains("MICRO STAR")) return Vendor.Msi;
        if (b.Contains("ASROCK")) return Vendor.AsRock;
        return Vendor.Generic;
    }

    /// <summary>Nome do fabricante exibido no guia.</summary>
    /// <param name="v">Fabricante.</param>
    private static string VendorName(Vendor v) => v switch
    {
        Vendor.Gigabyte => "Gigabyte / Aorus",
        Vendor.Asus => "ASUS / ROG",
        Vendor.Msi => "MSI",
        Vendor.AsRock => "ASRock",
        _ => Loc.T("Genérico", "Generic")
    };

    /// <summary>Tecla para entrar na BIOS conforme o fabricante. Valores comuns de cada marca; o genérico manda conferir a tela de logotipo ou o manual.</summary>
    /// <param name="v">Fabricante.</param>
    private static string EnterKey(Vendor v) => v switch
    {
        Vendor.Gigabyte => Loc.T("Del (ou F2) logo ao ligar o PC", "Del (or F2) right after powering on the PC"),
        Vendor.Asus => Loc.T("Del (ou F2) logo ao ligar o PC", "Del (or F2) right after powering on the PC"),
        Vendor.Msi => Loc.T("Del logo ao ligar o PC", "Del right after powering on the PC"),
        Vendor.AsRock => Loc.T("F2 (ou Del) logo ao ligar o PC", "F2 (or Del) right after powering on the PC"),
        _ => Loc.T("Del ou F2 logo ao ligar o PC (veja a tecla na tela de logotipo ou no manual)", "Del or F2 right after powering on the PC (see the key on the logo screen or in the manual)")
    };

    /// <summary>Como sair do modo simples da BIOS para o avançado, onde ficam as opções dos guias.</summary>
    /// <param name="v">Fabricante.</param>
    private static string Advanced(Vendor v) => v switch
    {
        Vendor.Gigabyte => Loc.T("Se abrir o modo simples, aperte F2 para o modo avançado.", "If it opens in simple mode, press F2 for advanced mode."),
        Vendor.Asus => Loc.T("Se abrir o EZ Mode, aperte F7 para o modo avançado.", "If it opens in EZ Mode, press F7 for advanced mode."),
        Vendor.Msi => Loc.T("Se abrir o modo simples, aperte F7 para o modo avançado.", "If it opens in simple mode, press F7 for advanced mode."),
        Vendor.AsRock => Loc.T("Se abrir uma tela simples, aperte F6 para o modo avançado (em algumas versões o modo avançado já abre direto).", "If it opens a simple screen, press F6 for advanced mode (in some versions advanced mode opens directly)."),
        _ => Loc.T("Se a BIOS abrir em um modo simples, procure a opção de modo avançado (em geral F7, F6 ou F2).", "If the BIOS opens in a simple mode, look for the advanced mode option (usually F7, F6 or F2).")
    };

    /// <summary>Devolve o guia do assunto para o fabricante da placa. Fabricante desconhecido ou nulo recebe o guia genérico.</summary>
    /// <param name="topic">Assunto.</param>
    /// <param name="boardManufacturer">Fabricante ou modelo da placa-mãe (ex.: "Gigabyte H510M S2H"), ou null.</param>
    public static BiosGuide For(BiosTopic topic, string? boardManufacturer)
    {
        var v = Detect(boardManufacturer);
        return topic switch
        {
            BiosTopic.PcieSlot => PcieSlot(v),
            BiosTopic.ReBar => ReBar(v),
            BiosTopic.Xmp => Xmp(v),
            BiosTopic.Virtualization => Virtualization(v),
            BiosTopic.BiosUpdate => BiosUpdate(v),
            _ => PcieSlot(v)
        };
    }

    /// <summary>Converte o nome do assunto (como em <c>Finding.GuideTopic</c>) em <see cref="BiosTopic"/>. Aceita maiúsculas e minúsculas.</summary>
    /// <param name="topic">Texto, ex.: "PcieSlot".</param>
    /// <param name="t">Assunto convertido.</param>
    /// <returns>true se o texto é um assunto válido.</returns>
    public static bool TryParse(string? topic, out BiosTopic t)
    {
        t = default;
        if (string.IsNullOrWhiteSpace(topic)) return false;
        return Enum.TryParse(topic.Trim(), ignoreCase: true, out t) && Enum.IsDefined(t);
    }

    /// <summary>Monta um guia completo: os passos fixos de entrada na BIOS e modo avançado, os passos específicos do assunto, o salvamento e o aviso de variação de menus.</summary>
    /// <param name="topic">Assunto.</param>
    /// <param name="v">Fabricante.</param>
    /// <param name="title">Título do guia.</param>
    /// <param name="mid">Passos específicos do assunto, sem numeração.</param>
    /// <param name="revert">Como desfazer; null usa <see cref="RevertDefault"/>.</param>
    /// <param name="caution">Cuidado; null usa o aviso padrão de mexer só no item descrito.</param>
    private static BiosGuide Make(BiosTopic topic, Vendor v, string title, List<string> mid, string? revert = null, string? caution = null)
    {
        var steps = new List<string>
        {
            Loc.T($"Reinicie o PC e aperte {EnterKey(v)} para entrar na BIOS.", $"Restart the PC and press {EnterKey(v)} to enter the BIOS."),
            Advanced(v),
        };
        steps.AddRange(mid);
        steps.Add(Save);
        steps.Add(Vary);
        return new BiosGuide(topic, VendorName(v), title, EnterKey(v), steps, revert ?? RevertDefault,
            caution ?? Loc.T("Mexa só no item descrito e anote o valor antigo. Não altere tensões nem outras opções que você não conheça.", "Change only the item described and write down the old value. Do not change voltages or other options you are not familiar with."));
    }

    /// <summary>Guia para ajustar a geração do slot PCIe da placa de vídeo (item de configuração de PCIe/slot na BIOS).</summary>
    /// <param name="v">Fabricante.</param>
    private static BiosGuide PcieSlot(Vendor v)
    {
        string title = Loc.T("Ajustar a geração do slot PCIe da placa de vídeo", "Adjust the graphics card PCIe slot generation");
        var mid = v switch
        {
            Vendor.Gigabyte => new List<string>
            {
                Loc.T("Vá em Settings > IO Ports (em algumas versões, Peripherals).", "Go to Settings > IO Ports (in some versions, Peripherals)."),
                Loc.T("Abra PCIEX16 Slot Configuration (ou \"PCIe Slot Configuration\").", "Open PCIEX16 Slot Configuration (or \"PCIe Slot Configuration\")."),
                Loc.T("Troque o valor de Gen4/Gen3 fixo para Auto. Se a placa de vídeo for de uma geração mais antiga que a do slot, escolha a geração dela (por exemplo Gen3).", "Change the fixed Gen4/Gen3 value to Auto. If the graphics card is an older generation than the slot, choose its generation (for example Gen3)."),
            },
            Vendor.Asus => new List<string>
            {
                Loc.T("Vá em Advanced > PCH/System Agent (SA) Configuration > PEG Port Configuration (em algumas placas, Advanced > Onboard Devices Configuration).", "Go to Advanced > PCH/System Agent (SA) Configuration > PEG Port Configuration (on some boards, Advanced > Onboard Devices Configuration)."),
                Loc.T("Em PCIEX16 Link Speed (ou \"PEG Port Link Speed\"), escolha Auto. Se precisar fixar, escolha a geração suportada pela placa de vídeo e pelo processador.", "In PCIEX16 Link Speed (or \"PEG Port Link Speed\"), choose Auto. If you need to fix it, choose the generation supported by the graphics card and the processor."),
            },
            Vendor.Msi => new List<string>
            {
                Loc.T("Vá em Settings > Advanced > PCI Subsystem Settings (em algumas versões, Advanced > Integrated Peripherals).", "Go to Settings > Advanced > PCI Subsystem Settings (in some versions, Advanced > Integrated Peripherals)."),
                Loc.T("Em PEG0 Max Link Speed (ou \"PCI_E1 Max Link Speed\"), escolha Auto. Se precisar fixar, escolha a geração suportada pela placa de vídeo e pelo processador.", "In PEG0 Max Link Speed (or \"PCI_E1 Max Link Speed\"), choose Auto. If you need to fix it, choose the generation supported by the graphics card and the processor."),
            },
            Vendor.AsRock => new List<string>
            {
                Loc.T("Vá em Advanced > Chipset Configuration (em algumas placas, Advanced > North Bridge).", "Go to Advanced > Chipset Configuration (on some boards, Advanced > North Bridge)."),
                Loc.T("Em PCIE1 Link Speed (ou \"PCIE Link Speed\"), escolha Auto. Se precisar fixar, escolha a geração suportada pela placa de vídeo e pelo processador.", "In PCIE1 Link Speed (or \"PCIE Link Speed\"), choose Auto. If you need to fix it, choose the generation supported by the graphics card and the processor."),
            },
            _ => new List<string>
            {
                Loc.T("Procure, nos menus Advanced, Chipset, Peripherals ou IO Ports, a opção da geração do slot da placa de vídeo (nomes comuns: PCIEX16 Slot Configuration, PEG Link Speed, PCIE1 Link Speed).", "Look, in the Advanced, Chipset, Peripherals or IO Ports menus, for the graphics card slot generation option (common names: PCIEX16 Slot Configuration, PEG Link Speed, PCIE1 Link Speed)."),
                Loc.T("Escolha Auto. Se precisar fixar, escolha a geração suportada pela placa de vídeo e pelo processador.", "Choose Auto. If you need to fix it, choose the generation supported by the graphics card and the processor."),
            },
        };
        mid.Add(Loc.T("Depois de entrar no Windows, confira o link em um jogo ou teste de carga, pois em repouso a geração cai para economizar energia.", "After booting into Windows, check the link in a game or load test, since at idle the generation drops to save power."));
        return Make(BiosTopic.PcieSlot, v, title, mid,
            caution: Loc.T("Se a opção estiver em Auto e o link ainda for baixo, verifique se a placa está bem encaixada no slot principal e se não há cabo extensor (riser). Se a opção não existir, atualize a BIOS; veja o guia de atualização.", "If the option is on Auto and the link is still low, check that the card is well seated in the main slot and that there is no extender cable (riser). If the option does not exist, update the BIOS; see the update guide."));
    }

    /// <summary>Guia para ligar Above 4G Decoding e Resizable BAR (exige boot UEFI; o CSM ligado costuma impedir).</summary>
    /// <param name="v">Fabricante.</param>
    private static BiosGuide ReBar(Vendor v)
    {
        string title = Loc.T("Ligar o Resizable BAR (e o Above 4G Decoding)", "Turn on Resizable BAR (and Above 4G Decoding)");
        var mid = v switch
        {
            Vendor.Gigabyte => new List<string>
            {
                Loc.T("Vá em Settings > IO Ports e ligue Above 4G Decoding (Enabled).", "Go to Settings > IO Ports and turn on Above 4G Decoding (Enabled)."),
                Loc.T("Ainda nesse menu, coloque Re-Size BAR Support em Auto ou Enabled.", "Still in that menu, set Re-Size BAR Support to Auto or Enabled."),
                Loc.T("Vá em Boot e coloque CSM Support em Disabled (o Resizable BAR exige boot UEFI).", "Go to Boot and set CSM Support to Disabled (Resizable BAR requires UEFI boot)."),
            },
            Vendor.Asus => new List<string>
            {
                Loc.T("Vá em Advanced > PCI Subsystem Settings e ligue Above 4G Decoding (Enabled).", "Go to Advanced > PCI Subsystem Settings and turn on Above 4G Decoding (Enabled)."),
                Loc.T("Ainda nesse menu, coloque Re-Size BAR Support em Enabled (ou Auto).", "Still in that menu, set Re-Size BAR Support to Enabled (or Auto)."),
                Loc.T("Vá em Boot e confirme que CSM (Compatibility Support Module) está Disabled.", "Go to Boot and confirm that CSM (Compatibility Support Module) is Disabled."),
            },
            Vendor.Msi => new List<string>
            {
                Loc.T("Vá em Settings > Advanced > PCI Subsystem Settings e ligue Above 4G memory / Crypto Currency mining (Enabled).", "Go to Settings > Advanced > PCI Subsystem Settings and turn on Above 4G memory / Crypto Currency mining (Enabled)."),
                Loc.T("Ainda nesse menu, coloque Re-Size BAR Support em Enabled (ou Auto).", "Still in that menu, set Re-Size BAR Support to Enabled (or Auto)."),
                Loc.T("Vá em Settings > Boot e confirme que CSM está Disabled.", "Go to Settings > Boot and confirm that CSM is Disabled."),
            },
            Vendor.AsRock => new List<string>
            {
                Loc.T("Vá em Advanced > Chipset Configuration e ligue Above 4G Decoding (Enabled).", "Go to Advanced > Chipset Configuration and turn on Above 4G Decoding (Enabled)."),
                Loc.T("Ainda nesse menu, coloque Re-Size BAR Support em Enabled (ou Auto).", "Still in that menu, set Re-Size BAR Support to Enabled (or Auto)."),
                Loc.T("Vá em Boot e desligue CSM (Disabled). Em placas AMD, ligue também C.A.M. (Clever Access Memory), se existir.", "Go to Boot and turn off CSM (Disabled). On AMD boards, also turn on C.A.M. (Clever Access Memory), if it exists."),
            },
            _ => new List<string>
            {
                Loc.T("Procure Above 4G Decoding (também chamado Above 4G memory) e ligue (Enabled).", "Look for Above 4G Decoding (also called Above 4G memory) and turn it on (Enabled)."),
                Loc.T("Procure Re-Size BAR Support (ou Smart Access Memory / C.A.M.) e coloque em Enabled ou Auto.", "Look for Re-Size BAR Support (or Smart Access Memory / C.A.M.) and set it to Enabled or Auto."),
                Loc.T("Confirme que o CSM está desligado (Disabled) e que o modo de boot é UEFI.", "Confirm that CSM is off (Disabled) and that the boot mode is UEFI."),
            },
        };
        mid.Add(Loc.T("No Windows, confira o estado no WinDiag: o BAR1 deve passar a cobrir a memória de vídeo.", "In Windows, check the state in WinDiag: BAR1 should now cover the video memory."));
        return Make(BiosTopic.ReBar, v, title, mid,
            caution: Loc.T("Esse recurso exige Windows instalado em modo UEFI/GPT, placa de vídeo e processador compatíveis e, em placas mais antigas, uma BIOS atualizada. Com o Windows no modo legado, desligar o CSM pode impedir o PC de iniciar; nesse caso, volte o CSM para o valor antigo.", "This feature requires Windows installed in UEFI/GPT mode, a compatible graphics card and processor and, on older boards, an updated BIOS. With Windows in legacy mode, turning off CSM can prevent the PC from starting; in that case, set CSM back to the old value."));
    }

    /// <summary>Guia para ativar o perfil de memória XMP (Intel), DOCP ou EXPO (AMD), que faz a RAM trabalhar na velocidade anunciada.</summary>
    /// <param name="v">Fabricante.</param>
    private static BiosGuide Xmp(Vendor v)
    {
        string title = Loc.T("Ligar o perfil de memória (XMP / DOCP / EXPO)", "Turn on the memory profile (XMP / DOCP / EXPO)");
        var mid = v switch
        {
            Vendor.Gigabyte => new List<string>
            {
                Loc.T("Vá em Tweaker (em algumas versões, Settings ou M.I.T.).", "Go to Tweaker (in some versions, Settings or M.I.T.)."),
                Loc.T("Em Extreme Memory Profile (X.M.P.), escolha Profile1.", "In Extreme Memory Profile (X.M.P.), choose Profile1."),
                Loc.T("Em placas AMD, o nome pode ser DOCP ou EXPO, no mesmo menu.", "On AMD boards, the name may be DOCP or EXPO, in the same menu."),
            },
            Vendor.Asus => new List<string>
            {
                Loc.T("Vá em Ai Tweaker (ou Extreme Tweaker).", "Go to Ai Tweaker (or Extreme Tweaker)."),
                Loc.T("Em Ai Overclock Tuner, escolha XMP I (ou XMP, DOCP ou EXPO conforme a placa).", "In Ai Overclock Tuner, choose XMP I (or XMP, DOCP or EXPO depending on the board)."),
                Loc.T("Em muitas versões da BIOS, também há um botão ou aviso na tela inicial para ligar o perfil.", "In many BIOS versions, there is also a button or prompt on the home screen to turn the profile on."),
            },
            Vendor.Msi => new List<string>
            {
                Loc.T("Vá em OC (ou Overclocking).", "Go to OC (or Overclocking)."),
                Loc.T("Em Extreme Memory Profile (XMP), escolha Profile 1. Em placas AMD, o nome pode ser A-XMP, DOCP ou EXPO.", "In Extreme Memory Profile (XMP), choose Profile 1. On AMD boards, the name may be A-XMP, DOCP or EXPO."),
                Loc.T("Em algumas versões, há um botão A-XMP / EXPO na tela inicial da BIOS.", "In some versions, there is an A-XMP / EXPO button on the BIOS home screen."),
            },
            Vendor.AsRock => new List<string>
            {
                Loc.T("Vá em OC Tweaker > DRAM Configuration.", "Go to OC Tweaker > DRAM Configuration."),
                Loc.T("Em Load XMP Setting (ou Load EXPO Setting em AMD), escolha Profile 1.", "In Load XMP Setting (or Load EXPO Setting on AMD), choose Profile 1."),
            },
            _ => new List<string>
            {
                Loc.T("Procure, nos menus de overclock (Tweaker, OC, Ai Tweaker, DRAM Configuration), a opção XMP, DOCP ou EXPO.", "Look, in the overclocking menus (Tweaker, OC, Ai Tweaker, DRAM Configuration), for the XMP, DOCP or EXPO option."),
                Loc.T("Escolha o primeiro perfil (Profile 1).", "Choose the first profile (Profile 1)."),
            },
        };
        mid.Add(Loc.T("Depois do reinício, confira no WinDiag se a velocidade da memória subiu para a nominal dos pentes.", "After the restart, check in WinDiag whether the memory speed rose to the modules' rated speed."));
        return Make(BiosTopic.Xmp, v, title, mid,
            caution: Loc.T("O perfil roda a memória fora do padrão do processador, o que é aceito pelo fabricante da memória, mas não é garantido em todas as combinações. Se o PC travar ou não iniciar, volte a Auto ou use Load Optimized Defaults.", "The profile runs the memory outside the processor's default, which the memory manufacturer supports, but it is not guaranteed in every combination. If the PC freezes or does not start, go back to Auto or use Load Optimized Defaults."));
    }

    /// <summary>Guia para ligar a virtualização (Intel VT-x / AMD SVM), necessária a emuladores e máquinas virtuais.</summary>
    /// <param name="v">Fabricante.</param>
    private static BiosGuide Virtualization(Vendor v)
    {
        string title = Loc.T("Ligar a virtualização do processador (VT-x / SVM)", "Turn on processor virtualization (VT-x / SVM)");
        var mid = v switch
        {
            Vendor.Gigabyte => new List<string>
            {
                Loc.T("Em placas Intel, vá em Tweaker > Advanced CPU Settings e coloque Intel Virtualization Technology em Enabled.", "On Intel boards, go to Tweaker > Advanced CPU Settings and set Intel Virtualization Technology to Enabled."),
                Loc.T("Em placas AMD, vá em Tweaker > Advanced CPU Settings e coloque SVM Mode em Enabled.", "On AMD boards, go to Tweaker > Advanced CPU Settings and set SVM Mode to Enabled."),
            },
            Vendor.Asus => new List<string>
            {
                Loc.T("Em placas Intel, vá em Advanced > CPU Configuration e coloque Intel Virtualization Technology em Enabled.", "On Intel boards, go to Advanced > CPU Configuration and set Intel Virtualization Technology to Enabled."),
                Loc.T("Em placas AMD, vá em Advanced > CPU Configuration e coloque SVM Mode em Enabled.", "On AMD boards, go to Advanced > CPU Configuration and set SVM Mode to Enabled."),
            },
            Vendor.Msi => new List<string>
            {
                Loc.T("Em placas Intel, vá em OC > Advanced CPU Configuration e coloque Intel Virtualization Tech em Enabled.", "On Intel boards, go to OC > Advanced CPU Configuration and set Intel Virtualization Tech to Enabled."),
                Loc.T("Em placas AMD, vá em OC > Advanced CPU Configuration e coloque SVM Mode em Enabled.", "On AMD boards, go to OC > Advanced CPU Configuration and set SVM Mode to Enabled."),
            },
            Vendor.AsRock => new List<string>
            {
                Loc.T("Em placas Intel, vá em Advanced > CPU Configuration e coloque Intel Virtualization Technology em Enabled.", "On Intel boards, go to Advanced > CPU Configuration and set Intel Virtualization Technology to Enabled."),
                Loc.T("Em placas AMD, vá em Advanced > CPU Configuration e coloque SVM Mode em Enabled.", "On AMD boards, go to Advanced > CPU Configuration and set SVM Mode to Enabled."),
            },
            _ => new List<string>
            {
                Loc.T("Procure, no menu de configuração da CPU, Intel Virtualization Technology (VT-x) em placas Intel, ou SVM Mode em placas AMD.", "Look, in the CPU configuration menu, for Intel Virtualization Technology (VT-x) on Intel boards, or SVM Mode on AMD boards."),
                Loc.T("Coloque a opção em Enabled.", "Set the option to Enabled."),
            },
        };
        return Make(BiosTopic.Virtualization, v, title, mid,
            caution: Loc.T("Isso só é necessário para usar máquinas virtuais, WSL ou emuladores. Para jogar normalmente não é preciso, e ligar a virtualização pode ativar recursos de segurança do Windows que custam um pouco de desempenho.", "This is only needed to use virtual machines, WSL or emulators. It is not needed for normal gaming, and turning on virtualization can enable Windows security features that cost a bit of performance."));
    }

    /// <summary>Guia de atualização da BIOS. É o mais arriscado: enfatiza usar o arquivo do fabricante para o modelo exato e não desligar o PC durante o processo.</summary>
    /// <param name="v">Fabricante.</param>
    private static BiosGuide BiosUpdate(Vendor v)
    {
        string title = Loc.T("Atualizar a BIOS", "Update the BIOS");
        var mid = new List<string>
        {
            Loc.T("Anote o modelo exato da placa-mãe e a versão atual da BIOS (o WinDiag mostra os dois em Placa-mãe e BIOS).", "Write down the exact motherboard model and the current BIOS version (WinDiag shows both under Motherboard and BIOS)."),
            v switch
            {
                Vendor.Gigabyte => Loc.T("No site da Gigabyte, abra a página do seu modelo > Suporte > BIOS e leia as notas de cada versão.", "On the Gigabyte website, open your model's page > Support > BIOS and read the notes for each version."),
                Vendor.Asus => Loc.T("No site da ASUS, abra a página do seu modelo > Suporte > Driver e Utilitários > BIOS e Firmware.", "On the ASUS website, open your model's page > Support > Driver & Tools > BIOS & Firmware."),
                Vendor.Msi => Loc.T("No site da MSI, abra a página do seu modelo > Suporte > BIOS e leia as notas de cada versão.", "On the MSI website, open your model's page > Support > BIOS and read the notes for each version."),
                Vendor.AsRock => Loc.T("No site da ASRock, abra a página do seu modelo > Suporte > Download > BIOS.", "On the ASRock website, open your model's page > Support > Download > BIOS."),
                _ => Loc.T("No site do fabricante da placa, abra a página do seu modelo e procure a seção de suporte > BIOS.", "On the board manufacturer's website, open your model's page and look for the support > BIOS section."),
            },
            Loc.T("Baixe apenas pelo site oficial do fabricante e leia as instruções do arquivo. Atualize só se a nova versão corrigir um problema que você tem ou habilitar um recurso de que precisa.", "Download only from the manufacturer's official website and read the file's instructions. Update only if the new version fixes a problem you have or enables a feature you need."),
            v switch
            {
                Vendor.Gigabyte => Loc.T("Use a ferramenta oficial: Q-Flash, dentro da própria BIOS (tecla F8 ou menu), com o arquivo em um pen drive formatado em FAT32.", "Use the official tool: Q-Flash, inside the BIOS itself (F8 key or menu), with the file on a USB drive formatted as FAT32."),
                Vendor.Asus => Loc.T("Use a ferramenta oficial: EZ Flash, dentro da própria BIOS (tecla F7 no modo avançado, menu Tool), com o arquivo em um pen drive formatado em FAT32.", "Use the official tool: EZ Flash, inside the BIOS itself (F7 key in advanced mode, Tool menu), with the file on a USB drive formatted as FAT32."),
                Vendor.Msi => Loc.T("Use a ferramenta oficial: M-Flash, dentro da própria BIOS, com o arquivo em um pen drive formatado em FAT32.", "Use the official tool: M-Flash, inside the BIOS itself, with the file on a USB drive formatted as FAT32."),
                Vendor.AsRock => Loc.T("Use a ferramenta oficial: Instant Flash, dentro da própria BIOS, com o arquivo em um pen drive formatado em FAT32.", "Use the official tool: Instant Flash, inside the BIOS itself, with the file on a USB drive formatted as FAT32."),
                _ => Loc.T("Use a ferramenta de atualização oficial do fabricante, de preferência a que fica dentro da própria BIOS, com o arquivo em um pen drive formatado em FAT32.", "Use the manufacturer's official update tool, preferably the one inside the BIOS itself, with the file on a USB drive formatted as FAT32."),
            },
            Loc.T("Durante a atualização, não desligue nem reinicie o PC, e deixe o notebook ou o PC ligado na tomada. Ela pode levar alguns minutos e o PC pode reiniciar sozinho mais de uma vez.", "During the update, do not turn off or restart the PC, and keep the laptop or PC plugged in. It can take a few minutes and the PC may restart by itself more than once."),
            Loc.T("Depois, entre na BIOS e confira os ajustes (XMP, Resizable BAR, ordem de boot), pois a atualização costuma voltar tudo ao padrão.", "Afterwards, enter the BIOS and check the settings (XMP, Resizable BAR, boot order), since the update usually resets everything to default."),
        };
        var steps = new List<string> { Loc.T($"Reinicie o PC e aperte {EnterKey(v)} para ver a versão atual da BIOS (opcional).", $"Restart the PC and press {EnterKey(v)} to see the current BIOS version (optional).") };
        steps.AddRange(mid);
        steps.Add(Vary);
        return new BiosGuide(BiosTopic.BiosUpdate, VendorName(v), title, EnterKey(v), steps,
            Loc.T("Em geral não há como desfazer pela BIOS. Algumas placas permitem voltar a uma versão anterior pelo mesmo arquivo de atualização; confira no manual. Se o PC não iniciar depois, limpe a CMOS (bateria da placa por alguns minutos ou jumper CLR_CMOS) e, se for o caso, procure a assistência do fabricante.", "In general there is no way to undo it through the BIOS. Some boards allow going back to a previous version using the same update file; check the manual. If the PC does not start afterwards, clear the CMOS (board battery for a few minutes or CLR_CMOS jumper) and, if necessary, contact the manufacturer's support."),
            Loc.T("Nunca desligue o PC durante a atualização: uma falha de energia ou um reinício forçado pode deixar a placa sem funcionar. Use apenas a ferramenta e o arquivo oficiais do fabricante, exatamente para o seu modelo de placa.", "Never turn off the PC during the update: a power failure or forced restart can leave the board inoperable. Use only the manufacturer's official tool and file, exactly for your board model."));
    }
}
