using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

/// <summary>Testes das correções do backend: exclusão do processo atual, data com hora, leitura do MMAgent e parse sem cultura.</summary>
public class BackendFixTests
{
    private static readonly int[] Esperado = { 100, 200, 400 };

    [Fact]
    public void SelfAndChildren_IncluiOProcessoAtualEOsFilhosDiretos()
    {
        var procs = new[] { (Pid: 100, Parent: 1), (Pid: 200, Parent: 100), (Pid: 300, Parent: 200), (Pid: 400, Parent: 100), (Pid: 500, Parent: 7) };
        var set = ProcessCollector.SelfAndChildren(100, procs);
        Assert.Equal(Esperado, set.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void SelfAndChildren_SemListaMantemSoOProprio()
    {
        var set = ProcessCollector.SelfAndChildren(Environment.ProcessId, Array.Empty<(int, int)>());
        Assert.Single(set);
        Assert.Contains(Environment.ProcessId, set);
    }

    [Fact]
    public void ColetorDeProcessosNaoListaOProprioProcesso()
    {
        var res = new ProcessCollector().Collect(new WinDiag.Core.Model.SystemFacts());
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = res.Groups.SelectMany(g => g.Tables).Where(t => t.Title.StartsWith("Processos")).SelectMany(t => t.Rows);
        Assert.DoesNotContain(cpu, row => row.Count > 1 && row[1] == Environment.ProcessId.ToString());
    }

    [Fact]
    public void DataComHora_UsaDiaMesAnoEHoraMinuto() =>
        Assert.Equal("08/11/2026 08:17", Fmt.DateTime(new DateTime(2026, 11, 8, 8, 17, 42)));

    [Fact]
    public void DataComHora_NuloVira_ND() => Assert.Equal("n/d", Fmt.DateTime(null));

    [Theory]
    [InlineData("True|False|True\r\n", true, false, true)]
    [InlineData("False|True|False", false, true, false)]
    [InlineData("false|x|TRUE", false, null, true)]
    public void ParseMmAgent_LeTresCampos(string saida, bool? mc, bool? pc, bool? pl)
    {
        var r = MemoryCollector.ParseMmAgent(saida);
        Assert.Equal(mc, r.Compression);
        Assert.Equal(pc, r.PageCombining);
        Assert.Equal(pl, r.PreLaunch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("erro qualquer")]
    [InlineData(null)]
    public void ParseMmAgent_SaidaInvalidaDevolveNulos(string? saida)
    {
        var r = MemoryCollector.ParseMmAgent(saida);
        Assert.Null(r.Compression);
        Assert.Null(r.PageCombining);
        Assert.Null(r.PreLaunch);
    }

    [Fact]
    public void Filters_ParseIgnoraCulturaELinhasForaDoFormato()
    {
        var l = Filters.Parse("Nome do Filtro  Instâncias  Altitude  Quadro\r\n-----\r\nWdFilter  9  328010  0\r\nbindflt  1  409800  0\r\n");
        Assert.Equal(2, l.Count);
        Assert.Equal("WdFilter", l[0].Name);
        Assert.Equal(9, l[0].Instances);
    }

    [Fact]
    public void WmiDisposeAll_AceitaNuloELista()
    {
        Wmi.DisposeAll(null);
        Wmi.DisposeAll(new List<System.Management.ManagementBaseObject>());
    }

    [Fact]
    public void Capture_ParseGpuPid()
    {
        Assert.Equal(1234, Core.Live.Capture.ParseGpuPid("pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D"));
        Assert.Null(Core.Live.Capture.ParseGpuPid(null));
    }
}
