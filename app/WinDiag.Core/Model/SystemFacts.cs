namespace WinDiag.Core.Model;

/// <summary>
/// Fatos compartilhados entre coletores, regras e catálogo de otimizações.
/// Cada coletor preenche só as propriedades da sua área. O SystemCollector roda antes dos demais
/// (fase A), então Os/Bcd/Power já estão disponíveis para os outros coletores.
/// </summary>
public sealed class SystemFacts
{
    /// <summary>true se o WinDiag roda como administrador.</summary>
    public bool IsAdmin { get; set; }

    // --- Sistema (SystemCollector, fase A) ---
    /// <summary>Nome do Windows (Win32_OperatingSystem.Caption).</summary>
    public string? OsCaption { get; set; }
    /// <summary>Número da build do Windows (0 se não lido).</summary>
    public int OsBuild { get; set; }
    /// <summary>true se foi detectada bateria (Win32_Battery).</summary>
    public bool IsLaptop { get; set; }
    /// <summary>true se há indícios do AtlasOS (descrição do BCD, plano de energia ou pasta de módulos).</summary>
    public bool IsAtlas { get; set; }
    /// <summary>Versão do AtlasOS lida da descrição do BCD, se houver.</summary>
    public string? AtlasVersion { get; set; }
    /// <summary>Valores do "bcdedit /enum {current}", chaves em minúsculas. Vazio sem admin.</summary>
    public Dictionary<string, string> Bcd { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>true se a hibernação está habilitada (HibernateEnabled).</summary>
    public bool HibEnabled { get; set; }
    /// <summary>Suspensão seletiva de USB no plano ativo, em CA (0 = desativada, 1 = ativada); null se não lido.</summary>
    public int? UsbSuspendAC { get; set; }
    /// <summary>Nível de ASPM do PCIe no plano ativo, em CA (0 = desligado); null se não lido.</summary>
    public int? AspmAC { get; set; }

    // --- Plataforma (SystemCollector) --- usados pela "Análise da plataforma" (Rules\Insights)
    /// <summary>Fabricante e modelo da placa-mãe (Win32_BaseBoard). Null se não lido.</summary>
    public string? BoardModel { get; set; }
    /// <summary>Chipset lido do controlador LPC/eSPI via PnP (ex.: "H510"). Null se não lido; a análise também tenta deduzi-lo de <see cref="BoardModel"/>.</summary>
    public string? Chipset { get; set; }
    /// <summary>Versão da BIOS (SMBIOSBIOSVersion, ex.: "F20e").</summary>
    public string? BiosVersion { get; set; }
    /// <summary>true = boot UEFI; false = BIOS legado (CSM); null = não determinado.</summary>
    public bool? FirmwareUefi { get; set; }
    /// <summary>true/false = Secure Boot ligado/desligado; null = não aplicável ou não lido.</summary>
    public bool? SecureBoot { get; set; }

    // --- CPU / memória ---
    /// <summary>Nome comercial da CPU (Win32_Processor.Name), ex.: "Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz".</summary>
    public string? CpuName { get; set; }
    /// <summary>Velocidade configurada da RAM em MT/s (maior valor entre os pentes). Null se não lida.</summary>
    public int? RamConfiguredMTs { get; set; }
    /// <summary>Velocidade nominal dos pentes em MT/s (maior entre SMBIOS "Speed" e o part number decodificado).</summary>
    public int? RamRatedMTs { get; set; }
    /// <summary>Tipo da RAM ("DDR4", "DDR5"...). Null se não identificado.</summary>
    public string? RamType { get; set; }
    /// <summary>Quantidade de canais distintos identificados pelos nomes dos slots. Null se não identificável.</summary>
    public int? RamChannels { get; set; }
    /// <summary>true = pentes em canais distintos; false = mono canal ou pentes no mesmo canal; null = indeterminado.</summary>
    public bool? DualChannel { get; set; }
    /// <summary>Número de pentes de memória instalados.</summary>
    public int RamModules { get; set; }
    /// <summary>Núcleos físicos.</summary>
    public int CpuCores { get; set; }
    /// <summary>Threads lógicas.</summary>
    public int CpuThreads { get; set; }
    /// <summary>Clock nominal (MaxClockSpeed), em MHz.</summary>
    public int CpuBaseMHz { get; set; }
    /// <summary>RAM total visível ao Windows (TotalVisibleMemorySize), em GB. Fica abaixo da instalada (ex.: 32 GB aparecem como ~31,8).</summary>
    public double RamGB { get; set; }
    /// <summary>RAM instalada (soma de Win32_PhysicalMemory.Capacity), em GB. 0 se não lida.</summary>
    public double RamInstalledGB { get; set; }
    /// <summary>RAM para comparar com limiares (16/32 GB): a instalada, se lida; senão a visível.</summary>
    public double RamForThresholdGB => RamInstalledGB > 0 ? RamInstalledGB : RamGB;
    /// <summary>RAM disponível, em GB.</summary>
    public double AvailGB { get; set; }
    /// <summary>RAM disponível, em % do total.</summary>
    public double AvailPct { get; set; }
    /// <summary>Compressão de memória ligada (true), desligada (false) ou não determinada (null).</summary>
    public bool? MemCompression { get; set; }

    // --- GPU / vídeo ---
    /// <summary>Nome da GPU principal (preferindo a dedicada), ex.: "NVIDIA GeForce RTX 5060".</summary>
    public string? GpuName { get; set; }
    /// <summary>VRAM total em MiB (nvidia-smi ou registro). Null se não lida.</summary>
    public double? GpuVramMiB { get; set; }
    /// <summary>Geração PCIe máxima que a GPU suporta (nvidia-smi pcie.link.gen.gpumax). Null se não lida.</summary>
    public int? GpuPcieGenMax { get; set; }
    /// <summary>Geração PCIe máxima que o host (CPU/placa/BIOS) reporta (pcie.link.gen.hostmax). Null se não lida.</summary>
    public int? GpuPcieHostMax { get; set; }
    /// <summary>Geração do link PCIe negociada agora (pode cair em repouso).</summary>
    public int? GpuPcieGenCurrent { get; set; }
    /// <summary>Largura atual do link (linhas).</summary>
    public int? GpuPcieWidthCurrent { get; set; }
    /// <summary>Largura máxima reportada pelo driver (linhas).</summary>
    public int? GpuPcieWidthMax { get; set; }
    /// <summary>Tamanho do BAR1 em MiB (nvidia-smi). Null se não lido.</summary>
    public double? Bar1MiB { get; set; }
    /// <summary>true = BAR1 cobre a VRAM (Resizable BAR ativo); false = BAR1 pequeno; null = indeterminado.</summary>
    public bool? ReBarActive { get; set; }
    /// <summary>true se a GPU é de geração recente (RTX, GTX 16, RX 6/7/8, Arc).</summary>
    public bool GpuModern { get; set; }
    /// <summary>true se há GPU NVIDIA.</summary>
    public bool IsNvidia { get; set; }
    /// <summary>HwSchMode (2 = HAGS ativo); null se não definido.</summary>
    public int? HAGS { get; set; }
    /// <summary>GameDVR_Enabled do usuário; null se não definido.</summary>
    public int? GameDVR { get; set; }
    /// <summary>OverlayTestMode do DWM (5 = MPO desativado); null se não definido.</summary>
    public int? MPO { get; set; }
    /// <summary>true se AutoGameModeEnabled = 0.</summary>
    public bool GameModeOff { get; set; }
    /// <summary>Valor de SwapEffectUpgradeEnable em DirectXUserGlobalSettings ("1", "0" ou null).</summary>
    public string? WindowedOpt { get; set; }
    /// <summary>Quantidade de monitores ativos.</summary>
    public int DisplayCount { get; set; }
    /// <summary>Monitores ativos.</summary>
    public List<DisplayInfo> Displays { get; set; } = new();

    /// <summary>Limite de potência máximo da GPU em watts (nvidia-smi power.max_limit ou power.limit). Null se não lido.</summary>
    public double? GpuPowerLimitW { get; set; }
    /// <summary>TDP da CPU em watts, pela tabela interna. Null se o modelo é desconhecido.</summary>
    public int? CpuTdpW { get; set; }
    /// <summary>Potência da fonte (PSU) em watts, informada pelo usuário em Configurações. Null se não informada.</summary>
    public int? PsuWatts { get; set; }
    /// <summary>Modelo da fonte, informado pelo usuário. Null se não informado.</summary>
    public string? PsuModel { get; set; }

    /// <summary>Capacidade da fonte (W) deduzida do modelo de uma fonte digital detectada pelos sensores (ex.: "HX850i" = 850). Null se não detectada ou sem potência no nome.</summary>
    public int? PsuDetectedWatts { get; set; }

    /// <summary>Fontes de sensores que trouxeram leituras, em chave estável (ordenada, separada por "+"; "none" se nenhuma). Null se a seção de sensores não rodou. Usada pelo histórico para não comparar a seção "sensores" entre fontes diferentes.</summary>
    public string? SensorSources { get; set; }
    /// <summary>Modelo de uma fonte digital detectada pelos sensores (Corsair, NZXT etc.). Null se não detectada.</summary>
    public string? PsuDetectedModel { get; set; }

    // --- Sensores (SensorsCollector) ---
    /// <summary>Maior temperatura de CPU lida pelos sensores, em °C (leitura única). Null se nenhum sensor de CPU respondeu.</summary>
    public double? CpuTempC { get; set; }
    /// <summary>Maior temperatura do núcleo da GPU lida pelos sensores, em °C (leitura única, sem o hot spot). Null se não lida.</summary>
    public double? GpuTempC { get; set; }

    // --- Armazenamento ---
    /// <summary>true se a unidade C: está em SSD.</summary>
    public bool IsSystemSSD { get; set; }
    /// <summary>Quantidade de discos físicos HDD (0 se não lido).</summary>
    public int HddCount { get; set; }
    /// <summary>Quantidade de discos físicos que não são HDD (SSD/NVMe/indefinido). 0 se não lido.</summary>
    public int SsdCount { get; set; }
    /// <summary>true se alguma biblioteca Steam está em HDD.</summary>
    public bool SteamOnHdd { get; set; }

    // --- Segurança / serviços ---
    /// <summary>true se o hypervisor está em execução.</summary>
    public bool HypervisorPresent { get; set; }
    /// <summary>true se o BCD tem hypervisorlaunchtype = Off.</summary>
    public bool HypervisorBcdOff => Bcd.TryGetValue("hypervisorlaunchtype", out var v) && v.Equals("Off", StringComparison.OrdinalIgnoreCase);
    /// <summary>true se a Integridade de Memória (HVCI) está em execução.</summary>
    public bool HvciRunning { get; set; }
    /// <summary>Aplicativos/serviços que mantêm o hypervisor em uso (WSL, Hyper-V...).</summary>
    public List<string> HypervisorOwners { get; set; } = new();
    /// <summary>Tipo de inicialização do SysMain ("Automatic", "Disabled"...).</summary>
    public string? SysMainStart { get; set; }
    /// <summary>Tipo de inicialização do Windows Search.</summary>
    public string? WSearchStart { get; set; }
    /// <summary>Tamanho dos temporários (usuário + Windows), em MB.</summary>
    public double TempMB { get; set; }

    // --- Processos / inicialização ---
    /// <summary>Itens de inicialização lidos.</summary>
    public List<StartupItem> StartupItems { get; set; } = new();
}

/// <summary>Monitor ativo (DisplayCollector). Usado também pelo "Igualar taxa dos monitores".</summary>
public sealed class DisplayInfo
{
    /// <summary>Nome do monitor.</summary>
    public string Name { get; set; } = "";
    /// <summary>Nome de dispositivo GDI (ex.: \\.\DISPLAY1), necessário para ChangeDisplaySettingsEx.</summary>
    public string DeviceName { get; set; } = "";
    /// <summary>Tipo de conexão (HDMI, DisplayPort...).</summary>
    public string Connection { get; set; } = "";
    /// <summary>Largura em pixels.</summary>
    public int Width { get; set; }
    /// <summary>Altura em pixels.</summary>
    public int Height { get; set; }
    /// <summary>Taxa de atualização atual, em Hz.</summary>
    public double RefreshHz { get; set; }
    /// <summary>Maior taxa suportada na resolução atual, em Hz.</summary>
    public double MaxRefreshHz { get; set; }
    /// <summary>Taxas suportadas na resolução atual.</summary>
    public List<double> SupportedHzAtCurrentRes { get; set; } = new();
}

/// <summary>Item de inicialização (Run/Run32/pasta Inicializar + StartupApproved).</summary>
public sealed class StartupItem
{
    /// <summary>Nome do monitor.</summary>
    public string Name { get; set; } = "";
    /// <summary>Comando executado na inicialização.</summary>
    public string Command { get; set; } = "";
    /// <summary>Ex.: "HKCU Run", "HKLM Run32", "Pasta Inicializar".</summary>
    public string Location { get; set; } = "";
    /// <summary>true se o item está habilitado.</summary>
    public bool Enabled { get; set; }
    /// <summary>Chave StartupApproved correspondente (formato "HKCU:\..."), se editável.</summary>
    public string? ApprovedPath { get; set; }
    /// <summary>Nome do valor em StartupApproved.</summary>
    public string? ValueName { get; set; }
}
