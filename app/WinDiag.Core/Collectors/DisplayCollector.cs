using System.Text;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Monitores: resolução, taxa de atualização, taxas suportadas, conexão e identificação (WmiMonitorID).
/// Fontes: user32 (EnumDisplaySettings, QueryDisplayConfig) e WMI (root\wmi). O modo ativo vem de
/// EnumDisplaySettings(ENUM_CURRENT_SETTINGS), que funciona sem administrador. A taxa fracionária (ex.: 59,96 Hz)
/// vem de QueryDisplayConfig (taxa racional do caminho ativo); dmDisplayFrequency só tem valor inteiro.
/// </summary>
public sealed class DisplayCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "monitores";
    /// <inheritdoc/>
    public string Title => Loc.T("Monitores", "Displays");

    /// <summary>Dados de um monitor lidos do namespace root\wmi (EDID): nome, ano, tamanho e tecnologia de saída.</summary>
    private sealed class WmiMon
    {
        /// <summary>InstanceName do WmiMonitorID (DISPLAY\FABRICANTE\...), usado para casar com o monitor do GDI.</summary>
        public string Instance = "";
        /// <summary>Fabricante + nome amigável decodificados do EDID.</summary>
        public string Name = "";
        /// <summary>Ano de fabricação do EDID (YearOfManufacture); null se 0 ou ausente.</summary>
        public int? Year;
        /// <summary>Diagonal em polegadas, calculada das dimensões do EDID em cm (hipotenusa / 2,54). null se o EDID não informa.</summary>
        public double? Inches;
        /// <summary>Código VideoOutputTechnology de WmiMonitorConnectionParams.</summary>
        public long? Tech;
        /// <summary>true depois de associado a um monitor do GDI, para não ser reutilizado.</summary>
        public bool Used;
    }

    /// <summary>Monitor consolidado a partir do GDI, do QueryDisplayConfig e do WMI.</summary>
    private sealed class Mon
    {
        /// <summary>Monitor ativo visto pelo GDI (base da lista).</summary>
        public GdiMonitor G = null!;
        /// <summary>Dados do EDID/WMI casados com este monitor; null se não houve correspondência.</summary>
        public WmiMon? W;
        /// <summary>Caminho ativo do QueryDisplayConfig (taxa exata e tecnologia de saída).</summary>
        public ConfigPath? Cfg;
        /// <summary>Modo de vídeo em uso agora (resolução e taxa inteira).</summary>
        public GdiMode? Mode;
        /// <summary>Todos os modos que o driver oferece para este monitor.</summary>
        public List<GdiMode> Modes = new();
        /// <summary>Nome exibido, em ordem de preferência: EDID/WMI, nome amigável do caminho, descrição do GDI ou "Monitor N".</summary>
        public string Name = "";
        /// <summary>Taxa de atualização usada (a fracionária do QueryDisplayConfig quando coincide com a do GDI).</summary>
        public double Hz;
        /// <summary>Taxas distintas suportadas na resolução atual, em ordem crescente.</summary>
        public List<double> Supported = new();
        /// <summary>Maior taxa suportada na resolução atual (ou a atual, se maior).</summary>
        public double MaxHz;
    }

    /// <summary>Decodifica ManufacturerName/UserFriendlyName do WmiMonitorID (array de ushort com zeros de preenchimento).</summary>
    public static string DecodeWmi(object? arr)
    {
        if (arr is null) return "";
        var sb = new StringBuilder();
        if (arr is Array a)
        {
            foreach (var o in a)
            {
                int c;
                try { c = Convert.ToInt32(o); } catch { continue; }
                if (c != 0) sb.Append((char)c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Normaliza "\\?\DISPLAY#AOC2050#5&amp;abc#{guid}" e "DISPLAY\AOC2050\5&amp;abc_0" para a mesma forma.</summary>
    public static string NormalizeMonitorId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var s = id.Trim();
        if (s.StartsWith(@"\\?\")) s = s[4..];
        int i = s.IndexOf("#{", StringComparison.Ordinal);
        if (i >= 0) s = s[..i];
        s = s.Replace('#', '\\');
        s = Regex.Replace(s, @"_\d+$", "");
        return s.ToUpperInvariant();
    }

    /// <summary>Nome da conexão a partir do código VideoOutputTechnology (HDMI, DisplayPort...); "n/d" se desconhecido.</summary>
    public static string OutputName(long? t)
    {
        if (t is null) return Fmt.NA;
        long v = t.Value;
        if (v < 0) v += 4294967296L;
        return v switch
        {
            0 => "VGA",
            4 => "DVI",
            5 => "HDMI",
            10 => "DisplayPort",
            11 => Loc.T("DisplayPort (interno)", "DisplayPort (internal)"),
            2147483648L => Loc.T("interno", "internal"),
            _ => Loc.T("outro", "other")
        };
    }

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        var gdi = new List<GdiMonitor>();
        var paths = new List<ConfigPath>();
        var wmi = new List<WmiMon>();

        try { gdi = DisplayApi.EnumActiveMonitors(); } catch { }
        try { paths = DisplayApi.QueryActivePaths(); } catch { }
        if (gdi.Count == 0) res.Note(Loc.T("Monitores: EnumDisplayDevices não retornou monitores ativos", "Displays: EnumDisplayDevices returned no active displays"));
        if (paths.Count == 0) res.Note(Loc.T("Monitores: QueryDisplayConfig indisponível; taxa fracionária (ex.: 59,96 Hz) arredondada para inteiro", "Displays: QueryDisplayConfig unavailable; fractional refresh rate (e.g. 59.96 Hz) rounded to an integer"));

        try { wmi = ReadWmi(res); } catch (Exception ex) { res.Note(Loc.T("Monitores (WMI): ", "Displays (WMI): ") + ex.Message); }

        // Se o GDI não listou nada, usa os caminhos de QueryDisplayConfig como base mínima.
        if (gdi.Count == 0)
            gdi = paths.Select(p => new GdiMonitor(p.SourceGdiName, "", false, p.TargetDevicePath, p.FriendlyName)).ToList();

        var mons = new List<Mon>();
        foreach (var g in gdi)
        {
            var m = new Mon { G = g };
            try
            {
                var nid = NormalizeMonitorId(g.MonitorId);
                if (nid.Length > 0) m.W = wmi.FirstOrDefault(w => !w.Used && NormalizeMonitorId(w.Instance) == nid);
                if (m.W is not null) m.W.Used = true;
                m.Cfg = paths.FirstOrDefault(p => nid.Length > 0 && NormalizeMonitorId(p.TargetDevicePath) == nid)
                        ?? paths.FirstOrDefault(p => string.Equals(p.SourceGdiName, g.AdapterName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            mons.Add(m);
        }

        // Quem não casou pelo identificador: associa pela ordem e avisa.
        var unmatched = mons.Where(m => m.W is null).ToList();
        var leftover = wmi.Where(w => !w.Used).ToList();
        if (unmatched.Count > 0 && leftover.Count > 0)
        {
            for (int i = 0; i < unmatched.Count && i < leftover.Count; i++)
            {
                unmatched[i].W = leftover[i];
                leftover[i].Used = true;
            }
            res.Note(Loc.T("Monitores: não foi possível casar WMI e GDI pelo identificador; nomes associados pela ordem de enumeração", "Displays: could not match WMI and GDI by identifier; names assigned by enumeration order"));
        }

        int idx = 0;
        foreach (var m in mons)
        {
            idx++;
            try
            {
                m.Mode = DisplayApi.GetCurrentMode(m.G.AdapterName);
                m.Modes = DisplayApi.EnumModes(m.G.AdapterName);
                m.Name = m.W?.Name is { Length: > 0 } n ? n
                       : !string.IsNullOrWhiteSpace(m.Cfg?.FriendlyName) ? m.Cfg!.FriendlyName
                       : !string.IsNullOrWhiteSpace(m.G.MonitorString) ? m.G.MonitorString
                       : Loc.T("Monitor ", "Display ") + idx;
                // Usa a taxa fracionária só se coincidir (±1 Hz) com a do modo GDI; caso contrário o caminho pode ser de outro monitor/modo.
                m.Hz = m.Cfg is { Hz: > 0 } c && m.Mode is not null && Math.Abs(c.Hz - m.Mode.Hz) <= 1.0 ? c.Hz : (m.Mode?.Hz ?? 0);
                if (m.Mode is not null)
                {
                    m.Supported = m.Modes.Where(x => x.Width == m.Mode.Width && x.Height == m.Mode.Height && x.Hz > 1)
                        .Select(x => (double)x.Hz).Distinct().OrderBy(x => x).ToList();
                    m.MaxHz = Math.Max(m.Supported.Count > 0 ? m.Supported.Max() : 0, m.Hz);
                }
            }
            catch (Exception ex) { res.Note(Loc.T($"Monitor {idx}: leitura parcial ({ex.Message})", $"Display {idx}: partial reading ({ex.Message})")); }
        }

        facts.Displays = new List<DisplayInfo>();
        foreach (var m in mons)
        {
            var g = res.Group(m.Name);
            long? tech = m.W?.Tech ?? (m.Cfg is null ? null : (long?)m.Cfg.OutputTechnology);
            string conn = OutputName(tech);
            g.Add(Loc.T("Conexão", "Connection"), conn);
            g.Add(Loc.T("Dispositivo GDI", "GDI device"), m.G.AdapterName + (m.G.Primary ? Loc.T(" (principal)", " (primary)") : ""));
            if (m.Mode is not null)
            {
                g.Add(Loc.T("Modo ativo", "Active mode"), $"{m.Mode.Width}x{m.Mode.Height} @ {Fmt.Num(m.Hz)} Hz");
                g.Add(Loc.T("Taxa máxima na resolução atual", "Maximum rate at current resolution"), m.MaxHz > 0 ? $"{Fmt.Num(m.MaxHz)} Hz ({m.Mode.Width}x{m.Mode.Height})" : null);
                g.Add(Loc.T("Taxas suportadas na resolução atual", "Supported rates at current resolution"), m.Supported.Count > 0 ? string.Join(" | ", m.Supported.Select(h => Fmt.Num(h) + " Hz")) : null);
                var top = m.Modes.OrderByDescending(x => (long)x.Width * x.Height).FirstOrDefault();
                if (top is not null) g.Add(Loc.T("Maior resolução oferecida", "Highest resolution offered"), $"{top.Width}x{top.Height}");
            }
            else
            {
                g.Add(Loc.T("Modo ativo", "Active mode"), null);
                res.Note(Loc.T($"{m.Name}: modo ativo não lido (EnumDisplaySettings)", $"{m.Name}: active mode not read (EnumDisplaySettings)"));
            }
            g.Add(Loc.T("Ano de fabricação", "Year of manufacture"), m.W?.Year);
            g.Add(Loc.T("Tamanho (diagonal)", "Size (diagonal)"), m.W?.Inches is { } inch ? Fmt.Num(inch, 1) + Loc.T(" polegadas", " inches") : null);

            facts.Displays.Add(new DisplayInfo
            {
                Name = m.Name,
                DeviceName = m.G.AdapterName,
                Connection = conn,
                Width = m.Mode?.Width ?? 0,
                Height = m.Mode?.Height ?? 0,
                RefreshHz = m.Hz,
                MaxRefreshHz = m.MaxHz,
                SupportedHzAtCurrentRes = m.Supported.ToList(),
            });
        }
        facts.DisplayCount = mons.Count;

        // Findings
        var active = mons.Where(m => m.Mode is not null).ToList();
        var lowRate = active.Where(m => m.MaxHz > m.Hz + 1)
            .Select(m => Loc.T($"{m.Name}: {Fmt.Num(m.Hz)} Hz de {Fmt.Num(m.MaxHz)} Hz", $"{m.Name}: {Fmt.Num(m.Hz)} Hz of {Fmt.Num(m.MaxHz)} Hz")).ToList();
        if (lowRate.Count > 0)
            res.Find(Severity.Aviso, Loc.T("Monitor abaixo da taxa máxima", "Display below its maximum refresh rate"), string.Join("; ", lowRate),
                Loc.T("O monitor opera numa taxa menor do que a que ele suporta na mesma resolução. Em jogos isso limita a fluidez percebida e aumenta a latência de exibição.",
                    "The display runs at a lower rate than it supports at the same resolution. In games this limits perceived smoothness and increases display latency."),
                Loc.T("Ajuste manualmente em Configurações > Sistema > Tela > Vídeo avançado > Taxa de atualização.",
                    "Adjust manually in Settings > System > Display > Advanced display > Refresh rate."), "", 40,
                id: "display.below_max_rate",
                simple: Loc.T("Seu monitor está numa taxa de atualização menor do que ele aguenta. Ajustar nas configurações do Windows deixa a imagem mais fluida.",
                    "Your display is running at a lower refresh rate than it can handle. Adjusting it in Windows settings makes the image smoother."));

        if (active.Count >= 2)
        {
            var hzs = active.Select(m => m.Hz).ToList();
            if (hzs.Max() - hzs.Min() > 0.005)
                res.Find(Severity.Aviso, Loc.T("Monitores com taxas diferentes", "Displays with different refresh rates"),
                    string.Join(" + ", active.Select(m => $"{m.Mode!.Width}x{m.Mode.Height}@{Fmt.Num(m.Hz)}Hz")),
                    Loc.T("O Windows compõe a imagem de todos os monitores num mesmo ciclo. Com taxas diferentes (até 59,94 contra 60 Hz), os quadros não se alinham: aparecem micro-engasgos em jogos em janela/sem borda e em vídeo, mesmo com FPS alto.",
                        "Windows composes the image of all displays in a single cycle. With different rates (even 59.94 versus 60 Hz), frames do not line up: micro-stutters appear in windowed/borderless games and in video, even at high FPS."),
                    Loc.T("Teste jogar em tela cheia exclusiva, ou com um só monitor ativo (Win+P > Somente tela do PC). Se possível, iguale as taxas em Configurações > Sistema > Tela > Vídeo avançado.",
                        "Try playing in exclusive fullscreen, or with a single active display (Win+P > PC screen only). If possible, match the rates in Settings > System > Display > Advanced display."),
                    "", 70, id: "display.mixed_rates",
                    simple: Loc.T("Seus monitores estão em taxas de atualização diferentes, o que pode causar pequenos engasgos mesmo com bom FPS. Iguale as taxas ou jogue em tela cheia.",
                        "Your displays are running at different refresh rates, which can cause small stutters even at good FPS. Match the rates or play in fullscreen."));
            else
                res.Find(Severity.Ok, Loc.T("Taxas dos monitores", "Display refresh rates"), Loc.T("Todos na mesma taxa de atualização", "All at the same refresh rate"), id: "display.same_rate");
        }

        var low = active.FirstOrDefault(m => m.Mode!.Width > 0 && m.Mode.Width <= 1366);
        if (low is not null)
            res.Find(Severity.Info, Loc.T("Monitor de baixa resolução", "Low-resolution display"), $"{low.Mode!.Width}x{low.Mode.Height} @ {Fmt.Num(low.Hz)} Hz",
                Loc.T("Em resolução baixa a GPU trabalha pouco e o limite passa a ser a CPU. Se o jogo roda neste monitor, a CPU é mais exigida.",
                    "At low resolution the GPU does little work and the CPU becomes the limit. If the game runs on this display, the CPU is more heavily loaded."), id: "display.low_resolution");

        return res;
    }

    /// <summary>Lê WmiMonitorID, WmiMonitorBasicDisplayParams e WmiMonitorConnectionParams (root\wmi) e junta por InstanceName. Falhas viram nota; devolve o que foi lido.</summary>
    private static List<WmiMon> ReadWmi(SectionResult res)
    {
        const string ns = @"root\wmi";
        void Err(string e) => res.Note(Loc.T("Monitores (WMI): ", "Displays (WMI): ") + e);
        var ids = Wmi.Query("SELECT * FROM WmiMonitorID", ns, Err);
        var bps = Wmi.Query("SELECT * FROM WmiMonitorBasicDisplayParams", ns, Err);
        var cps = Wmi.Query("SELECT * FROM WmiMonitorConnectionParams", ns, Err);
        var list = new List<WmiMon>();
        foreach (var m in ids)
        {
            try
            {
                var inst = m.S("InstanceName") ?? "";
                var b = bps.FirstOrDefault(x => string.Equals(x.S("InstanceName"), inst, StringComparison.OrdinalIgnoreCase));
                var c = cps.FirstOrDefault(x => string.Equals(x.S("InstanceName"), inst, StringComparison.OrdinalIgnoreCase));
                double? inches = null;
                var hs = b?.D("MaxHorizontalImageSize"); var vs = b?.D("MaxVerticalImageSize");
                // Dimensões do EDID vêm em centímetros; diagonal = hipotenusa / 2,54 (cm para polegadas).
                if (hs is > 0 && vs is not null) inches = Math.Sqrt(hs.Value * hs.Value + vs.Value * vs.Value) / 2.54;
                int? year = (int?)m.L("YearOfManufacture");
                var name = $"{DecodeWmi(m.P("ManufacturerName"))} {DecodeWmi(m.P("UserFriendlyName"))}".Trim();
                list.Add(new WmiMon
                {
                    Instance = inst,
                    Name = name,
                    Year = year > 0 ? year : null,
                    Inches = inches,
                    Tech = c?.L("VideoOutputTechnology"),
                });
            }
            catch (Exception ex) { res.Note(Loc.T("Monitores (WMI): ", "Displays (WMI): ") + ex.Message); }
        }
        return list;
    }
}
