using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinDiag.App.Views;
using WinDiag.Core;
using WinDiag.Core.Settings;
using WinDiag.Core.Update;

namespace WinDiag.App;

/// <summary>Parte da janela principal que cuida da atualização do próprio app: verificação automática, faixa de aviso e "Atualizar agora".</summary>
public partial class MainWindow
{
    /// <summary>true se o app foi aberto com "--updated" (mostra "Atualizado para a versão X" na barra de status).</summary>
    private bool _updatedNote;
    /// <summary>true durante o fluxo "Atualizar agora" (evita abrir duas vezes).</summary>
    private bool _updating;

    /// <summary>Limpa restos de atualização anterior, lê o argumento "--updated" e, sem atrasar a varredura, agenda a verificação automática.</summary>
    private void StartUpdateFlow()
    {
        _updatedNote = Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase));
        _ = Task.Run(() =>
        {
            try { UpdateInstaller.CleanupLeftovers(); }
            catch { /* limpar restos nunca derruba o app */ }
        });
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = AutoCheckAsync()));
    }

    /// <summary>Verificação automática conforme a frequência escolhida; só consulta a versão (nunca baixa). Nunca lança.</summary>
    private async Task AutoCheckAsync()
    {
        try
        {
            if (_closed) return;
            var prefs = Prefs.Read();
            var freq = UserPrefs.UpdateFrequencyOf(prefs);
            if (!UpdateCheck.IsDue(freq, prefs.LastUpdateCheckUtc, DateTime.UtcNow)) return;
            var info = await QueryAsync();
            if (_closed || info is null) return;
            Prefs.Update(p => p.LastUpdateCheckUtc = DateTime.UtcNow);
            if (info.IsNewer && !string.Equals(info.Version, Prefs.Read().SkippedVersion, StringComparison.OrdinalIgnoreCase))
                ShowUpdateBanner(info);
        }
        catch { /* a verificação automática nunca derruba o app */ }
    }

    /// <summary>Consulta a versão mais recente em segundo plano, com limite de 20 s; null se falhar.</summary>
    private static async Task<UpdateInfo?> QueryAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await Task.Run(() => UpdateCheck.CheckAsync(cts.Token), cts.Token);
        }
        catch { return null; }
    }

    /// <summary>"Verificar agora" das Configurações: ignora a versão pulada, grava a data da verificação e mostra a faixa se houver versão nova.</summary>
    public async Task<UpdateInfo?> CheckForUpdateNowAsync()
    {
        var info = await QueryAsync();
        if (_closed || info is null) return null;
        Prefs.Update(p => p.LastUpdateCheckUtc = DateTime.UtcNow);
        if (info.IsNewer) ShowUpdateBanner(info);
        return info;
    }

    /// <summary>Monta e exibe a faixa discreta no topo: "WinDiag X disponível" com Atualizar agora, Novidades, Pular esta versão e Depois.</summary>
    private void ShowUpdateBanner(UpdateInfo info)
    {
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);

        Button Make(string label, Action click, bool accent = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            if (accent) b.Style = (Style)FindResource("AccentButton");
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => click();
            return b;
        }
        buttons.Children.Add(Make(Loc.T("Atualizar agora", "Update now"), () => _ = UpdateNowAsync(info), accent: true));
        buttons.Children.Add(Make(Loc.T("Novidades", "What's new"), () => new UpdateNotesWindow(info) { Owner = this }.ShowDialog()));
        buttons.Children.Add(Make(Loc.T("Pular esta versão", "Skip this version"), () =>
        {
            Prefs.Update(p => p.SkippedVersion = info.Version);
            HideUpdateBanner();
        }));
        buttons.Children.Add(Make(Loc.T("Depois", "Later"), HideUpdateBanner));
        row.Children.Add(buttons);

        var text = new TextBlock { Text = Loc.T($"WinDiag {info.Version} disponível", $"WinDiag {info.Version} available"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        row.Children.Add(text);

        UpdateBanner.Child = row;
        System.Windows.Automation.AutomationProperties.SetName(UpdateBanner, Loc.T($"WinDiag {info.Version} disponível", $"WinDiag {info.Version} available"));
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>Fecha a faixa nesta sessão.</summary>
    private void HideUpdateBanner()
    {
        UpdateBanner.Child = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>"Atualizar agora": bloqueia enquanto uma otimização/ferramenta/varredura está em curso; senão roda o fluxo de confirmação e download.</summary>
    private async Task UpdateNowAsync(UpdateInfo info)
    {
        if (_updating || _closed) return;
        if (_exporting)
        {
            MessageBox.Show(this, Loc.T("Há uma exportação em andamento. Espere terminar para atualizar.", "An export is in progress. Wait for it to finish before updating."),
                "WinDiag", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        using var cts = new CancellationTokenSource();
        using var ticket = Operations.TryBegin(Loc.T("Atualização do WinDiag", "WinDiag update"), cts);
        if (ticket is null)
        {
            Operations.WarnBusy(this);
            return;
        }
        _updating = true;
        BeginBusy();
        try { await UpdateFlow.RunAsync(this, info, cts, ticket); }
        finally
        {
            _updating = false;
            EndBusy();
        }
    }
}
