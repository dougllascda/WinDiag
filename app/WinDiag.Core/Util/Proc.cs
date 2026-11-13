using System.Diagnostics;
using System.Text;

namespace WinDiag.Core.Util;

/// <summary>Execução de ferramentas do Windows (nvidia-smi, powercfg, bcdedit, fltmc) sem janela e com timeout.</summary>
public static class Proc
{
    /// <summary>Separadores de linha da saída de ferramentas de console.</summary>
    private static readonly string[] NewLines = { "\r\n", "\n" };

    /// <summary>Página de código OEM real do sistema (kernel32 GetOEMCP). Independe da cultura do app: o WinDiag troca a cultura para en-US no modo inglês, mas as ferramentas do Windows continuam imprimindo na página OEM do sistema (ex.: CP850 em pt-BR).</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>Codificação OEM do console do sistema (CP850 em pt-BR); cai para UTF-8 se a página não estiver disponível.</summary>
    public static Encoding OemEncoding { get; } = ResolveOem();

    /// <summary>Resolve a página de código OEM do sistema (GetOEMCP). Registra o provedor de code pages porque o .NET moderno só traz UTF-8/Unicode por padrão.</summary>
    private static Encoding ResolveOem()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return ResolveOem((int)GetOEMCP());
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>Codificação de uma página de código; UTF-8 se o número for inválido ou a página não existir.</summary>
    /// <param name="codePage">Número da página (ex.: 850).</param>
    public static Encoding ResolveOem(int codePage)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return codePage > 0 ? Encoding.GetEncoding(codePage) : Encoding.UTF8;
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>Resultado de uma execução.</summary>
    /// <param name="ExitCode">Código de saída (-1 se não iniciou ou estourou o tempo).</param>
    /// <param name="StdOut">Saída padrão.</param>
    /// <param name="StdErr">Saída de erro (ou o motivo da falha ao iniciar).</param>
    /// <param name="TimedOut">true se o tempo esgotou e o processo foi encerrado.</param>
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        /// <summary>true se terminou a tempo com código 0.</summary>
        public bool Ok => !TimedOut && ExitCode == 0;
        /// <summary>Saída padrão separada em linhas.</summary>
        public string[] Lines => StdOut.Split(NewLines, StringSplitOptions.None);
    }

    /// <summary>Executa um programa sem janela, capturando as saídas. Nunca lança exceção: falhas voltam em <see cref="Result"/>.</summary>
    /// <param name="exe">Caminho do executável.</param>
    /// <param name="args">Argumentos.</param>
    /// <param name="timeoutMs">Tempo máximo; ao estourar o processo (e filhos) é encerrado.</param>
    /// <param name="encoding">Codificação da saída. Null = página OEM do console (ex.: CP850 em pt-BR), usada pelas ferramentas do Windows; o nvidia-smi passa <see cref="Encoding.UTF8"/>.</param>
    public static Result Run(string exe, string args, int timeoutMs = 15000, Encoding? encoding = null)
    {
        try
        {
            var enc = encoding ?? OemEncoding;
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
            };
            using var p = Process.Start(psi);
            if (p is null) return new Result(-1, "", Loc.T("não iniciou", "did not start"), false);
            // Leitura assíncrona de stdout e stderr ao mesmo tempo evita deadlock quando o buffer do pipe enche antes de o processo terminar.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                // Kill(true) encerra a árvore de processos, para não deixar filhos órfãos após o timeout.
                try { p.Kill(true); } catch { }
                return new Result(-1, "", Loc.T("tempo esgotado", "timed out"), true);
            }
            return new Result(p.ExitCode, outTask.Result, errTask.Result, false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
    }

    /// <summary>
    /// Executa um programa sem janela entregando a saída linha a linha (stdout e stderr) a <paramref name="onLine"/>. Nunca lança exceção.
    /// Ao cancelar ou estourar o tempo, encerra o processo e seus filhos. Linhas vazias são descartadas e caracteres NUL (comuns na saída UTF-16 do sfc) removidos.
    /// </summary>
    /// <param name="exe">Caminho do executável.</param>
    /// <param name="args">Argumentos.</param>
    /// <param name="onLine">Recebe cada linha de saída, sem quebra de linha.</param>
    /// <param name="ct">Cancelamento: encerra a árvore de processos.</param>
    /// <param name="timeoutMs">Tempo máximo total.</param>
    /// <param name="encoding">Codificação da saída; null = OEM do console. O sfc escreve em UTF-16 (<see cref="Encoding.Unicode"/>).</param>
    /// <returns>Resultado com a saída acumulada; <see cref="Result.TimedOut"/> indica tempo esgotado e, se <paramref name="ct"/> foi cancelado, o código de saída é -1.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Assinatura fixada pelo contrato com o App (CancellationToken antes do tempo máximo).")]
    public static Result RunStreaming(string exe, string args, Action<string> onLine, CancellationToken ct, int timeoutMs, Encoding? encoding = null)
    {
        try
        {
            var enc = encoding ?? OemEncoding;
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
            };
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            var gate = new object();
            void Push(string? raw, StringBuilder target)
            {
                if (raw is null) return;
                var line = raw.Replace("\0", "").Trim();
                if (line.Length == 0) return;
                lock (gate) { if (target.Length < 400_000) target.AppendLine(line); }
                try { onLine(line); } catch (Exception) { /* o chamador não pode derrubar a leitura */ }
            }
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => Push(e.Data, outSb);
            p.ErrorDataReceived += (_, e) => Push(e.Data, errSb);
            if (!p.Start()) return new Result(-1, "", Loc.T("não iniciou", "did not start"), false);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var sw = Stopwatch.StartNew();
            while (!p.WaitForExit(200))
            {
                bool cancelled = ct.IsCancellationRequested;
                if (cancelled || sw.ElapsedMilliseconds > timeoutMs)
                {
                    try { p.Kill(true); } catch (Exception) { /* já terminou */ }
                    try { p.WaitForExit(3000); } catch (Exception) { }
                    string so, se;
                    lock (gate) { so = outSb.ToString(); se = errSb.ToString(); }
                    return cancelled ? new Result(-1, so, Loc.T("cancelado", "cancelled"), false) : new Result(-1, so, Loc.T("tempo esgotado", "timed out"), true);
                }
            }
            p.WaitForExit(); // espera as leituras assíncronas esvaziarem
            lock (gate) return new Result(p.ExitCode, outSb.ToString(), errSb.ToString(), false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
    }
    /// <summary>Caminho de uma ferramenta de System32 (ex.: "powercfg.exe").</summary>
    public static string Sys32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
