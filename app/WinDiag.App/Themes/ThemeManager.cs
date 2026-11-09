using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinDiag.App.Themes;

/// <summary>Modo de tema escolhido pelo usuário.</summary>
public enum ThemeMode
{
    /// <summary>Acompanha o tema de aplicativos do Windows (padrão).</summary>
    Auto,
    /// <summary>Sempre claro.</summary>
    Light,
    /// <summary>Sempre escuro.</summary>
    Dark
}

/// <summary>
/// Gerencia as paletas Claro/Escuro. Os pincéis são criados em código (não congelados), registrados em
/// <see cref="Application.Resources"/> e, na troca de tema, apenas a propriedade <c>Color</c> de cada um é
/// alterada: tudo que referencia a chave (StaticResource, DynamicResource ou <c>Ui.Res</c>) atualiza ao vivo.
/// A preferência fica em %AppData%\WinDiag\settings.json.
/// </summary>
public static class ThemeManager
{
    /// <summary>Atributo DWM (Windows 10 20H1 e posteriores/11) que pede barra de título escura.</summary>
    private const int DwmUseImmersiveDarkMode = 20;
    /// <summary>Valor antigo do mesmo atributo, usado em builds anteriores do Windows 10.</summary>
    private const int DwmUseImmersiveDarkModeOld = 19;

    /// <summary>P/Invoke documentado da Win32 (dwmapi) para escurecer ou clarear a barra de título; o retorno diferente de zero indica falha.</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Chaves dos pincéis, na ordem das paletas.</summary>
    private static readonly string[] Keys =
    {
        "BgBrush", "SideBrush", "PanelBrush", "PanelAltBrush", "LineBrush", "TextBrush", "MutedBrush",
        "AccentBrush", "OkBrush", "InfoBrush", "WarnBrush", "BadBrush", "HoverBrush",
        "AccentHoverBrush", "OnAccentBrush", "BadgeTextBrush", "ChartGridBrush", "InputBrush",
        "BorderStrongBrush", "SelectedBrush", "RowAltBrush", "ScrollThumbBrush", "SwitchOffBrush",
        "KnobBrush", "OverlayBrush"
    };

    /// <summary>Cores do tema escuro, na mesma ordem de <see cref="Keys"/>; "#AARRGGBB" quando há transparência.</summary>
    private static readonly string[] DarkColors =
    {
        "#12151A", "#0F1216", "#1A1E25", "#232933", "#2D3440", "#ECEFF4", "#9AA4B2",
        "#4DA3FF", "#3DD68C", "#5AAEFF", "#F5C542", "#FF6B6B", "#2A313C",
        "#7BBBFF", "#0B1220", "#10131A", "#2A313C", "#12151A",
        "#4A5463", "#2B3A52", "#171B21", "#46505F", "#3A4250",
        "#D0D5DD", "#B3101317"
    };

    /// <summary>Cores do tema claro, na mesma ordem de <see cref="Keys"/>.</summary>
    private static readonly string[] LightColors =
    {
        "#F6F7F9", "#EEF1F5", "#FFFFFF", "#F1F3F6", "#E1E5EA", "#1B1F24", "#5B6676",
        "#1F6FEB", "#17804F", "#1F6FD1", "#9A6700", "#C62828", "#EAEEF3",
        "#1A5FCF", "#FFFFFF", "#FFFFFF", "#E1E5EA", "#FFFFFF",
        "#9AA3AF", "#DCEBFF", "#F8F9FB", "#BFC6D0", "#C5CBD3",
        "#FFFFFF", "#B3FFFFFF"
    };

    /// <summary>Pincéis vivos por chave. Ficam mutáveis (nunca congelados) para que a troca de tema altere só a cor e todos os controles atualizem ao vivo.</summary>
    private static readonly Dictionary<string, SolidColorBrush> Palette = new();
    /// <summary>Janelas registradas; referências fracas para não impedir que janelas fechadas sejam coletadas.</summary>
    private static readonly List<WeakReference<Window>> Tracked = new();
    /// <summary>Garante que <see cref="Initialize"/> rode uma única vez.</summary>
    private static bool _initialized;
    /// <summary>Cache do ícone (congelado, pode ser compartilhado entre janelas).</summary>
    private static ImageSource? _icon;

    /// <summary>Modo escolhido (Automático, Claro ou Escuro).</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;

    /// <summary>Verdadeiro quando o tema efetivo (resolvido) é o escuro.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Disparado depois que as cores mudam (modo ou tema do sistema).</summary>
    public static event Action? Changed;

    /// <summary>Ícone do aplicativo para as janelas (carregado de Assets\WinDiag.ico); null se indisponível.</summary>
    public static ImageSource? AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                var f = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WinDiag.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                f.Freeze();
                _icon = f;
            }
            catch { /* ícone é opcional */ }
            return _icon;
        }
    }

    /// <summary>
    /// Lê a preferência, cria os pincéis e aplica o tema efetivo. Deve ser chamado uma vez, antes de
    /// criar qualquer janela (evita o flash de tela branca).
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Mode = LoadMode();
        IsDark = ResolveDark(Mode);

        var res = Application.Current.Resources;
        var colors = IsDark ? DarkColors : LightColors;
        for (int i = 0; i < Keys.Length; i++)
        {
            var b = new SolidColorBrush(Parse(colors[i]));
            Palette[Keys[i]] = b;
            res[Keys[i]] = b;
        }
        res["BgColor"] = Parse(colors[0]);

        try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch { }
        Application.Current.Exit += (_, _) =>
        {
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
        };
    }

    /// <summary>Define o modo, salva a preferência e reaplica as cores.</summary>
    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        SaveMode(mode);
        Apply();
    }

    /// <summary>
    /// Prepara uma janela: fundo/texto/fonte pelo tema, ícone, e barra de título clara/escura
    /// (acompanha as trocas de tema enquanto a janela existir).
    /// </summary>
    public static void Register(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        w.UseLayoutRounding = true;
        var icon = AppIcon;
        if (icon != null) w.Icon = icon;

        lock (Tracked) Tracked.Add(new WeakReference<Window>(w));
        w.SourceInitialized += (_, _) => ApplyTitleBar(w);
        // Janela já criada (HWND existente): aplica já.
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) ApplyTitleBar(w);
    }

    /// <summary>Reaplica as cores do modo atual; se o tema efetivo não mudou, só avisa os ouvintes.</summary>
    private static void Apply()
    {
        bool dark = ResolveDark(Mode);
        if (dark == IsDark) { RaiseChanged(); return; }
        IsDark = dark;
        var colors = dark ? DarkColors : LightColors;
        var res = Application.Current.Resources;
        for (int i = 0; i < Keys.Length; i++)
        {
            var c = Parse(colors[i]);
            var b = Palette[Keys[i]];
            if (b.IsFrozen)
            {
                // Salvaguarda: um pincel congelado não aceita nova cor; troca o recurso (DynamicResource acompanha).
                b = new SolidColorBrush(c);
                Palette[Keys[i]] = b;
                res[Keys[i]] = b;
            }
            else b.Color = c;
        }
        res["BgColor"] = Parse(colors[0]);
        RaiseChanged();
    }

    /// <summary>Atualiza a barra de título das janelas vivas (descartando as coletadas) e notifica quem redesenha por conta própria.</summary>
    private static void RaiseChanged()
    {
        lock (Tracked)
        {
            Tracked.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (var r in Tracked) if (r.TryGetTarget(out var w)) ApplyTitleBar(w);
        }
        Changed?.Invoke();
    }

    /// <summary>Reage a mudanças de preferência do Windows (tema dos aplicativos) quando o modo é Automático.</summary>
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != ThemeMode.Auto) return;
        // O evento chega em outra thread: volta para a thread da interface.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ResolveDark(Mode) != IsDark) Apply();
        }));
    }

    /// <summary>Aplica barra de título clara/escura via DWM; sem HWND ainda, o <c>SourceInitialized</c> chama de novo.</summary>
    private static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int v = IsDark ? 1 : 0;
            // Windows 10 antigo usa o atributo 19; os atuais, o 20. Se ambos falharem, a barra fica padrão.
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref v, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref v, sizeof(int));
        }
        catch { /* barra de título é cosmética */ }
    }

    /// <summary>Resolve o modo para claro/escuro (Automático consulta o Windows).</summary>
    private static bool ResolveDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => SystemUsesDark()
    };

    /// <summary>Lê AppsUseLightTheme; se não conseguir ler, assume escuro (evita o susto da tela branca).</summary>
    private static bool SystemUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { }
        return true;
    }

    /// <summary>Converte "#RRGGBB" ou "#AARRGGBB" em <see cref="Color"/>.</summary>
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // ---------- Preferência ----------

    /// <summary>Lê o modo salvo; valores inválidos ou falha de leitura caem em Automático.</summary>
    private static ThemeMode LoadMode()
    {
        try
        {
            var s = Prefs.Read().Theme?.ToString();
            if (Enum.TryParse<ThemeMode>(s, true, out var m) && Enum.IsDefined(m)) return m;
        }
        catch { /* leitura falhou: usa o padrão */ }
        return ThemeMode.Auto;
    }

    /// <summary>Grava o modo; <see cref="Prefs.Update"/> não grava se o arquivo estava ilegível.</summary>
    private static void SaveMode(ThemeMode mode) => Prefs.Update(p => p.Theme = mode.ToString());
}
