using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Live;

/// <summary>Grupo de processos com o mesmo nome, candidato a ser fechado na sessão de jogo.</summary>
public sealed class HeavyApp
{
    /// <summary>Nome do processo (sem extensão).</summary>
    public string Name { get; set; } = "";
    /// <summary>Quantidade de processos com esse nome.</summary>
    public int Count { get; set; }
    /// <summary>Soma do working set, em MB (inclui dupla contagem de memória compartilhada).</summary>
    public double RamMB { get; set; }
    /// <summary>Caminho do executável, se legível.</summary>
    public string? Path { get; set; }
    /// <summary>PIDs do grupo.</summary>
    public int[] Pids { get; set; } = Array.Empty<int>();
    /// <summary>Aviso exibido em amarelo (ex.: claude/node); vazio quando não há.</summary>
    public string Warning { get; set; } = "";
}

/// <summary>Resultado do fechamento de aplicativos na sessão de jogo.</summary>
public sealed class CloseResult
{
    /// <summary>RAM disponível, em MB, antes de fechar (null se não lida).</summary>
    public double? AvailBeforeMB { get; set; }
    /// <summary>RAM disponível, em MB, cerca de 2 s depois de fechar (null se não lida).</summary>
    public double? AvailAfterMB { get; set; }
    /// <summary>Uma linha por aplicativo: fechado normalmente, encerrado à força ou falha.</summary>
    public List<string> Lines { get; } = new();
}

/// <summary>
/// Sessão de jogo: lista aplicativos que consomem RAM na sessão atual (fonte: <see cref="Process"/>) e fecha apenas os escolhidos pelo usuário.
/// Não exige administrador, mas processos de outros usuários ou protegidos não são listados. Nada é fechado sem escolha explícita.
/// </summary>
public static class GameSession
{
    /// <summary>Aviso exibido para claude/node.</summary>
    public static string ClaudeWarning => Loc.T("é o assistente Claude; fechar encerra a conversa", "it is the Claude assistant; closing it ends the conversation");

    private static readonly Regex Exclude = new(
        @"^(Idle|System|Registry|smss|csrss|wininit|winlogon|services|lsass|svchost|explorer|dwm|fontdrvhost|sihost|ctfmon|SearchHost|StartMenuExperienceHost|ShellExperienceHost|TextInputHost|RuntimeBroker|audiodg|conhost|powershell|pwsh|WindowsTerminal|cmd|vmmem.*|MsMpEng|nvcontainer|NVDisplay\.Container|NVIDIA Web Helper|nvsphelper64|RtkAudUService64)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AvProc = new(
        @"^(avp|avpui|MsMpEng|MpDefenderCoreService|AvastSvc|AVGSvc|ekrn|bdservicehost|vsserv|NortonSecurity|mcshield|mfemms)(#\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BgApp = new(
        @"^(msedge|msedgewebview2|chrome|firefox|opera|brave|Discord|steamwebhelper|OneDrive|Teams|ms-teams|Spotify|CurseForge|Overwolf|EpicGamesLauncher|EpicWebHelper|Battle\.net|EADesktop|Code|Antigravity|language_server.*|Gemini|claude|WhatsApp|Telegram)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True se o processo nunca deve aparecer na lista (sistema, antivírus, drivers, o próprio WinDiag).</summary>
    public static bool IsExcluded(string name, string? path, int pid = -1)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (pid >= 0 && pid == Environment.ProcessId) return true;
        if (name.StartsWith("WinDiag", StringComparison.OrdinalIgnoreCase)) return true;
        if (Exclude.IsMatch(name) || AvProc.IsMatch(name)) return true;
        if (!string.IsNullOrEmpty(path) && path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Aviso para apps que não são excluídos mas exigem cuidado; vazio caso contrário.</summary>
    public static string WarningFor(string name) =>
        name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("node", StringComparison.OrdinalIgnoreCase)
            ? ClaudeWarning : "";

    /// <summary>Lista, por nome, os aplicativos da sessão atual com ao menos 150 MB de working set (excluindo sistema, antivírus e o próprio WinDiag). Em falha devolve lista vazia.</summary>
    public static List<HeavyApp> ListHeavyApps()
    {
        var result = new List<HeavyApp>();
        try
        {
            int session = 0;
            try { using var self = Process.GetCurrentProcess(); session = self.SessionId; } catch (Exception) { /* usa a sessão 0 */ }
            var groups = new Dictionary<string, (List<int> Pids, double Bytes, string? Path)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    using (p)
                    {
                        string name = p.ProcessName;
                        if (p.SessionId != session) continue;
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (IsExcluded(name, path, p.Id)) continue;
                        long ws = 0;
                        try { ws = p.WorkingSet64; } catch { }
                        if (!groups.TryGetValue(name, out var g)) g = (new List<int>(), 0, null);
                        g.Pids.Add(p.Id);
                        g.Bytes += ws;
                        g.Path ??= path;
                        groups[name] = g;
                    }
                }
                catch { }
            }
            var list = groups
                .Select(kv => new HeavyApp
                {
                    Name = kv.Key,
                    Count = kv.Value.Pids.Count,
                    RamMB = Math.Round(kv.Value.Bytes / 1048576d, 0),
                    Path = kv.Value.Path,
                    Pids = kv.Value.Pids.ToArray(),
                    Warning = WarningFor(kv.Key)
                })
                .Where(a => a.RamMB >= 150)
                .OrderByDescending(a => a.RamMB)
                .ToList();
            // Nunca oferece o app em primeiro plano nem o de maior uso 3D (provável jogo).
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in new[] { NameOfPid(ForegroundPid()), NameOfPid(TopGpuPid()) }) if (!string.IsNullOrEmpty(n)) keep.Add(n);
            list = list.Where(a => !keep.Contains(a.Name)).ToList();
            var top = list.Take(8).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result = list.Where(a => BgApp.IsMatch(a.Name) || top.Contains(a.Name)).ToList();
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Decide se um processo pode ser encerrado à força: nunca se tem janela principal (pode haver "salvar?" pendente);
    /// um processo sem janela só, se o app não tem nenhuma janela ou se as janelas do app já fecharam.
    /// </summary>
    /// <param name="thisHasWindow">O processo tem janela principal.</param>
    /// <param name="groupHasWindow">Algum processo do app tinha janela principal ao pedir o fechamento.</param>
    /// <param name="windowedStillAlive">Algum processo com janela do app ainda está em execução.</param>
    public static bool CanForceKill(bool thisHasWindow, bool groupHasWindow, bool windowedStillAlive) =>
        !thisHasWindow && (!groupHasWindow || !windowedStillAlive);

    /// <summary>
    /// Pede o fechamento (CloseMainWindow) e espera até 5 s. Processos com janela principal que não fecharem NÃO são forçados
    /// (podem ter algo a salvar): são reportados como "não fechou". Só processos sem janela do mesmo app são encerrados à força,
    /// depois que a janela principal fechou (ou se o app não tem janela).
    /// </summary>
    /// <param name="apps">Aplicativos escolhidos pelo usuário.</param>
    /// <returns>Linhas descrevendo o resultado de cada aplicativo e a RAM disponível antes/depois.</returns>
    public static CloseResult Close(IEnumerable<HeavyApp> apps)
    {
        var res = new CloseResult { AvailBeforeMB = AvailableMB() };
        foreach (var app in apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            try
            {
                var procs = new List<(Process P, bool HasWindow)>();
                foreach (var pid in app.Pids)
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        // Protege contra reaproveitamento de PID por outro programa.
                        if (!p.ProcessName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || pid == Environment.ProcessId) { p.Dispose(); continue; }
                        bool win = false;
                        try { p.Refresh(); win = p.MainWindowHandle != IntPtr.Zero; } catch { }
                        procs.Add((p, win));
                    }
                    catch { }
                }
                bool groupHasWindow = procs.Any(x => x.HasWindow);
                foreach (var (p, win) in procs) { if (win) { try { p.CloseMainWindow(); } catch { } } }
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && procs.Any(x => x.HasWindow && Alive(x.P))) Thread.Sleep(300);

                var windowedAlive = procs.Where(x => x.HasWindow && Alive(x.P)).ToList();
                bool windowedStillAlive = windowedAlive.Count > 0;
                int killed = 0;
                if (!windowedStillAlive)
                {
                    // Janela(s) principal(is) fechou(aram): dá um tempo para os auxiliares saírem sozinhos e encerra só os sem janela que restarem.
                    var grace = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < grace && procs.Any(x => !x.HasWindow && Alive(x.P))) Thread.Sleep(300);
                    foreach (var (p, win) in procs)
                    {
                        if (CanForceKill(win, groupHasWindow, windowedStillAlive) && Alive(p))
                        {
                            try { p.Kill(); killed++; } catch { }
                        }
                    }
                }
                if (windowedStillAlive)
                    res.Lines.Add(Loc.T($"{app.Name}: não fechou (pode haver algo a salvar); {windowedAlive.Count} processo(s) com janela continuam abertos e não foram forçados", $"{app.Name}: did not close (there may be something to save); {windowedAlive.Count} process(es) with a window are still open and were not forced"));
                else if (killed > 0)
                    res.Lines.Add(Loc.T($"{app.Name}: janela fechada; {killed} processo(s) auxiliar(es) sem janela encerrado(s) à força", $"{app.Name}: window closed; {killed} helper process(es) without a window were force-terminated"));
                else
                    res.Lines.Add(Loc.T($"{app.Name}: fechado normalmente", $"{app.Name}: closed normally"));
                foreach (var (p, _) in procs) p.Dispose();
            }
            catch (Exception ex) { res.Lines.Add(Loc.T($"{app.Name}: falha ao fechar ({ex.Message})", $"{app.Name}: failed to close ({ex.Message})")); }
        }
        Thread.Sleep(2000);
        res.AvailAfterMB = AvailableMB();
        return res;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>PID do processo dono da janela em primeiro plano; null se não houver ou em falha.</summary>
    public static int? ForegroundPid()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            return GetWindowThreadProcessId(h, out var pid) != 0 && pid != 0 ? (int)pid : null;
        }
        catch { return null; }
    }

    /// <summary>PID com maior uso do motor 3D da GPU (provável jogo), medido em ~0,5 s pelos contadores "GPU Engine"; null se indisponível ou sem uso.</summary>
    public static int? TopGpuPid()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine")) return null;
            var cat = new PerformanceCounterCategory("GPU Engine");
            Dictionary<string, CounterSample> Snap()
            {
                var d = new Dictionary<string, CounterSample>();
                var data = cat.ReadCategory();
                if (!data.Contains("Utilization Percentage")) return d;
                foreach (InstanceData i in data["Utilization Percentage"].Values)
                    if (i.InstanceName is not null && i.InstanceName.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) d[i.InstanceName] = i.Sample;
                return d;
            }
            var a = Snap();
            Thread.Sleep(500);
            var b = Snap();
            var perPid = new Dictionary<int, double>();
            foreach (var (name, s2) in b)
            {
                if (!a.TryGetValue(name, out var s1)) continue;
                double v = CounterSample.Calculate(s1, s2);
                var pid = Capture.ParseGpuPid(name);
                if (double.IsNaN(v) || v <= 0 || pid is null) continue;
                perPid[pid.Value] = perPid.GetValueOrDefault(pid.Value) + v;
            }
            return perPid.Count == 0 ? null : perPid.OrderByDescending(kv => kv.Value).First().Key;
        }
        catch { return null; }
    }

    private static string? NameOfPid(int? pid)
    {
        if (pid is null) return null;
        try { using var p = Process.GetProcessById(pid.Value); return p.ProcessName; } catch { return null; }
    }

    private static bool Alive(Process p)
    {
        try { p.Refresh(); return !p.HasExited; } catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>RAM disponível em MB (GlobalMemoryStatusEx); null se a leitura falhar.</summary>
    public static double? AvailableMB()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? Math.Round(m.ullAvailPhys / 1048576d, 0) : null;
        }
        catch { return null; }
    }
}
