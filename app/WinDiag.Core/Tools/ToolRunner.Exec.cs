using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Text;
using WinDiag.Core.Fixes;
using WinDiag.Core.Util;

namespace WinDiag.Core.Tools;

/// <summary>Parte do <see cref="ToolRunner"/> com a execução de cada ferramenta. Todos os comandos são fixos e escritos aqui; nada é baixado nem gerado em tempo de execução.</summary>
public sealed partial class ToolRunner
{
    /// <summary>Uma execução em andamento: reúne o resultado, o canal de andamento e o cancelamento, e contém o passo a passo de cada ferramenta.</summary>
    private sealed class Job
    {
        /// <summary>Executor dono da execução.</summary>
        private readonly ToolRunner _owner;
        /// <summary>Ferramenta em execução.</summary>
        private readonly ToolDefinition _tool;
        /// <summary>Canal de andamento (pode ser null).</summary>
        private readonly IProgress<string>? _log;
        /// <summary>Cancelamento.</summary>
        private readonly CancellationToken _ct;
        /// <summary>Opções marcadas (ou null para as padrão).</summary>
        private readonly IReadOnlyCollection<string>? _options;
        /// <summary>Serviços parados durante a limpeza do cache do Windows Update.</summary>
        private static readonly string[] BitsServices = { "wuauserv", "bits" };
        /// <summary>Serviços parados na redefinição do Windows Update (os que estiverem em execução).</summary>
        private static readonly string[] WuServices = { "wuauserv", "bits", "msiserver", "cryptsvc" };
        /// <summary>Trava das mensagens (comandos escrevem de threads de leitura).</summary>
        private readonly object _gate = new();

        /// <summary>Resultado em construção.</summary>
        public OpResult R { get; } = new();

        /// <summary>Cria a execução.</summary>
        /// <param name="owner">Executor.</param>
        /// <param name="tool">Ferramenta.</param>
        /// <param name="log">Canal de andamento.</param>
        /// <param name="options">Opções marcadas.</param>
        /// <param name="ct">Cancelamento.</param>
        public Job(ToolRunner owner, ToolDefinition tool, IProgress<string>? log, IReadOnlyCollection<string>? options, CancellationToken ct)
        {
            _owner = owner; _tool = tool; _log = log; _ct = ct; _options = options;
        }

        /// <summary>Registra uma mensagem do que foi feito (com prefixo de simulação em DryRun), no andamento e no changes.log.</summary>
        /// <param name="m">Mensagem.</param>
        private void Say(string m)
        {
            lock (_gate)
            {
                R.Messages.Add(_owner.DryRun ? Loc.T("[simulação] ", "[simulation] ") + m : m);
                if (!_owner.DryRun) _owner.Store.Log($"{_tool.Id}: {m}");
            }
            _log?.Report(m);
        }

        /// <summary>Marca a execução como falha com a mensagem informada.</summary>
        /// <param name="msg">Erro no idioma atual.</param>
        private void Fail(string msg) { R.Ok = false; R.Error = msg; }

        /// <summary>Em simulação: diz o que seria executado, sem executar nada.</summary>
        public void Simulate()
        {
            if (_tool.Id == "DISK_CLEANUP")
            {
                foreach (var id in SelectedOptions())
                {
                    var dirs = DiskCleanup.Targets(id).Select(t => t.Dir).ToList();
                    Say(Loc.T("Limparia: ", "Would clean: ") + DiskCleanup.Label(id) + (dirs.Count > 0 ? " (" + string.Join("; ", dirs) + ")" : ""));
                }
                return;
            }
            foreach (var c in _tool.Commands) Say(Loc.T("Executaria: ", "Would run: ") + c);
            if (_tool.NeedsRestart) R.RebootNeeded = true;
        }

        /// <summary>Opções da limpeza de disco a executar: as informadas (só as válidas) ou, sem informar, as marcadas por padrão.</summary>
        private List<string> SelectedOptions() =>
            _options is null ? DiskCleanup.AllOptionIds.Where(DiskCleanup.DefaultOn).ToList()
                : DiskCleanup.AllOptionIds.Where(id => _options.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();

        /// <summary>Executa a ferramenta de verdade.</summary>
        public async Task ExecuteAsync()
        {
            switch (_tool.Id)
            {
                case "PRINT_UNSTICK": PrintUnstick(); break;
                case "SPOOLER_RESTART": SpoolerRestart(); break;
                case "PRINT_TEST_PAGE": PrintTestPage(); break;
                case "NET_TEST": await NetTestAsync().ConfigureAwait(false); break;
                case "NET_DNS_FLUSH": Simple(Proc.Sys32("ipconfig.exe"), "/flushdns", 30000, "ipconfig /flushdns"); break;
                case "NET_IP_RENEW": NetIpRenew(); break;
                case "NET_STACK_RESET": NetStackReset(); break;
                case "TIME_SYNC": TimeSync(); break;
                case "DISK_CLEANUP": DiskCleanupRun(); break;
                case "SHADER_CACHE_CLEAR": ShaderCache(); break;
                case "SYS_SFC": Sfc(); break;
                case "SYS_DISM": Dism(); break;
                case "SYS_CHKDSK": Chkdsk(); break;
                case "WU_RESET": WuReset(); break;
                case "EXPLORER_RESTART": ExplorerRestart(); break;
                case "MEM_DIAG": Open(Proc.Sys32("mdsched.exe"), ""); Say(Loc.T("Ferramenta de diagnóstico de memória do Windows aberta; ela pergunta quando reiniciar.", "Windows Memory Diagnostic opened; it asks when to restart.")); break;
                case "RESTORE_POINT": RestorePoint(); break;
                case "BATTERY_REPORT": BatteryReport(); break;
                case "OPEN_DEVMGMT": Open("devmgmt.msc", ""); break;
                case "OPEN_PRINTERS": OpenUnelevated("ms-settings:printers"); break;
                case "OPEN_APPS": OpenUnelevated("ms-settings:appsfeatures"); break;
                case "OPEN_WINUPDATE": OpenUnelevated("ms-settings:windowsupdate"); break;
                case "OPEN_DISKMGMT": Open("diskmgmt.msc", ""); break;
                case "OPEN_RELIABILITY": Open("perfmon.exe", "/rel"); break;
                default: Fail(Loc.T("Ferramenta desconhecida: ", "Unknown tool: ") + _tool.Id); break;
            }
        }

        // ------------------------------------------------------------ utilitários

        /// <summary>Abre uma tela, arquivo ou programa pelo shell do Windows (o próprio Windows pede elevação quando precisa).</summary>
        /// <param name="file">Arquivo, comando ou endereço (ex.: ms-settings:printers).</param>
        /// <param name="args">Argumentos.</param>
        private void Open(string file, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            Say(Loc.T("Aberto: ", "Opened: ") + (args.Length > 0 ? file + " " + args : file));
        }

        /// <summary>Abre um endereço ou arquivo pelo shell do usuário (explorer.exe), para o programa aberto não herdar a elevação do WinDiag.</summary>
        /// <param name="target">Endereço (ms-settings:, http, https) ou arquivo.</param>
        private void OpenUnelevated(string target)
        {
            Shell.OpenUnelevated(target);
            Say(Loc.T("Aberto: ", "Opened: ") + target);
        }

        /// <summary>Executa um programa sem janela entregando a saída ao andamento e, opcionalmente, às mensagens do resultado.</summary>
        /// <param name="exe">Executável.</param>
        /// <param name="args">Argumentos.</param>
        /// <param name="timeoutMs">Tempo máximo.</param>
        /// <param name="keepLine">Decide se uma linha de saída vira mensagem do resultado (null = nenhuma).</param>
        /// <param name="enc">Codificação da saída (null = OEM).</param>
        private Proc.Result Cmd(string exe, string args, int timeoutMs, Func<string, bool>? keepLine = null, Encoding? enc = null)
        {
            var res = Proc.RunStreaming(exe, args, line =>
            {
                if (keepLine is not null && keepLine(line)) Say(line); else _log?.Report(line);
            }, _ct, timeoutMs, enc);
            _ct.ThrowIfCancellationRequested();
            return res;
        }

        /// <summary>Executa um comando curto, mostra a saída e falha se o código de saída não for zero.</summary>
        /// <param name="exe">Executável.</param>
        /// <param name="args">Argumentos.</param>
        /// <param name="timeoutMs">Tempo máximo.</param>
        /// <param name="label">Comando como exibido ao usuário.</param>
        private void Simple(string exe, string args, int timeoutMs, string label)
        {
            var r = Cmd(exe, args, timeoutMs, _ => true);
            if (!r.Ok) Fail(Loc.T($"{label} falhou (código {r.ExitCode}). {r.StdErr}".Trim(), $"{label} failed (code {r.ExitCode}). {r.StdErr}".Trim()));
            else Say(Loc.T($"{label}: concluído", $"{label}: done"));
        }

        /// <summary>true se o serviço está em execução (sc query).</summary>
        /// <param name="name">Nome do serviço.</param>
        private static bool ServiceRunning(string name) =>
            Proc.Run(Proc.Sys32("sc.exe"), "query " + name, 8000).StdOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);

        /// <summary>Espera até <paramref name="seconds"/> s pelo estado do serviço, respeitando o cancelamento.</summary>
        /// <param name="name">Nome do serviço.</param>
        /// <param name="state">Palavra do estado (STOPPED ou RUNNING).</param>
        /// <param name="seconds">Tempo máximo.</param>
        /// <param name="token">Cancelamento da espera (religar serviços no finally usa <see cref="CancellationToken.None"/>).</param>
        private static bool WaitState(string name, string state, int seconds, CancellationToken token)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                if (Proc.Run(Proc.Sys32("sc.exe"), "query " + name, 8000).StdOut.Contains(state, StringComparison.OrdinalIgnoreCase)) return true;
                token.WaitHandle.WaitOne(500);
            }
            return false;
        }

        /// <summary>Para um serviço e espera o estado STOPPED. Tolera "já parado" (1062); com dependentes (1051) usa net stop /y. Lança exceção se não parar.</summary>
        /// <param name="name">Nome do serviço.</param>
        private void StopService(string name)
        {
            var st = Proc.Run(Proc.Sys32("sc.exe"), "stop " + name, 30000);
            if (!st.Ok && st.ExitCode != 1062)
            {
                if (st.ExitCode == 5) throw new UnauthorizedAccessException(name);
                if (st.ExitCode == 1051) Proc.Run(Proc.Sys32("net.exe"), $"stop {name} /y", 45000);
                else throw new InvalidOperationException(Loc.T($"sc stop {name} falhou: {(st.StdOut + " " + st.StdErr).Trim()}", $"sc stop {name} failed: {(st.StdOut + " " + st.StdErr).Trim()}"));
            }
            if (!WaitState(name, "STOPPED", 30, _ct))
                throw new InvalidOperationException(Loc.T($"O serviço {name} não parou a tempo.", $"The {name} service did not stop in time."));
            Say(Loc.T($"Serviço {name}: parado", $"Service {name}: stopped"));
        }

        /// <summary>Inicia um serviço e espera RUNNING. Tolera "já em execução" (1056). Devolve false (sem lançar) se não iniciar, para uso no finally.</summary>
        /// <param name="name">Nome do serviço.</param>
        private bool StartService(string name)
        {
            try
            {
                var st = Proc.Run(Proc.Sys32("sc.exe"), "start " + name, 30000);
                if (!st.Ok && st.ExitCode != 1056) return false;
                bool ok = ServiceRunning(name) || WaitState(name, "RUNNING", 30, CancellationToken.None); // religar nunca usa o token já cancelado
                if (ok) Say(Loc.T($"Serviço {name}: iniciado", $"Service {name}: started"));
                return ok;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------ impressão

        /// <summary>PRINT_UNSTICK: para o Spooler, apaga a fila e inicia o Spooler (e dependentes) de novo.</summary>
        private void PrintUnstick()
        {
            var dir = FixEngine.SpoolDirectory;
            int deleted = 0, failed = 0;
            var deps = FixEngine.WithSpoolerStopped(() => { (deleted, failed) = FixEngine.ClearSpoolDirectory(dir); });
            Say(Loc.T($"Fila de impressão: {deleted} arquivo(s) apagado(s) de {dir}" + (failed > 0 ? $"; {failed} não puderam ser apagados" : ""),
                $"Print queue: {deleted} file(s) deleted from {dir}" + (failed > 0 ? $"; {failed} could not be deleted" : "")));
            Say(Loc.T("Serviço Spooler: parado e iniciado", "Spooler service: stopped and started"));
            if (deps.Count > 0) Say(Loc.T("Serviços dependentes religados: ", "Dependent services started again: ") + string.Join(", ", deps));
        }

        /// <summary>SPOOLER_RESTART: reinicia o Spooler e religa os dependentes.</summary>
        private void SpoolerRestart()
        {
            var deps = FixEngine.WithSpoolerStopped(null);
            Say(Loc.T("Serviço Spooler: parado e iniciado", "Spooler service: stopped and started"));
            if (deps.Count > 0) Say(Loc.T("Serviços dependentes religados: ", "Dependent services started again: ") + string.Join(", ", deps));
        }

        /// <summary>PRINT_TEST_PAGE: envia a página de teste à impressora padrão.</summary>
        private void PrintTestPage()
        {
            var name = DefaultPrinterName(null);
            if (name is null) { Fail(Loc.T("Não há impressora padrão definida neste PC.", "There is no default printer set on this PC.")); return; }
            if (name.Contains('"')) { Fail(Loc.T("O nome da impressora tem aspas e não pode ser usado com segurança.", "The printer name contains quotes and cannot be used safely.")); return; }
            var r = Proc.Run(Proc.Sys32("rundll32.exe"), $"printui.dll,PrintUIEntry /k /n \"{name}\"", 30000);
            if (!r.Ok) Fail(Loc.T($"Não foi possível enviar a página de teste (código {r.ExitCode}).", $"Could not send the test page (code {r.ExitCode})."));
            else Say(Loc.T($"Página de teste enviada para: {name}", $"Test page sent to: {name}"));
        }

        // ------------------------------------------------------------ rede

        /// <summary>NET_TEST: pings ao gateway, 1.1.1.1 e 8.8.8.8, resolução de DNS e veredito.</summary>
        private async Task NetTestAsync()
        {
            var route = NetDiag.ReadRoute();
            var gw = route.Gateway;
            var dns = route.Dns;
            if (route.AdapterName is not null) Say(Loc.T("Adaptador usado: ", "Adapter used: ") + route.AdapterName);
            var targets = new List<(string Name, IPAddress Address)>();
            if (gw is not null) targets.Add((Loc.T($"roteador ({gw})", $"router ({gw})"), gw));
            targets.Add(("1.1.1.1", IPAddress.Parse("1.1.1.1")));
            targets.Add(("8.8.8.8", IPAddress.Parse("8.8.8.8")));
            if (gw is null) Say(Loc.T("Gateway padrão não encontrado: o teste usa só os destinos da internet.", "Default gateway not found: the test uses only the internet destinations."));
            if (dns.Count > 0) Say(Loc.T("Servidores DNS: ", "DNS servers: ") + string.Join(", ", dns));
            var stats = await NetDiag.PingAsync(targets, m => _log?.Report(m), _ct).ConfigureAwait(false);
            foreach (var s in stats) Say(NetDiag.Describe(s));

            bool? dnsOk = null;
            try
            {
                var sw = Stopwatch.StartNew();
                var task = Dns.GetHostAddressesAsync("www.microsoft.com", _ct);
                var done = await Task.WhenAny(task, Task.Delay(5000, _ct)).ConfigureAwait(false);
                dnsOk = done == task && task.IsCompletedSuccessfully && task.Result.Length > 0;
                Say(dnsOk == true
                    ? Loc.T($"Resolução de nome por DNS: ok ({sw.ElapsedMilliseconds} ms)", $"DNS name resolution: ok ({sw.ElapsedMilliseconds} ms)")
                    : Loc.T("Resolução de nome por DNS: falhou", "DNS name resolution: failed"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { dnsOk = false; Say(Loc.T("Resolução de nome por DNS: falhou", "DNS name resolution: failed")); }

            var gateway = gw is null ? null : stats[0];
            var internet = stats.Skip(gw is null ? 0 : 1).ToList();
            Say(NetDiag.VerdictText(NetDiag.Verdict(gateway, internet, dnsOk)));
        }

        /// <summary>NET_IP_RENEW: libera e renova o endereço IP.</summary>
        private void NetIpRenew()
        {
            var rel = Cmd(Proc.Sys32("ipconfig.exe"), "/release", 60000, _ => true);
            if (rel.TimedOut) { Fail(Loc.T("ipconfig /release excedeu o tempo.", "ipconfig /release timed out.")); return; }
            var ren = Cmd(Proc.Sys32("ipconfig.exe"), "/renew", 90000, _ => true);
            if (!ren.Ok) Fail(Loc.T($"ipconfig /renew falhou (código {ren.ExitCode}). Se o adaptador usa IP fixo, esta ferramenta não se aplica.", $"ipconfig /renew failed (code {ren.ExitCode}). If the adapter uses a fixed IP, this tool does not apply."));
            else Say(Loc.T("Endereço IP renovado", "IP address renewed"));
        }

        /// <summary>NET_STACK_RESET: redefine Winsock e a pilha IP; o efeito completo exige reiniciar (o WinDiag não reinicia).</summary>
        private void NetStackReset()
        {
            var a = Cmd(Proc.Sys32("netsh.exe"), "winsock reset", 60000, _ => true);
            if (a.Ok) R.RebootNeeded = true;
            var b = Cmd(Proc.Sys32("netsh.exe"), "int ip reset", 60000, _ => true);
            if (b.Ok) R.RebootNeeded = true;
            if (!a.Ok || !b.Ok)
                Fail(Loc.T($"A redefinição falhou em parte (winsock: código {a.ExitCode}; ip: código {b.ExitCode}).", $"The reset partly failed (winsock: code {a.ExitCode}; ip: code {b.ExitCode})."));
            else Say(Loc.T("Pilha de rede redefinida. Reinicie o PC para concluir.", "Network stack reset. Restart the PC to finish."));
        }

        /// <summary>TIME_SYNC: inicia o serviço de horário se estiver parado e sincroniza.</summary>
        private void TimeSync()
        {
            if (!ServiceRunning("w32time"))
            {
                if (!StartService("w32time")) { Fail(Loc.T("Não foi possível iniciar o serviço de horário (w32time).", "Could not start the time service (w32time).")); return; }
            }
            Simple(Proc.Sys32("w32tm.exe"), "/resync", 60000, "w32tm /resync");
        }

        // ------------------------------------------------------------ limpeza

        /// <summary>DISK_CLEANUP: limpa as opções marcadas e informa quanto foi liberado.</summary>
        private void DiskCleanupRun()
        {
            bool admin = IsAdministrator();
            long total = 0; int skippedTotal = 0;
            foreach (var id in SelectedOptions())
            {
                _ct.ThrowIfCancellationRequested();
                if (_owner.ElevatedAsOtherUser && DiskCleanup.IsPerUser(id))
                {
                    Say($"{DiskCleanup.Label(id)}: " + OtherUserNote);
                    continue;
                }
                if (DiskCleanup.NeedsAdmin(id) && !admin)
                {
                    Say(Loc.T($"{DiskCleanup.Label(id)}: pulado (exige administrador)", $"{DiskCleanup.Label(id)}: skipped (requires administrator)"));
                    continue;
                }
                if (id == DiskCleanup.Recycle)
                {
                    var (freed, ok) = DiskCleanup.EmptyRecycleBin();
                    total += freed;
                    Say(ok ? Loc.T($"{DiskCleanup.Label(id)}: esvaziada ({DiskCleanup.FormatBytes(freed)})", $"{DiskCleanup.Label(id)}: emptied ({DiskCleanup.FormatBytes(freed)})")
                           : Loc.T($"{DiskCleanup.Label(id)}: não foi possível esvaziar", $"{DiskCleanup.Label(id)}: could not be emptied"));
                    continue;
                }
                CleanResult res = new(0, 0, 0);
                if (id == DiskCleanup.WuCache) res = WithWuServicesStopped(() => CleanTargets(id));
                else res = CleanTargets(id);
                total += res.FreedBytes; skippedTotal += res.Skipped;
                Say(Loc.T($"{DiskCleanup.Label(id)}: {DiskCleanup.FormatBytes(res.FreedBytes)} liberados, {res.Deleted} arquivo(s) apagado(s)" + (res.Skipped > 0 ? $", {res.Skipped} pulado(s) (em uso ou sem acesso)" : ""),
                    $"{DiskCleanup.Label(id)}: {DiskCleanup.FormatBytes(res.FreedBytes)} freed, {res.Deleted} file(s) deleted" + (res.Skipped > 0 ? $", {res.Skipped} skipped (in use or no access)" : "")));
            }
            Say(Loc.T($"Total liberado: {DiskCleanup.FormatBytes(total)}" + (skippedTotal > 0 ? $" ({skippedTotal} item(ns) pulado(s))" : ""),
                $"Total freed: {DiskCleanup.FormatBytes(total)}" + (skippedTotal > 0 ? $" ({skippedTotal} item(s) skipped)" : "")));
        }

        /// <summary>Limpa todos os alvos de uma opção e soma os resultados.</summary>
        /// <param name="id">Id da opção.</param>
        private CleanResult CleanTargets(string id)
        {
            var sum = new CleanResult(0, 0, 0);
            foreach (var t in DiskCleanup.Targets(id, !_owner.ElevatedAsOtherUser)) sum = sum.Plus(DiskCleanup.Clean(t, _ct));
            return sum;
        }

        /// <summary>Para wuauserv e bits (os que estiverem em execução), executa o trabalho e, no finally, religa os que estavam em execução.</summary>
        /// <param name="work">Trabalho com os serviços parados.</param>
        private CleanResult WithWuServicesStopped(Func<CleanResult> work)
        {
            var running = BitsServices.Where(ServiceRunning).ToList();
            try
            {
                foreach (var s in running) StopService(s);
                return work();
            }
            finally
            {
                foreach (var s in Enumerable.Reverse(running))
                    if (!StartService(s)) Say(Loc.T($"Aviso: o serviço {s} não voltou a iniciar; inicie-o em services.msc.", $"Warning: the {s} service did not start again; start it in services.msc."));
            }
        }

        /// <summary>SHADER_CACHE_CLEAR: limpa os caches de shaders e informa o tamanho liberado.</summary>
        private void ShaderCache()
        {
            var sum = new CleanResult(0, 0, 0);
            foreach (var t in DiskCleanup.ShaderTargets()) { _ct.ThrowIfCancellationRequested(); sum = sum.Plus(DiskCleanup.Clean(t, _ct)); }
            Say(Loc.T($"Cache de shaders: {DiskCleanup.FormatBytes(sum.FreedBytes)} liberados, {sum.Deleted} arquivo(s) apagado(s)" + (sum.Skipped > 0 ? $", {sum.Skipped} pulado(s) (em uso)" : ""),
                $"Shader cache: {DiskCleanup.FormatBytes(sum.FreedBytes)} freed, {sum.Deleted} file(s) deleted" + (sum.Skipped > 0 ? $", {sum.Skipped} skipped (in use)" : "")));
        }

        // ------------------------------------------------------------ reparo

        /// <summary>SYS_SFC: sfc /scannow. A saída do sfc é UTF-16; as linhas de percentual vão só ao andamento.</summary>
        private void Sfc()
        {
            var r = Cmd(Proc.Sys32("sfc.exe"), "/scannow", 90 * 60000, l => !l.Contains('%'), Encoding.Unicode);
            if (r.TimedOut) Fail(Loc.T("O SFC excedeu o tempo máximo.", "SFC exceeded the maximum time."));
            else if (!r.Ok) Fail(Loc.T($"O SFC terminou com código {r.ExitCode}. Se apontou arquivos que não conseguiu reparar, rode o DISM e depois o SFC de novo.", $"SFC finished with code {r.ExitCode}. If it reported files it could not repair, run DISM and then SFC again."));
            else Say(Loc.T("SFC concluído (veja as linhas acima)", "SFC finished (see the lines above)"));
        }

        /// <summary>SYS_DISM: DISM /Online /Cleanup-Image /RestoreHealth. O código 3010 significa "concluído, reinício pendente".</summary>
        private void Dism()
        {
            var r = Cmd(Proc.Sys32("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", 120 * 60000, l => !l.Contains('%'));
            if (r.TimedOut) Fail(Loc.T("O DISM excedeu o tempo máximo.", "DISM exceeded the maximum time."));
            else if (r.ExitCode == 3010) { R.RebootNeeded = true; Say(Loc.T("DISM concluído; o Windows pede reinício.", "DISM finished; Windows asks for a restart.")); }
            else if (!r.Ok) Fail(Loc.T($"O DISM terminou com código {r.ExitCode}. Ele precisa de conexão com a internet para buscar os arquivos.", $"DISM finished with code {r.ExitCode}. It needs an internet connection to fetch the files."));
            else Say(Loc.T("DISM concluído", "DISM finished"));
        }

        /// <summary>SYS_CHKDSK: chkdsk &lt;unidade do sistema&gt; /scan (somente leitura).</summary>
        private void Chkdsk()
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var drive = root.TrimEnd('\\');
            var r = Cmd(Proc.Sys32("chkdsk.exe"), $"{drive} /scan", 90 * 60000, l => !l.Contains('%'));
            if (r.TimedOut) Fail(Loc.T("O CHKDSK excedeu o tempo máximo.", "CHKDSK exceeded the maximum time."));
            else if (r.ExitCode == 0) Say(Loc.T("CHKDSK: nenhum erro encontrado", "CHKDSK: no errors found"));
            else if (r.ExitCode == 1) Say(Loc.T("CHKDSK encontrou erros no sistema de arquivos. Nada foi corrigido; veja as linhas acima antes de decidir um reparo.", "CHKDSK found file system errors. Nothing was fixed; read the lines above before deciding on a repair."));
            else Fail(Loc.T($"O CHKDSK terminou com código {r.ExitCode}.", $"CHKDSK finished with code {r.ExitCode}."));
        }

        /// <summary>WU_RESET: para os serviços, renomeia SoftwareDistribution e catroot2 e religa o que estava em execução.</summary>
        private void WuReset()
        {
            var running = WuServices.Where(ServiceRunning).ToList();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string stamp = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            try
            {
                foreach (var s in running) { _ct.ThrowIfCancellationRequested(); StopService(s); }
                foreach (var dir in new[] { Path.Combine(win, "SoftwareDistribution"), Path.Combine(win, "System32", "catroot2") })
                {
                    _ct.ThrowIfCancellationRequested();
                    RenameToOld(dir, stamp);
                }
            }
            finally
            {
                foreach (var s in Enumerable.Reverse(running))
                    if (!StartService(s)) Say(Loc.T($"Aviso: o serviço {s} não voltou a iniciar; inicie-o em services.msc.", $"Warning: the {s} service did not start again; start it in services.msc."));
            }
            Say(Loc.T("Windows Update redefinido. Verifique as atualizações em Configurações > Windows Update.", "Windows Update reset. Check for updates in Settings > Windows Update."));
        }

        /// <summary>Renomeia uma pasta para "&lt;nome&gt;.old-&lt;data&gt;", apagando antes as cópias antigas do WinDiag (".old-*") ao lado dela. Links não são seguidos.</summary>
        /// <param name="dir">Pasta a renomear.</param>
        /// <param name="stamp">Data no formato yyyyMMdd.</param>
        private void RenameToOld(string dir, string stamp)
        {
            var parent = Path.GetDirectoryName(dir)!;
            var name = Path.GetFileName(dir);
            if (Directory.Exists(parent))
            {
                foreach (var old in Directory.GetDirectories(parent, name + ".old-*"))
                {
                    try
                    {
                        if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) continue;
                        Directory.Delete(old, true);
                        Say(Loc.T($"Cópia antiga apagada: {old}", $"Old copy deleted: {old}"));
                    }
                    catch (Exception ex) { Say(Loc.T($"Não foi possível apagar a cópia antiga {old}: {ex.Message}", $"Could not delete the old copy {old}: {ex.Message}")); }
                }
            }
            if (!Directory.Exists(dir)) { Say(Loc.T($"{dir}: não existe, nada a renomear", $"{dir}: does not exist, nothing to rename")); return; }
            var target = dir + ".old-" + stamp;
            if (Directory.Exists(target)) target += DateTime.Now.ToString("-HHmmss", CultureInfo.InvariantCulture);
            Directory.Move(dir, target);
            Say(Loc.T($"Renomeado: {dir} -> {target}", $"Renamed: {dir} -> {target}"));
        }

        /// <summary>EXPLORER_RESTART: encerra o explorer.exe da sessão atual e garante que ele volte.</summary>
        private void ExplorerRestart()
        {
            int sid = Process.GetCurrentProcess().SessionId;
            int killed = 0;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId != sid) continue;
                        p.Kill();
                        p.WaitForExit(5000);
                        killed++;
                    }
                    catch (Exception) { /* sem permissão ou já encerrado */ }
                }
            }
            Say(Loc.T($"Explorador encerrado ({killed} processo(s))", $"Explorer ended ({killed} process(es))"));
            // O Windows costuma reiniciar o shell sozinho; só inicia de novo se, depois de um instante, ele não voltou.
            _ct.WaitHandle.WaitOne(2500);
            bool back = Process.GetProcessesByName("explorer").Any(p => { using (p) { try { return p.SessionId == sid; } catch (Exception) { return false; } } });
            if (!back)
            {
                using var np = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
                Say(Loc.T("Explorador iniciado de novo", "Explorer started again"));
            }
            else Say(Loc.T("O Windows já reiniciou o Explorador", "Windows already restarted Explorer"));
        }

        /// <summary>RESTORE_POINT: cria o ponto de restauração "WinDiag &lt;data&gt;" pelo WMI (SystemRestore.CreateRestorePoint).</summary>
        private void RestorePoint()
        {
            string name = "WinDiag " + DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
            using var mc = new ManagementClass(@"\\.\root\default:SystemRestore");
            using var input = mc.GetMethodParameters("CreateRestorePoint");
            input["Description"] = name;
            input["RestorePointType"] = 12; // MODIFY_SETTINGS
            input["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var output = mc.InvokeMethod("CreateRestorePoint", input, null);
            uint code = output?["ReturnValue"] is { } v ? Convert.ToUInt32(v, CultureInfo.InvariantCulture) : uint.MaxValue;
            if (code == 0)
            {
                // ReturnValue 0 não garante um ponto novo (o Windows limita um por 24 h): confirma na lista de pontos.
                bool found = false;
                for (int i = 0; i < 4 && !found; i++)
                {
                    if (i > 0) _ct.WaitHandle.WaitOne(1500);
                    found = RestorePointCheck.HasRecent(RestorePointCheck.Read(), name, DateTime.Now);
                }
                if (found) Say(Loc.T($"Ponto de restauração criado: {name}", $"Restore point created: {name}"));
                else Fail(Loc.T("O Windows não criou um ponto novo (já existe um recente ou a Proteção do Sistema recusou).", "Windows did not create a new point (a recent one already exists or System Protection refused)."));
            }
            else Fail(Loc.T($"O Windows não criou o ponto de restauração (código {code}). Por padrão ele só cria um a cada 24 horas e exige a Proteção do Sistema ligada.", $"Windows did not create the restore point (code {code}). By default it only creates one every 24 hours and requires System Protection to be on."));
        }

        // ------------------------------------------------------------ relatórios

        /// <summary>BATTERY_REPORT: gera o relatório de bateria na pasta temporária e o abre.</summary>
        private void BatteryReport()
        {
            var file = Path.Combine(Path.GetTempPath(), "windiag-battery-report.html");
            var r = Cmd(Proc.Sys32("powercfg.exe"), $"/batteryreport /output \"{file}\"", 60000, _ => true);
            if (!r.Ok || !File.Exists(file)) { Fail(Loc.T($"O powercfg não gerou o relatório (código {r.ExitCode}).", $"powercfg did not generate the report (code {r.ExitCode}).")); return; }
            OpenUnelevated(file);
        }
    }
}
