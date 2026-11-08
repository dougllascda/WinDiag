using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using WinDiag.App.Themes;
using WinDiag.Core;
using WinDiag.Core.Sensors;

namespace WinDiag.App.Views;

/// <summary>Aba "Configurações": aparência, modo, idioma, fonte (PSU), sensores, dados e sobre.</summary>
public sealed class SettingsView : UserControl
{
    private static readonly string[] ThemeLabels = { Loc.T("Automático", "Automatic"), Loc.T("Claro", "Light"), Loc.T("Escuro", "Dark") };
    private static readonly string[] ModeLabels = { Loc.T("Simples", "Simple"), Loc.T("Detalhado", "Detailed") };
    private static readonly string[] LangLabels = { Loc.T("Automático", "Automatic"), "Português", "English" };
    private static readonly string[] LangCodes = { "Auto", "pt-BR", "en" };

    private readonly Func<Task> _rescan;
    private readonly Segmented _theme;
    private readonly Segmented _mode;
    private readonly TextBox _watts = new() { Width = 90, MaxLength = 4, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _model = new() { Width = 260, MaxLength = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _psuMsg = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _dataMsg = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static string HistoryDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDiag", "history");

    private readonly Func<bool, Task<SensorStatus?>>? _sensorStatus;
    private readonly StackPanel _sensorBox = new();
    private Button? _sensorRecheck;
    private bool _sensorsChecked;
    private bool _sensorsBusy;

    /// <summary>Cria a aba; <paramref name="rescan"/> refaz a varredura (usado ao salvar a fonte); <paramref name="sensorStatus"/> lê o estado dos sensores (true = detectar de novo).</summary>
    public SettingsView(Func<Task> rescan, Func<bool, Task<SensorStatus?>>? sensorStatus = null)
    {
        _rescan = rescan;
        _sensorStatus = sensorStatus;
        var prefs = Prefs.Read();
        var root = new StackPanel();

        // Aparência
        _theme = new Segmented(ThemeLabels, (int)ThemeManager.Mode, i => ThemeManager.SetMode((ThemeMode)i));
        ThemeManager.Changed += () => _theme.Select((int)ThemeManager.Mode);
        root.Children.Add(Block("Aparência", "Appearance", Labeled(Loc.T("Tema", "Theme"), _theme)));

        // Modo
        _mode = new Segmented(ModeLabels, Ui.Simple ? 0 : 1, i => Ui.SetSimple(i == 0));
        Ui.ModeChanged += () => _mode.Select(Ui.Simple ? 0 : 1);
        var modeBody = new StackPanel();
        modeBody.Children.Add(Labeled(Loc.T("Modo de exibição", "Display mode"), _mode));
        modeBody.Children.Add(Ui.Muted(Loc.T("Simples mostra frases curtas e só as abas principais; Detalhado mostra todas as abas e os detalhes técnicos.", "Simple shows short sentences and only the main tabs; Detailed shows all tabs and the technical details.")));
        root.Children.Add(Block("Modo", "Mode", modeBody));

        // Idioma
        int lang = Math.Max(0, Array.FindIndex(LangCodes, c => string.Equals(c, prefs.Language, StringComparison.OrdinalIgnoreCase)));
        var langBody = new StackPanel();
        _langCode = LangCodes[lang];
        langBody.Children.Add(Labeled(Loc.T("Idioma", "Language"), new Segmented(LangLabels, lang, i => OnLanguagePicked(LangCodes[i]))));
        langBody.Children.Add(Ui.Muted(Loc.T("Aplicado ao reiniciar o aplicativo.", "Applied when the application restarts.")));
        root.Children.Add(Block("Idioma", "Language", langBody));

        // Minha fonte
        _watts.Text = prefs.PsuWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _model.Text = prefs.PsuModel ?? "";
        _watts.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(_watts, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string s || !s.All(char.IsDigit)) e.CancelCommand();
        });
        var save = new Button { Content = Loc.T("Salvar", "Save"), Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        save.Click += async (_, _) => await SavePsuAsync(save);
        var psu = new StackPanel();
        psu.Children.Add(Ui.Muted(Loc.T("A fonte de alimentação não é detectável por software. Informe a potência para que o diagnóstico compare com o consumo estimado.", "The power supply cannot be detected by software. Enter its wattage so the diagnosis can compare it with the estimated consumption."), 12.5));
        psu.Children.Add(Labeled(Loc.T("Potência (W, 150 a 2000)", "Wattage (W, 150 to 2000)"), _watts));
        psu.Children.Add(Labeled(Loc.T("Modelo (opcional)", "Model (optional)"), _model));
        psu.Children.Add(save);
        psu.Children.Add(_psuMsg);
        root.Children.Add(Block("Minha fonte", "My power supply", psu));

        // Sensores
        root.Children.Add(Block("Sensores", "Sensors", BuildSensors()));
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_sensorsChecked) _ = CheckSensorsAsync(false);
        };

        // Dados
        root.Children.Add(Block("Dados", "Data", BuildData()));

        // Sobre
        root.Children.Add(Block("Sobre", "About", BuildAbout()));

        Content = root;
    }

    private string _langCode = "Auto";

    /// <summary>Grava o idioma escolhido e oferece reiniciar agora (a nova instância pode pedir o UAC de novo).</summary>
    private void OnLanguagePicked(string code)
    {
        if (string.Equals(code, _langCode, StringComparison.OrdinalIgnoreCase)) return;
        _langCode = code;
        Prefs.Update(p => p.Language = code);
        bool restart = ConfirmDialog.Ask(Window.GetWindow(this), "WinDiag",
            Loc.T("Reiniciar agora para aplicar o idioma?", "Restart now to apply the language?"),
            Loc.T("Se escolher Não, o idioma será aplicado na próxima abertura do aplicativo. Ao reiniciar, o Windows pode pedir a confirmação de administrador (UAC) de novo.",
                  "If you choose No, the language will be applied the next time the application opens. When restarting, Windows may ask for administrator confirmation (UAC) again."));
        if (!restart) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var started = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            if (started == null) throw new InvalidOperationException(Loc.T("o processo não foi iniciado", "the process did not start"));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Say(_dataMsg, Loc.T("Não foi possível reiniciar: ", "Could not restart: ") + ex.Message, "WarnBrush");
        }
    }

    private static Border Block(string ptTitle, string enTitle, UIElement content)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.CardTitleInfo(Loc.T(ptTitle, enTitle), ptTitle));
        sp.Children.Add(content);
        return Ui.Card(sp);
    }

    private static Grid Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = label };
        l.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(control, 1);
        g.Children.Add(l);
        g.Children.Add(control);
        return g;
    }

    private async Task SavePsuAsync(Button save)
    {
        string raw = _watts.Text.Trim();
        int? watts = null;
        if (raw.Length > 0)
        {
            if (!int.TryParse(raw, out var w) || w < 150 || w > 2000)
            {
                Say(_psuMsg, Loc.T("Informe uma potência entre 150 e 2000 W (ou deixe em branco para não informar).", "Enter a wattage between 150 and 2000 W (or leave it blank to omit it)."), "BadBrush");
                return;
            }
            watts = w;
        }
        string? model = string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim();
        Prefs.Update(p =>
        {
            p.PsuWatts = watts;
            p.PsuModel = model;
        });
        save.IsEnabled = false;
        Say(_psuMsg, Loc.T("Salvo. Refazendo a leitura...", "Saved. Scanning again..."), "MutedBrush");
        try
        {
            await _rescan();
            Say(_psuMsg, Loc.T("Salvo e leitura refeita.", "Saved and scan repeated."), "OkBrush");
        }
        catch (Exception ex)
        {
            Say(_psuMsg, Loc.T("Salvo, mas a leitura falhou: ", "Saved, but the scan failed: ") + ex.Message, "WarnBrush");
        }
        finally { save.IsEnabled = true; }
    }

    private static void Say(TextBlock t, string text, string brushKey)
    {
        t.Text = text;
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private StackPanel BuildSensors()
    {
        var sp = new StackPanel();
        _sensorBox.Children.Add(Ui.Muted(Loc.T("Verificando as fontes de sensores...", "Checking the sensor sources..."), 13));
        sp.Children.Add(_sensorBox);
        _sensorRecheck = new Button { Content = Loc.T("Verificar de novo", "Check again"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _sensorRecheck.Click += async (_, _) => await CheckSensorsAsync(true);
        sp.Children.Add(_sensorRecheck);
        return sp;
    }

    private async Task CheckSensorsAsync(bool recheck)
    {
        if (_sensorsBusy || _sensorStatus == null) return;
        _sensorsBusy = true;
        _sensorsChecked = true;
        if (_sensorRecheck != null) _sensorRecheck.IsEnabled = false;
        try
        {
            var st = await _sensorStatus(recheck);
            _sensorBox.Children.Clear();
            if (st == null)
            {
                _sensorBox.Children.Add(Ui.Muted(Loc.T("Não foi possível consultar as fontes de sensores.", "Could not query the sensor sources."), 13));
                return;
            }
            _sensorBox.Children.Add(Ui.LabelValue("HWiNFO", st.HwInfoAvailable ? Loc.T("detectado (Shared Memory ligada)", "detected (Shared Memory on)") : Loc.T("não detectado", "not detected"),
                Ui.Res(st.HwInfoAvailable ? "OkBrush" : "MutedBrush")));
            if (!st.HwInfoAvailable)
                _sensorBox.Children.Add(Ui.Muted(Loc.T("Dica: abra o HWiNFO e ligue \"Shared Memory Support\" nas configurações dele para o WinDiag ler todos os sensores.", "Tip: open HWiNFO and turn on \"Shared Memory Support\" in its settings so WinDiag can read all sensors.")));
            _sensorBox.Children.Add(Ui.LabelValue("LibreHardwareMonitor", st.LhmAvailable
                ? (string.IsNullOrWhiteSpace(st.LhmVersion) ? Loc.T("disponível", "available") : Loc.T("versão ", "version ") + st.LhmVersion)
                : Loc.T("indisponível", "unavailable")));
            string driver = string.IsNullOrWhiteSpace(st.DriverName) ? Loc.T("nenhum", "none") : st.DriverName;
            _sensorBox.Children.Add(Ui.LabelValue(Loc.T("Driver de kernel", "Kernel driver"), st.KernelDriverAvailable ? driver + Loc.T(" (ativo)", " (active)") : driver + Loc.T(" (inativo)", " (inactive)")));
            foreach (var n in st.Notes) _sensorBox.Children.Add(Ui.Muted(n));
        }
        catch (Exception ex)
        {
            _sensorBox.Children.Clear();
            _sensorBox.Children.Add(Ui.Muted(Loc.T("Falha ao verificar os sensores: ", "Failed to check the sensors: ") + ex.Message, 13));
        }
        finally
        {
            _sensorsBusy = false;
            if (_sensorRecheck != null) _sensorRecheck.IsEnabled = true;
        }
    }

    private StackPanel BuildData()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.LabelValue(Loc.T("Histórico de leituras", "Scan history"), HistoryDir));
        sp.Children.Add(Ui.LabelValue(Loc.T("Registros de erro (logs)", "Error logs"), App.LogDirectory));
        sp.Children.Add(Ui.LabelValue(Loc.T("Preferências", "Preferences"), WinDiag.Core.Settings.UserPrefs.FilePath));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openHist = new Button { Content = Loc.T("Abrir pasta do histórico", "Open history folder"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        openHist.Click += (_, _) => OpenFolder(HistoryDir);
        var openLogs = new Button { Content = Loc.T("Abrir pasta dos logs", "Open logs folder"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        openLogs.Click += (_, _) => OpenFolder(App.LogDirectory);
        var clear = new Button { Content = Loc.T("Limpar histórico", "Clear history"), Padding = new Thickness(12, 4, 12, 4) };
        clear.Click += (_, _) => ClearHistory();
        row.Children.Add(openHist);
        row.Children.Add(openLogs);
        row.Children.Add(clear);
        sp.Children.Add(row);
        sp.Children.Add(_dataMsg);
        return sp;
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Say(_dataMsg, Loc.T("Não foi possível abrir a pasta: ", "Could not open the folder: ") + ex.Message, "WarnBrush"); }
    }

    private void ClearHistory()
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "WinDiag", Loc.T("Limpar o histórico de leituras?", "Clear the scan history?"),
                Loc.T("Os arquivos de leituras anteriores em " + HistoryDir + " serão apagados. A comparação \"desde a última leitura\" recomeça do zero. Snapshots de otimizações não são afetados.",
                      "The files of previous scans in " + HistoryDir + " will be deleted. The \"since the last scan\" comparison starts over. Optimization snapshots are not affected."),
                warn: true, okText: Loc.T("Limpar", "Clear"), cancelText: Loc.T("Cancelar", "Cancel")))
            return;
        try
        {
            int n = 0;
            if (Directory.Exists(HistoryDir))
            {
                foreach (var f in Directory.GetFiles(HistoryDir, "scan_*.json"))
                {
                    File.Delete(f);
                    n++;
                }
            }
            Say(_dataMsg, n + Loc.T(" arquivo(s) removido(s).", " file(s) removed."), "OkBrush");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(_dataMsg, Loc.T("Não foi possível limpar tudo: ", "Could not clear everything: ") + ex.Message, "WarnBrush");
        }
    }

    private StackPanel BuildAbout()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.LabelValue(Loc.T("Versão", "Version"), AppInfo.Version));
        sp.Children.Add(Ui.LabelValue(Loc.T("Licença", "License"), "MIT"));

        var link = new Hyperlink(new Run(AppInfo.RepoUrl));
        if (Uri.TryCreate(AppInfo.RepoUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            link.NavigateUri = u;
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { /* sem navegador padrão */ }
            e.Handled = true;
        };
        var linkText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        linkText.Inlines.Add(link);
        sp.Children.Add(Labeled(Loc.T("Repositório", "Repository"), linkText));

        var notices = new Button { Content = Loc.T("Avisos de terceiros", "Third-party notices"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        notices.Click += (_, _) => ShowNotices();
        sp.Children.Add(notices);
        return sp;
    }

    private void ShowNotices()
    {
        foreach (var name in new[] { "THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch { /* sem programa associado: mostra o texto embutido */ }
        }
        MessageBox.Show(Window.GetWindow(this),
            Loc.T("O WinDiag é distribuído sob a licença MIT.\n\n" +
            "Usa o .NET (Microsoft, licença MIT) e os pacotes System.Management, System.Diagnostics.PerformanceCounter (Microsoft, MIT) e CommunityToolkit.Mvvm (.NET Foundation, MIT). " +
            "Os ícones vêm da fonte Segoe Fluent Icons / Segoe MDL2 Assets do próprio Windows.\n\n" +
            "O arquivo THIRD-PARTY-NOTICES completo não foi encontrado ao lado do executável.",
            "WinDiag is distributed under the MIT license.\n\n" +
            "It uses .NET (Microsoft, MIT license) and the packages System.Management, System.Diagnostics.PerformanceCounter (Microsoft, MIT) and CommunityToolkit.Mvvm (.NET Foundation, MIT). " +
            "The icons come from the Segoe Fluent Icons / Segoe MDL2 Assets font that ships with Windows.\n\n" +
            "The full THIRD-PARTY-NOTICES file was not found next to the executable."),
            Loc.T("Avisos de terceiros", "Third-party notices"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
