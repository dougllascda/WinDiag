namespace WinDiag.Core.Model;

/// <summary>Veredito da lista de programas de fábrica (veja <c>WinDiag.Core.Work.BloatwareDb</c>).</summary>
public enum BloatVerdict
{
    /// <summary>Costuma ser promoção, teste ou jogo casual instalado de fábrica: pode ser removido.</summary>
    Remove,
    /// <summary>Redundante ou dispensável para a maioria dos usuários; a decisão é do usuário.</summary>
    Optional,
    /// <summary>Driver, painel ou ferramenta de suporte/BIOS do fabricante: manter.</summary>
    Keep
}

/// <summary>Impressora instalada (Win32_Printer) com o resumo da fila (Win32_PrintJob).</summary>
public sealed class PrinterInfo
{
    /// <summary>Nome da impressora.</summary>
    public string Name { get; set; } = "";
    /// <summary>true se é a impressora padrão.</summary>
    public bool IsDefault { get; set; }
    /// <summary>true se é uma impressora de rede.</summary>
    public bool IsNetwork { get; set; }
    /// <summary>Estado em texto (PrinterStatus/DetectedErrorState/WorkOffline), no idioma atual.</summary>
    public string StatusText { get; set; } = "";
    /// <summary>true se está offline.</summary>
    public bool Offline { get; set; }
    /// <summary>true se o Windows reporta erro (papel, toner, porta etc.).</summary>
    public bool HasError { get; set; }
    /// <summary>Quantidade de trabalhos na fila.</summary>
    public int JobCount { get; set; }
    /// <summary>Trabalhos na fila há mais de 10 minutos (considerados presos).</summary>
    public int StuckJobs { get; set; }
}

/// <summary>Programa instalado classificado pela lista de programas de fábrica.</summary>
public sealed class BloatItem
{
    /// <summary>Nome exibido do programa.</summary>
    public string Name { get; set; } = "";
    /// <summary>Fabricante/publicador (pode ser vazio).</summary>
    public string Publisher { get; set; } = "";
    /// <summary>Versão (pode ser vazio).</summary>
    public string Version { get; set; } = "";
    /// <summary>Recomendação.</summary>
    public BloatVerdict Verdict { get; set; }
    /// <summary>Motivo da recomendação, no idioma atual.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Comando de desinstalação interativo (UninstallString); vazio para pacotes Appx.</summary>
    public string UninstallCommand { get; set; } = "";
    /// <summary>true se é um pacote Appx/MSIX (Loja).</summary>
    public bool IsAppx { get; set; }
    /// <summary>PackageFullName de um pacote Appx; vazio nos demais.</summary>
    public string PackageFullName { get; set; } = "";
    /// <summary>true se o item veio do registro do usuário (HKCU). O WinDiag roda elevado e não lança desinstaladores de HKCU (rodariam como administrador); a interface deve orientar a abrir Configurações &gt; Aplicativos.</summary>
    public bool FromUserHive { get; set; }
}
