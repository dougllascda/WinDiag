using WinDiag.Core.Live;
using Xunit;

namespace WinDiag.Tests;

public class LiveTests
{
    [Fact]
    public void Percentile_PortaDeGetPctl()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double?)i).ToList();
        Assert.Equal(50, Capture.Percentile(v, 50));
        Assert.Equal(95, Capture.Percentile(v, 95));
        Assert.Equal(99, Capture.Percentile(v, 99));
        Assert.Equal(100, Capture.Percentile(v, 100));
        Assert.Equal(1, Capture.Percentile(v, 0));
        Assert.Equal(0, Capture.Percentile(new double?[] { null }, 95));
        Assert.Equal(7, Capture.Percentile(new double?[] { 7, null }, 95));
        // 5 valores: ceil(0,95*5)-1 = 4 -> maior; ceil(0,5*5)-1 = 2 -> mediana
        var five = new double?[] { 5, 1, 4, 2, 3 };
        Assert.Equal(5, Capture.Percentile(five, 95));
        Assert.Equal(3, Capture.Percentile(five, 50));
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("pid_7_luid_0x0_0x1_phys_0_eng_3_engtype_Copy", 7)]
    public void ParseGpuPid_Extrai(string inst, int pid) => Assert.Equal(pid, Capture.ParseGpuPid(inst));

    [Theory]
    [InlineData("")]
    [InlineData("luid_0x0_phys_0")]
    public void ParseGpuPid_Invalido(string inst) => Assert.Null(Capture.ParseGpuPid(inst));

    [Fact]
    public void Exclusao_SistemaAntivirusENvidia()
    {
        Assert.True(GameSession.IsExcluded("x", @"C:\Windows\System32\x.exe"));
        Assert.True(GameSession.IsExcluded("nvcontainer", null));
        Assert.True(GameSession.IsExcluded("NVDisplay.Container", null));
        Assert.True(GameSession.IsExcluded("vmmemWSL", null));
        Assert.True(GameSession.IsExcluded("MsMpEng", null));
        Assert.True(GameSession.IsExcluded("avp#2", null));
        Assert.True(GameSession.IsExcluded("WinDiag", null));
        Assert.False(GameSession.IsExcluded("chrome", @"C:\Program Files\Google\Chrome\chrome.exe"));
        Assert.False(GameSession.IsExcluded("claude", null));
        Assert.False(GameSession.IsExcluded("node", null));
    }

    [Fact]
    public void ClaudeENode_ComAviso()
    {
        Assert.Contains("assistente Claude", GameSession.WarningFor("claude"));
        Assert.Contains("assistente Claude", GameSession.WarningFor("node"));
        Assert.Equal("", GameSession.WarningFor("chrome"));
    }

    [Fact]
    public void Resumo_CpuLimitada()
    {
        var s = Enumerable.Range(1, 10).Select(i => new CaptureSample { Second = i, CpuPct = 97, Gpu3DPct = 40, DpcPct = 0.5 }).ToList();
        var r = Capture.Summarize(s);
        Assert.Contains(r.Interpretation, t => t.Contains("CPU-bound"));
    }

    [Fact]
    public void Resumo_PoucasAmostras()
    {
        var r = Capture.Summarize(new[] { new CaptureSample { Second = 1 } });
        Assert.Contains(r.Interpretation, t => t.Contains("Poucas amostras"));
    }

    [Fact]
    public async Task Capture_3s_NaoLanca()
    {
        var r = await Capture.RunAsync(3, null, CancellationToken.None);
        Assert.InRange(r.SampleCount, 1, 3);
    }
}
