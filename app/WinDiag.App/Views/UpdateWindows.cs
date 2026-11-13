using System.Windows;
using System.Windows.Controls;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Update;

namespace WinDiag.App.Views;

/// <summary>Janela com as novidades (notas em texto simples) de uma versão e o link para a página do release.</summary>
public sealed class UpdateNotesWindow : Window
{
    /// <summary>Monta a janela com as notas de <paramref name="info"/>.</summary>
    public UpdateNotesWindow(UpdateInfo info)
    {
        Title = Loc.T("Novidades da versão ", "What's new in version ") + info.Version;
        Width = 640;
        Height = 480;
        MinWidth = 440;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Themes.ThemeManager.Register(this);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? Loc.T("Esta versão não traz notas publicadas.", "This version has no published notes.") : info.Notes,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        notes.Style = (Style)Application.Current.FindResource("ValueBox");
        System.Windows.Automation.AutomationProperties.SetName(notes, Loc.T("Notas da versão", "Release notes"));
        root.Children.Add(Ui.Card(notes));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var page = new Button { Content = Loc.T("Abrir a página do release", "Open the release page"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(page, Loc.T("Abrir a página do release no navegador", "Open the release page in the browser"));
        page.Click += (_, _) => UpdateFlow.OpenPage(info.Url);
        var close = new Button { Content = Loc.T("Fechar", "Close"), MinWidth = 90, IsCancel = true, IsDefault = true };
        buttons.Children.Add(page);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
    }
}

/// <summary>Janela de progresso: baixa e confere (SHA-256) o pacote da atualização, com botão Cancelar.</summary>
public sealed class UpdateProgressWindow : Window
{
    /// <summary>Atualização a baixar.</summary>
    private readonly UpdateInfo _info;
    /// <summary>Tipo de instalação detectado.</summary>
    private readonly InstallKind _kind;
    /// <summary>Cancelamento do download (compartilhado com o estado ocupado; quem o criou o descarta).</summary>
    private readonly CancellationTokenSource _cts;
    /// <summary>Barra de progresso (indeterminada até o primeiro valor).</summary>
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0) };
    /// <summary>Texto da etapa atual.</summary>
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    /// <summary>true enquanto o download roda (impede fechar sem cancelar).</summary>
    private bool _working = true;

    /// <summary>Pacote baixado e verificado (null se falhou ou foi cancelado).</summary>
    public DownloadedUpdate? Result { get; private set; }
    /// <summary>Mensagem de erro (null se não houve erro).</summary>
    public string? Error { get; private set; }
    /// <summary>true se o usuário cancelou.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Cria a janela; o download começa quando ela é exibida.</summary>
    public UpdateProgressWindow(UpdateInfo info, InstallKind kind, CancellationTokenSource cts)
    {
        _cts = cts;
        _info = info;
        _kind = kind;
        Title = Loc.T("Atualizando o WinDiag", "Updating WinDiag");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Themes.ThemeManager.Register(this);

        var root = new StackPanel { Margin = new Thickness(24) };
        _text.Text = Loc.T("Baixando...", "Downloading...");
        System.Windows.Automation.AutomationProperties.SetName(_bar, Loc.T("Progresso do download", "Download progress"));
        root.Children.Add(new TextBlock { Text = "WinDiag " + info.Version, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(_text);
        root.Children.Add(_bar);
        var cancel = new Button { Content = Loc.T("Cancelar", "Cancel"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetName(cancel, Loc.T("Cancelar a atualização", "Cancel the update"));
        cancel.Click += (_, _) =>
        {
            Cancelled = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        root.Children.Add(cancel);
        Content = root;

        Closing += (_, e) =>
        {
            if (!_working) return;
            e.Cancel = true;
            Cancelled = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>Baixa e verifica o pacote; ao terminar fecha a janela.</summary>
    private async Task RunAsync()
    {
        var progress = new Progress<double>(v =>
        {
            if (v > 1) v /= 100.0;
            v = Math.Clamp(v, 0, 1);
            _bar.IsIndeterminate = false;
            _bar.Value = v;
            _text.Text = v >= 1
                ? Loc.T("Conferindo a integridade (SHA-256)...", "Verifying integrity (SHA-256)...")
                : Loc.T("Baixando... ", "Downloading... ") + (v * 100).ToString("0", Loc.Culture) + " %";
        });
        try { Result = await UpdateInstaller.DownloadAsync(_info, _kind, progress, _cts.Token); }
        catch (OperationCanceledException) { Cancelled = true; }
        catch (Exception ex) { Error = ex.Message; }
        _working = false;
        DialogResult = Result is not null;
    }
}

/// <summary>Fluxo "Atualizar agora": confirmação, download com progresso, aplicação e encerramento do app.</summary>
internal static class UpdateFlow
{
    /// <summary>Abre a página do release no navegador (só https).</summary>
    public static void OpenPage(string? url)
    {
        string target = !string.IsNullOrWhiteSpace(url) && UpdateCheck.IsOfficialUrl(url) ? url : AppInfo.RepoUrl + "/releases";
        try { WinDiag.Core.Util.Shell.OpenUnelevated(target); } catch { /* sem navegador padrão */ }
    }

    /// <summary>Executa o fluxo completo; só baixa depois da confirmação do usuário. Nunca lança.</summary>
    public static async Task RunAsync(Window owner, UpdateInfo info, CancellationTokenSource cts, Operations.Ticket ticket)
    {
        DownloadedUpdate? downloaded = null;
        try
        {
            var kind = UpdateInstaller.DetectKind();
            string name = UpdateInstaller.AssetNameFor(kind, info.Version);
            var asset = info.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            string size = asset is null || asset.Size <= 0 ? Fmt.NA : ToolsView.Size(asset.Size);
            string body = Loc.T($"Arquivo: {name} ({size})\nOrigem: {info.Url}\n\n", $"File: {name} ({size})\nSource: {info.Url}\n\n")
                + Loc.T("O WinDiag vai fechar, instalar a atualização e abrir de novo.", "WinDiag will close, install the update and open again.");
            if (!ConfirmDialog.Ask(owner, Loc.T("Atualizar o WinDiag", "Update WinDiag"),
                    Loc.T($"Atualizar da versão {AppInfo.Version} para {info.Version}?", $"Update from version {AppInfo.Version} to {info.Version}?"), body,
                    warn: true, okText: Loc.T("Atualizar agora", "Update now"), cancelText: Loc.T("Cancelar", "Cancel")))
                return;

            var win = new UpdateProgressWindow(info, kind, cts);
            if (owner.IsLoaded) win.Owner = owner;
            win.ShowDialog();
            downloaded = win.Result;
            if (win.Cancelled) return;
            string? error = win.Error;
            if (downloaded is not null)
            {
                OpResult op;
                try { op = await Task.Run(() => UpdateInstaller.Apply(downloaded)); }
                catch (Exception ex) { op = new OpResult { Ok = false, Error = ex.Message }; }
                if (op.Ok)
                {
                    downloaded.Dispose();
                    downloaded = null;
                    ticket.Dispose(); // libera o estado ocupado: o fechamento do app não deve ser bloqueado
                    Application.Current.Shutdown();
                    return;
                }
                error = op.Error ?? Loc.T("Falha ao aplicar a atualização.", "Failed to apply the update.");
            }
            ShowFailure(owner, info, error ?? Loc.T("Falha desconhecida.", "Unknown failure."));
        }
        catch (Exception ex) { ShowFailure(owner, info, ex.Message); }
        finally { downloaded?.Dispose(); }
    }

    /// <summary>Mostra o erro e oferece abrir a página do release.</summary>
    private static void ShowFailure(Window owner, UpdateInfo info, string error)
    {
        if (ConfirmDialog.Ask(owner, Loc.T("Atualizar o WinDiag", "Update WinDiag"), Loc.T("Não foi possível atualizar.", "Could not update."), error,
                warn: true, okText: Loc.T("Abrir a página do release", "Open the release page"), cancelText: Loc.T("Fechar", "Close")))
            OpenPage(info.Url);
    }
}
