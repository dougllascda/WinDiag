using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using WinDiag.Core;
using WinDiag.Core.Settings;
using WinDiag.App.Themes;

namespace WinDiag.App;

/// <summary>Aplicativo WPF. Aplica o tema antes de criar a primeira janela e registra o tratamento global de exceções.</summary>
public partial class App : Application
{
    private static readonly object LogLock = new();
    private int _errorCount;
    private bool _dialogOpen;

    /// <summary>Pasta dos logs de erro: %LocalAppData%\WinDiag\logs (um arquivo por dia).</summary>
    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDiag", "logs");

    /// <summary>Inicializa o tratamento de exceções, o tema (preferência salva ou automático) e abre a janela principal.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLog("AppDomain.UnhandledException (terminating=" + args.IsTerminating + ")", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        // O idioma é definido antes de qualquer janela existir.
        try { Loc.Set(UserPrefs.Load().Language); }
        catch { Loc.Set("Auto"); }
        Thread.CurrentThread.CurrentCulture = Loc.Culture;
        Thread.CurrentThread.CurrentUICulture = Loc.Culture;
        CultureInfo.DefaultThreadCurrentCulture = Loc.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Loc.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag)));
        ThemeManager.Initialize();
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var file = WriteLog("Dispatcher.UnhandledException", e.Exception);
        e.Handled = true;
        _errorCount++;
        if (_dialogOpen) return; // evita pilha de diálogos se o erro se repetir
        _dialogOpen = true;
        try
        {
            var where = file ?? Loc.T("(não foi possível gravar o log)", "(could not write the log)");
            var msg = Loc.T("Ocorreu um erro inesperado, mas o WinDiag continua aberto.\n\n", "An unexpected error occurred, but WinDiag remains open.\n\n") + e.Exception.Message +
                      Loc.T("\n\nDetalhes técnicos foram gravados em:\n", "\n\nTechnical details were written to:\n") + where;
            if (_errorCount >= 10) msg += Loc.T("\n\nO erro está se repetindo; o aplicativo será encerrado.", "\n\nThe error keeps repeating; the application will close.");
            MessageBox.Show(msg, "WinDiag", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_errorCount >= 10) Shutdown(1);
        }
        catch { /* nada mais a fazer */ }
        finally { _dialogOpen = false; }
    }

    /// <summary>Acrescenta uma entrada ao log do dia; devolve o caminho do arquivo, ou null se a gravação falhar.</summary>
    private static string? WriteLog(string origin, Exception? ex)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                var sb = new StringBuilder();
                sb.Append('[').Append(Fmt.DateTimeSec(DateTime.Now)).Append("] ").AppendLine(origin);
                sb.AppendLine(ex?.ToString() ?? "(no details)");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
        }
        catch { return null; }
    }
}
