using System.Text;
using WinDiag.Core;
using WinDiag.Core.Model;
using WinDiag.Core.Report;

namespace WinDiag.Tests;

public class ReportTests
{
    [Fact]
    public void Serial_Mascara_Ultimos4()
    {
        Assert.Equal("****274B", ReportMask.Serial("E558274B"));
        Assert.Equal("****", ReportMask.Serial("AB12"));
        Assert.Equal("n/d", ReportMask.Serial("n/d"));
        Assert.True(ReportMask.IsSerialName("Número de série"));
        Assert.True(ReportMask.IsSerialName("Serial"));
        Assert.False(ReportMask.IsSerialName("Modelo"));
    }

    [Fact]
    public void Usuario_Mascarado_Em_Caminho()
    {
        var r = ReportMask.User(@"C:\Users\Fulano\AppData\x", "fulano");
        Assert.Equal(@"C:\Users\<USUARIO>\AppData\x", r);
    }

    private static ScanEngine Manual()
    {
        var e = new ScanEngine();
        var s = new SectionResult { Key = "sys", Title = "Sistema" };
        var g = s.Group("Teste");
        g.Add("Número de série", "E558274B");
        g.Add("Nota", "<script>alert(1)</script>");
        g.Table("Discos", "Modelo", "Série").AddRow("X", "ABCDEFGH");
        s.Find(Severity.Aviso, "Algo", "evidência", "significa", "fazer", impact: 50);
        s.Find(Severity.Ok, "Bom", "ok");
        s.Note("S.M.A.R.T.: exige administrador");
        e.Results.Add(s);
        return e;
    }

    [Fact]
    public void Html_Codifica_E_Sem_Script()
    {
        var html = ReportWriter.BuildHtml(Manual());
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("****274B", html);
        Assert.Contains("****EFGH", html);
        Assert.DoesNotContain("E558274B", html);
        Assert.Contains("lang=\"pt-BR\"", html);
    }

    [Fact]
    public void DefaultFileName_Segue_Padrao()
    {
        Assert.Matches(@"^WinDiag_Relatorio_\d{8}_\d{4}\.html$", ReportWriter.DefaultFileName("html"));
        Assert.Matches(@"^WinDiag_Relatorio_\d{8}_\d{4}\.txt$", ReportWriter.DefaultFileName(".txt"));
    }

    [Fact]
    public void WriteHtml_E_WriteTxt_Gravam_Arquivo_Unico()
    {
        var dir = Path.Combine(Path.GetTempPath(), "windiag_rpt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var txt = Path.Combine(dir, ReportWriter.DefaultFileName("txt"));
            var html = Path.Combine(dir, ReportWriter.DefaultFileName("html"));
            var e = Manual();
            ReportWriter.WriteTxt(e, txt);
            ReportWriter.WriteHtml(e, html);
            Assert.True(File.Exists(txt));
            Assert.True(File.Exists(html));
            var bytes = File.ReadAllBytes(txt);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var t = File.ReadAllText(txt, Encoding.UTF8);
            Assert.Contains($"{AppInfo.Title} - RELATÓRIO".ToUpperInvariant(), t.ToUpperInvariant());
            Assert.Contains(AppInfo.Version, t);
            Assert.Contains("DIAGNÓSTICO", t);
            Assert.Contains("Leituras parciais", t);
            Assert.Contains("prefers-color-scheme", File.ReadAllText(html, Encoding.UTF8));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Txt_Rotulo_Longo_Tem_Dois_Espacos_E_Findings_Alinhados()
    {
        var e = new ScanEngine();
        var s = new SectionResult { Key = "sys", Title = "Sistema" };
        s.Group("Energia").Add("Fast Startup / Hibernação", "desligado / desligada").Add("Virtualização no firmware", "ativa");
        s.Find(Severity.Aviso, "Titulo", string.Join(' ', Enumerable.Repeat("palavra", 30)));
        e.Results.Add(s);
        var lines = ReportWriter.BuildTxt(e).Split("\r\n");
        Assert.Contains(lines, l => l.StartsWith("  Fast Startup / Hibernação:  desligado"));
        Assert.Contains(lines, l => l.StartsWith("  Virtualização no firmware:  ativa"));
        int i = Array.FindIndex(lines, l => l.StartsWith("  [ AVISO ] Titulo"));
        Assert.True(i >= 0);
        Assert.StartsWith("            palavra", lines[i + 1]); // 12 espaços = largura de "  [ AVISO ] "
    }

    [Fact]
    public void Secao_Analise_Vem_Antes_Do_Resumo()
    {
        var e = Manual();
        var a = new SectionResult { Key = "analise", Title = "Análise da plataforma" };
        a.Find(Severity.Info, "Limite", "evidência");
        e.Results.Add(a);
        var html = ReportWriter.BuildHtml(e);
        Assert.True(html.IndexOf("Análise da plataforma", StringComparison.Ordinal) < html.IndexOf(">Resumo<", StringComparison.Ordinal));
        var txt = ReportWriter.BuildTxt(e);
        Assert.True(txt.IndexOf("ANÁLISE DA PLATAFORMA", StringComparison.Ordinal) < txt.IndexOf("SISTEMA ====", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_Compat_Gera_Dois_Arquivos()
    {
        var dir = Path.Combine(Path.GetTempPath(), "windiag_rpt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (txt, html) = ReportWriter.Write(Manual(), dir);
            Assert.True(File.Exists(txt) && File.Exists(html));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Versao_Do_Assembly_E_1_0_0()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
    }

    [Fact]
    public async Task Manual_RelatorioReal_Em_Pasta_Temporaria()
    {
        if (Environment.GetEnvironmentVariable("WINDIAG_REAL_REPORT") != "1") return;
        var e = new ScanEngine();
        await e.RunAsync();
        var dir = Environment.GetEnvironmentVariable("WINDIAG_REAL_REPORT_DIR") ?? Path.Combine(Path.GetTempPath(), "windiag_real_report");
        var (txt, html) = ReportWriter.Write(e, dir);
        Assert.True(File.Exists(txt) && File.Exists(html));
    }
}
