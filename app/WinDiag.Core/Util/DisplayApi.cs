using System.Runtime.InteropServices;

namespace WinDiag.Core.Util;

/// <summary>Monitor ativo visto pelo GDI (EnumDisplayDevices). MonitorId é o nome de interface (\\?\DISPLAY#...).</summary>
public sealed record GdiMonitor(string AdapterName, string AdapterString, bool Primary, string MonitorId, string MonitorString);

/// <summary>Modo de vídeo (EnumDisplaySettings). Hz é inteiro (dmDisplayFrequency).</summary>
public sealed record GdiMode(int Width, int Height, int Hz, int Bpp, int X, int Y);

/// <summary>Caminho ativo de QueryDisplayConfig: taxa exata (racional) e tecnologia de saída; não exige administrador.</summary>
public sealed record ConfigPath(string SourceGdiName, string TargetDevicePath, double Hz, uint OutputTechnology, string FriendlyName);

/// <summary>
/// P/Invoke documentado da Win32 (user32): EnumDisplayDevices, EnumDisplaySettings, ChangeDisplaySettingsEx,
/// GetDisplayConfigBufferSizes, QueryDisplayConfig e DisplayConfigGetDeviceInfo. Funciona sem administrador.
/// Todas as leituras são somente leitura e nunca lançam exceção; TrySetRefresh só é chamado pelo FixEngine.
/// </summary>
public static class DisplayApi
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    private const int DISPLAY_DEVICE_ACTIVE = 0x1;

    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const uint CDS_TEST = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint rrNumerator; public uint rrDenominator; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential)] private struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        public HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SOURCE_NAME requestPacket);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TARGET_NAME requestPacket);

    private static DISPLAY_DEVICE NewDd() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };

    private static DEVMODE NewDm() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    private static GdiMode ToMode(DEVMODE dm) => new((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel, dm.dmPositionX, dm.dmPositionY);

    /// <summary>Monitores ativos na área de trabalho (um item por par adaptador/monitor). Lista vazia em falha.</summary>
    public static List<GdiMonitor> EnumActiveMonitors()
    {
        var res = new List<GdiMonitor>();
        try
        {
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
