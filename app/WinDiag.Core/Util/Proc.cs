using System.Diagnostics;
using System.Text;

namespace WinDiag.Core.Util;

/// <summary>Execução de ferramentas do Windows (nvidia-smi, powercfg, bcdedit, fltmc) sem janela e com timeout.</summary>
public static class Proc
{
    private static readonly string[] NewLines = { "\r\n", "\n" };

    /// <summary>Codificação OEM do console do sistema (CP850 em pt-BR); cai para UTF-8 se a página não estiver disponível.</summary>
    public static Encoding OemEncoding { get; } = ResolveOem();

    private static Encoding ResolveOem()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
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
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new Result(-1, "", "tempo esgotado", true);
            }
            return new Result(p.ExitCode, outTask.Result, errTask.Result, false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
    }

    /// <summary>Caminho de uma ferramenta de System32 (ex.: "powercfg.exe").</summary>
    public static string Sys32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
