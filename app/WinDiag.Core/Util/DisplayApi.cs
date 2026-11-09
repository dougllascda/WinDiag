using System.Runtime.InteropServices;

namespace WinDiag.Core.Util;

/// <summary>Monitor ativo visto pelo GDI (EnumDisplayDevices). MonitorId é o nome de interface (\\?\DISPLAY#...).</summary>
/// <param name="AdapterName">Nome GDI do adaptador (\\.\DISPLAYn).</param>
/// <param name="AdapterString">Descrição do adaptador (GPU).</param>
/// <param name="Primary">true se é o monitor principal.</param>
/// <param name="MonitorId">Caminho de interface do monitor; vazio se o adaptador não tem monitor ativo.</param>
/// <param name="MonitorString">Descrição do monitor segundo o driver.</param>
public sealed record GdiMonitor(string AdapterName, string AdapterString, bool Primary, string MonitorId, string MonitorString);

/// <summary>Modo de vídeo (EnumDisplaySettings). Hz é inteiro (dmDisplayFrequency).</summary>
/// <param name="Width">Largura em pixels.</param>
/// <param name="Height">Altura em pixels.</param>
/// <param name="Hz">Taxa de atualização em Hz (inteira).</param>
/// <param name="Bpp">Bits por pixel.</param>
/// <param name="X">Posição X na área de trabalho virtual.</param>
/// <param name="Y">Posição Y na área de trabalho virtual.</param>
public sealed record GdiMode(int Width, int Height, int Hz, int Bpp, int X, int Y);

/// <summary>Caminho ativo de QueryDisplayConfig: taxa exata (racional) e tecnologia de saída; não exige administrador.</summary>
/// <param name="SourceGdiName">Nome GDI da origem (\\.\DISPLAYn), para cruzar com <see cref="GdiMonitor"/>.</param>
/// <param name="TargetDevicePath">Caminho de interface do monitor.</param>
/// <param name="Hz">Taxa exata (numerador/denominador), 0 se desconhecida.</param>
/// <param name="OutputTechnology">Valor de VIDEO_OUTPUT_TECHNOLOGY (HDMI, DisplayPort, interna...).</param>
/// <param name="FriendlyName">Nome amigável do monitor vindo do EDID.</param>
public sealed record ConfigPath(string SourceGdiName, string TargetDevicePath, double Hz, uint OutputTechnology, string FriendlyName);

/// <summary>
/// P/Invoke documentado da Win32 (user32): EnumDisplayDevices, EnumDisplaySettings, ChangeDisplaySettingsEx,
/// GetDisplayConfigBufferSizes, QueryDisplayConfig e DisplayConfigGetDeviceInfo. Funciona sem administrador.
/// Todas as leituras são somente leitura e nunca lançam exceção; TrySetRefresh só é chamado pelo FixEngine.
/// </summary>
public static class DisplayApi
{
    /// <summary>Índice especial de EnumDisplaySettings: pede o modo em uso agora, e não um modo da lista do driver.</summary>
    private const int ENUM_CURRENT_SETTINGS = -1;
    /// <summary>Flag de EnumDisplayDevices: devolve em DeviceID o caminho de interface do monitor (\\?\DISPLAY#...), que casa com o da WMI e do QueryDisplayConfig.</summary>
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    /// <summary>StateFlags do adaptador: faz parte da área de trabalho do Windows (exclui saídas desligadas).</summary>
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    /// <summary>StateFlags do adaptador: é o monitor principal.</summary>
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    /// <summary>StateFlags do adaptador: driver de espelhamento virtual (ex.: remoto/captura), ignorado na listagem.</summary>
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    /// <summary>StateFlags do monitor: monitor ativo (as entradas inativas são ignoradas).</summary>
    private const int DISPLAY_DEVICE_ACTIVE = 0x1;

    /// <summary>Bit de DEVMODE.dmFields: dmPelsWidth é válido.</summary>
    private const uint DM_PELSWIDTH = 0x80000;
    /// <summary>Bit de DEVMODE.dmFields: dmPelsHeight é válido.</summary>
    private const uint DM_PELSHEIGHT = 0x100000;
    /// <summary>Bit de DEVMODE.dmFields: dmDisplayFrequency é válido.</summary>
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    /// <summary>Flag de ChangeDisplaySettingsEx: aplica e grava o modo no registro (persistente).</summary>
    private const uint CDS_UPDATEREGISTRY = 0x1;
    /// <summary>Flag de ChangeDisplaySettingsEx: só testa se o modo é possível, sem aplicar.</summary>
    private const uint CDS_TEST = 0x2;

    /// <summary>Espelho da estrutura DISPLAY_DEVICEW da Win32 (adaptador ou monitor).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        /// <summary>Tamanho da estrutura em bytes; deve ser preenchido antes da chamada.</summary>
        public int cb;
        /// <summary>Nome do dispositivo GDI (ex.: \\.\DISPLAY1).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        /// <summary>Descrição legível (modelo do adaptador ou do monitor).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        /// <summary>Combinação de flags DISPLAY_DEVICE_*.</summary>
        public int StateFlags;
        /// <summary>ID de hardware, ou caminho de interface quando pedido com EDD_GET_DEVICE_INTERFACE_NAME.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        /// <summary>Chave de registro do dispositivo (não usada).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>Espelho da estrutura DEVMODEW da Win32 (variante de vídeo da união de posição/orientação). Só os campos de modo gráfico são lidos.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        /// <summary>Nome do dispositivo.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        /// <summary>Versão da especificação DEVMODE.</summary>
        public ushort dmSpecVersion;
        /// <summary>Versão do driver.</summary>
        public ushort dmDriverVersion;
        /// <summary>Tamanho desta estrutura; o Windows recusa a chamada se estiver errado.</summary>
        public ushort dmSize;
        /// <summary>Bytes privados do driver após a estrutura.</summary>
        public ushort dmDriverExtra;
        /// <summary>Máscara DM_* dos campos válidos.</summary>
        public uint dmFields;
        /// <summary>Posição X do monitor na área de trabalho virtual (px).</summary>
        public int dmPositionX;
        /// <summary>Posição Y do monitor na área de trabalho virtual (px).</summary>
        public int dmPositionY;
        /// <summary>Rotação da tela.</summary>
        public uint dmDisplayOrientation;
        /// <summary>Modo de saída fixa (escala/centralizado).</summary>
        public uint dmDisplayFixedOutput;
        /// <summary>Campo de impressora, sem uso em vídeo (mantido pelo layout).</summary>
        public short dmColor;
        /// <summary>Campo de impressora, sem uso em vídeo (mantido pelo layout).</summary>
        public short dmDuplex;
        /// <summary>Campo de impressora, sem uso em vídeo (mantido pelo layout).</summary>
        public short dmYResolution;
        /// <summary>Campo de impressora, sem uso em vídeo (mantido pelo layout).</summary>
        public short dmTTOption;
        /// <summary>Campo de impressora, sem uso em vídeo (mantido pelo layout).</summary>
        public short dmCollate;
        /// <summary>Nome do formulário (impressora), sem uso em vídeo.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        /// <summary>Pixels lógicos por polegada (sem uso aqui).</summary>
        public ushort dmLogPixels;
        /// <summary>Bits por pixel do modo.</summary>
        public uint dmBitsPerPel;
        /// <summary>Largura do modo em pixels.</summary>
        public uint dmPelsWidth;
        /// <summary>Altura do modo em pixels.</summary>
        public uint dmPelsHeight;
        /// <summary>Flags de exibição (entrelaçado etc.).</summary>
        public uint dmDisplayFlags;
        /// <summary>Taxa de atualização em Hz, inteira (o valor exato fracionário vem do QueryDisplayConfig).</summary>
        public uint dmDisplayFrequency;
        /// <summary>Campo de ICM, não usado.</summary>
        public uint dmICMMethod;
        /// <summary>Campo de ICM, não usado.</summary>
        public uint dmICMIntent;
        /// <summary>Campo de impressora, não usado.</summary>
        public uint dmMediaType;
        /// <summary>Campo de impressora, não usado.</summary>
        public uint dmDitherType;
        /// <summary>Reservado.</summary>
        public uint dmReserved1;
        /// <summary>Reservado.</summary>
        public uint dmReserved2;
        /// <summary>Largura de panning (não usada).</summary>
        public uint dmPanningWidth;
        /// <summary>Altura de panning (não usada).</summary>
        public uint dmPanningHeight;
    }

    /// <summary>LUID de adaptador gráfico (identificador local único).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        /// <summary>Parte baixa do LUID.</summary>
        public uint Low;
        /// <summary>Parte alta do LUID.</summary>
        public int High;
    }

    /// <summary>Espelho de DISPLAYCONFIG_PATH_SOURCE_INFO: origem (adaptador/fonte) do caminho.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE
    {
        /// <summary>Adaptador da origem.</summary>
        public LUID adapterId;
        /// <summary>ID da origem no adaptador.</summary>
        public uint id;
        /// <summary>Índice no vetor de modos.</summary>
        public uint modeInfoIdx;
        /// <summary>Flags de estado.</summary>
        public uint statusFlags;
    }

    /// <summary>Espelho de DISPLAYCONFIG_PATH_TARGET_INFO: saída (monitor) do caminho, com a taxa de atualização racional.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        /// <summary>Adaptador da saída.</summary>
        public LUID adapterId;
        /// <summary>ID do alvo no adaptador.</summary>
        public uint id;
        /// <summary>Índice no vetor de modos.</summary>
        public uint modeInfoIdx;
        /// <summary>Tecnologia da saída (HDMI, DisplayPort, interna etc.).</summary>
        public uint outputTechnology;
        /// <summary>Rotação.</summary>
        public uint rotation;
        /// <summary>Escalonamento.</summary>
        public uint scaling;
        /// <summary>Numerador da taxa de atualização.</summary>
        public uint rrNumerator;
        /// <summary>Denominador da taxa de atualização (0 = desconhecida).</summary>
        public uint rrDenominator;
        /// <summary>Ordem de varredura (progressivo/entrelaçado).</summary>
        public uint scanLineOrdering;
        /// <summary>Não zero se o monitor está disponível.</summary>
        public int targetAvailable;
        /// <summary>Flags de estado.</summary>
        public uint statusFlags;
    }

    /// <summary>Espelho de DISPLAYCONFIG_PATH_INFO: um caminho origem para destino.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        /// <summary>Origem do caminho.</summary>
        public PATH_SOURCE sourceInfo;
        /// <summary>Destino do caminho.</summary>
        public PATH_TARGET targetInfo;
        /// <summary>Flags do caminho.</summary>
        public uint flags;
    }

    /// <summary>Espelho de DISPLAYCONFIG_MODE_INFO. O conteúdo da união não é lido, então basta reservar os 64 bytes do tamanho real da estrutura.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO
    {
        /// <summary>Tipo da informação (origem, destino ou imagem de desktop).</summary>
        public uint infoType;
        /// <summary>ID do alvo/origem.</summary>
        public uint id;
        /// <summary>Adaptador.</summary>
        public LUID adapterId;
    }

    /// <summary>Espelho de DISPLAYCONFIG_DEVICE_INFO_HEADER, cabeçalho comum dos pedidos de DisplayConfigGetDeviceInfo.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER
    {
        /// <summary>Tipo do pedido (1 = nome da origem, 2 = nome do alvo).</summary>
        public uint type;
        /// <summary>Tamanho total do pacote em bytes.</summary>
        public uint size;
        /// <summary>Adaptador consultado.</summary>
        public LUID adapterId;
        /// <summary>ID da origem ou do alvo.</summary>
        public uint id;
    }

    /// <summary>Pedido DISPLAYCONFIG_SOURCE_DEVICE_NAME: traduz a origem para o nome GDI (\\.\DISPLAYn).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        /// <summary>Cabeçalho do pedido.</summary>
        public HEADER header;
        /// <summary>Nome GDI devolvido.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    /// <summary>Pedido DISPLAYCONFIG_TARGET_DEVICE_NAME: nome amigável e caminho de interface do monitor (vem do EDID).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        /// <summary>Cabeçalho do pedido.</summary>
        public HEADER header;
        /// <summary>Flags de disponibilidade das informações.</summary>
        public uint flags;
        /// <summary>Tecnologia da saída (VIDEO_OUTPUT_TECHNOLOGY).</summary>
        public uint outputTechnology;
        /// <summary>Código do fabricante no EDID.</summary>
        public ushort edidManufactureId;
        /// <summary>Código do produto no EDID.</summary>
        public ushort edidProductCodeId;
        /// <summary>Instância do conector no adaptador.</summary>
        public uint connectorInstance;
        /// <summary>Nome amigável do monitor (descritor do EDID).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        /// <summary>Caminho de interface do monitor (\\?\DISPLAY#...).</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    /// <summary>user32!EnumDisplayDevicesW: enumera adaptadores (lpDevice nulo) ou os monitores de um adaptador.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    /// <summary>user32!EnumDisplaySettingsW: lê um modo de vídeo (ou o atual com ENUM_CURRENT_SETTINGS). Devolve false quando acabam os modos.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    /// <summary>user32!ChangeDisplaySettingsExW: aplica ou testa (CDS_TEST) um modo. Devolve DISP_CHANGE_* (0 = sucesso).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    /// <summary>user32!GetDisplayConfigBufferSizes: informa quantos caminhos e modos o QueryDisplayConfig vai devolver. 0 = sucesso.</summary>
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    /// <summary>user32!QueryDisplayConfig: preenche os vetores de caminhos e modos. 0 = sucesso; 122 = buffer pequeno (a configuração mudou entre as chamadas).</summary>
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    /// <summary>user32!DisplayConfigGetDeviceInfo com o pacote de nome da origem (declarado duas vezes para tipar cada pacote sem copiar memória).</summary>
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SOURCE_NAME requestPacket);
    /// <summary>user32!DisplayConfigGetDeviceInfo com o pacote de nome do alvo (monitor).</summary>
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TARGET_NAME requestPacket);

    /// <summary>Cria um DISPLAY_DEVICE com cb e strings inicializados, como a API exige.</summary>
    private static DISPLAY_DEVICE NewDd() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };

    /// <summary>Cria um DEVMODE com dmSize correto (a API recusa se o tamanho não bater).</summary>
    private static DEVMODE NewDm() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    /// <summary>Converte o DEVMODE nativo no registro gerenciado <see cref="GdiMode"/>.</summary>
    private static GdiMode ToMode(DEVMODE dm) => new((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel, dm.dmPositionX, dm.dmPositionY);

    /// <summary>Monitores ativos na área de trabalho (um item por par adaptador/monitor). Lista vazia em falha.</summary>
    public static List<GdiMonitor> EnumActiveMonitors()
    {
        var res = new List<GdiMonitor>();
        try
        {
            // Limites defensivos (64 adaptadores, 16 monitores por adaptador) evitam laço infinito se a API se comportar mal.
            for (uint i = 0; i < 64; i++)
            {
                var dd = NewDd();
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                if ((dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
                bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                bool any = false;
                for (uint j = 0; j < 16; j++)
                {
                    var mon = NewDd();
                    if (!EnumDisplayDevices(dd.DeviceName, j, ref mon, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                    if ((mon.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0) continue;
                    res.Add(new GdiMonitor(dd.DeviceName, dd.DeviceString, primary, mon.DeviceID, mon.DeviceString));
                    any = true;
                }
                // Adaptador ligado à área de trabalho sem monitor ativo enumerado: ainda aparece, com monitor vazio.
                if (!any) res.Add(new GdiMonitor(dd.DeviceName, dd.DeviceString, primary, "", ""));
            }
        }
        catch { }
        return res;
    }

    /// <summary>Modo atual (ENUM_CURRENT_SETTINGS) do dispositivo GDI, ex.: \\.\DISPLAY1. Null em falha.</summary>
    public static GdiMode? GetCurrentMode(string deviceName)
    {
        try
        {
            var dm = NewDm();
            return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? ToMode(dm) : null;
        }
        catch { return null; }
    }

    /// <summary>Todos os modos que o driver oferece para o dispositivo (sem EDS_RAWMODE: só os suportados pelo monitor).</summary>
    public static List<GdiMode> EnumModes(string deviceName)
    {
        var res = new List<GdiMode>();
        try
        {
            // Os índices crescem até EnumDisplaySettings devolver false; 4096 é só um teto de segurança.
            for (int i = 0; i < 4096; i++)
            {
                var dm = NewDm();
                if (!EnumDisplaySettings(deviceName, i, ref dm)) break;
                res.Add(ToMode(dm));
            }
        }
        catch { }
        return res;
    }

    /// <summary>Caminhos ativos (QDC_ONLY_ACTIVE_PATHS) com taxa de atualização exata. Lista vazia em falha.</summary>
    public static List<ConfigPath> QueryActivePaths()
    {
        var res = new List<ConfigPath>();
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
            PATH_INFO[] paths = Array.Empty<PATH_INFO>();
            MODE_INFO[] modes = Array.Empty<MODE_INFO>();
            uint np = 0, nm = 0;
            // 122 = ERROR_INSUFFICIENT_BUFFER: o Windows recomenda repetir se a topologia mudar entre o dimensionamento e a consulta.
            int rc = 122;
            for (int attempt = 0; attempt < 3 && rc == 122; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return res;
                paths = new PATH_INFO[np];
                modes = new MODE_INFO[nm];
                rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            }
            if (rc != 0) return res;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new SOURCE_NAME { header = new HEADER { type = 1, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id }, viewGdiDeviceName = "" };
                var tgt = new TARGET_NAME { header = new HEADER { type = 2, size = (uint)Marshal.SizeOf<TARGET_NAME>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id }, monitorFriendlyDeviceName = "", monitorDevicePath = "" };
                string gdi = GetSourceName(ref src) == 0 ? src.viewGdiDeviceName : "";
                string path = "", friendly = "";
                if (GetTargetName(ref tgt) == 0) { path = tgt.monitorDevicePath; friendly = tgt.monitorFriendlyDeviceName; }
                // Taxa exata = numerador/denominador (ex.: 143,98 Hz); o inteiro de EnumDisplaySettings arredonda e pode esconder 59,94 x 60.
                double hz = p.targetInfo.rrDenominator > 0 ? Math.Round((double)p.targetInfo.rrNumerator / p.targetInfo.rrDenominator, 2) : 0;
                res.Add(new ConfigPath(gdi, path, hz, p.targetInfo.outputTechnology, friendly));
            }
        }
        catch { }
        return res;
    }

    /// <summary>
    /// Troca resolução/taxa de um dispositivo GDI. Valida com CDS_TEST e então grava com CDS_UPDATEREGISTRY.
    /// Ainda não é chamado por nenhum coletor; reservado para "Igualar taxa dos monitores" (via FixEngine, com snapshot).
    /// </summary>
    public static bool TrySetRefresh(string deviceName, int width, int height, int hz, out string erro)
    {
        erro = "";
        try
        {
            var dm = NewDm();
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) { erro = Loc.T("não foi possível ler o modo atual de ", "could not read the current mode of ") + deviceName; return false; }
            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            // Primeiro valida com CDS_TEST: só grava no registro se o driver aceitar o modo.
            int t = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (t != 0) { erro = Describe(t); return false; }
            int r = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (r != 0) { erro = Describe(r); return false; }
            return true;
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            return false;
        }
    }

    /// <summary>Traduz o código DISP_CHANGE_* de ChangeDisplaySettingsEx em mensagem para o usuário.</summary>
    private static string Describe(int code) => code switch
    {
        1 => Loc.T("reinicialização necessária (DISP_CHANGE_RESTART)", "restart required (DISP_CHANGE_RESTART)"),
        -1 => Loc.T("falha ao trocar o modo (DISP_CHANGE_FAILED)", "failed to change the mode (DISP_CHANGE_FAILED)"),
        -2 => Loc.T("modo não suportado (DISP_CHANGE_BADMODE)", "mode not supported (DISP_CHANGE_BADMODE)"),
        -3 => Loc.T("não foi possível gravar no registro (DISP_CHANGE_NOTUPDATED)", "could not write to the registry (DISP_CHANGE_NOTUPDATED)"),
        -4 => Loc.T("parâmetros inválidos (DISP_CHANGE_BADFLAGS)", "invalid parameters (DISP_CHANGE_BADFLAGS)"),
        -5 => Loc.T("parâmetros inválidos (DISP_CHANGE_BADPARAM)", "invalid parameters (DISP_CHANGE_BADPARAM)"),
        -6 => Loc.T("sessão sem acesso ao monitor (DISP_CHANGE_BADDUALVIEW)", "session without access to the display (DISP_CHANGE_BADDUALVIEW)"),
        _ => Loc.T("código ", "code ") + code
    };
}
