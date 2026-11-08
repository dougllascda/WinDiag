using WinDiag.Core.Util;

namespace WinDiag.Tests;

public class SystemTests
{
    [Fact]
    public void RamPart_DecodesCrucial() =>
        Assert.Equal("8 GB, 2666 MT/s, CL16", RamPart.Decode("BL8G26C16U4R.8FD"));

    [Theory]
    [InlineData(21, 2133)]
    [InlineData(26, 2666)]
    [InlineData(40, 4000)]
    [InlineData(18, 1800)]
    public void RamSpeedCode(int code, int mts) => Assert.Equal(mts, RamPart.SpeedCode(code));

    [Fact]
    public void RamPart_UnknownIsNull() => Assert.Null(RamPart.Decode("XYZ123"));

    [Fact]
    public void Codename_CometLake() =>
        Assert.Equal("Comet Lake (14 nm)", CpuCodename.Lookup(true, false, 6, 165));

    [Fact]
    public void Codename_FromDescription() =>
        Assert.Equal("Comet Lake (14 nm)", CpuCodename.FromDescription("Intel64 Family 6 Model 165 Stepping 3", "GenuineIntel"));

    [Fact]
    public void Bcd_ParsesSample()
    {
        const string txt = "Carregador de Inicialização do Windows\r\n-------------------\r\n" +
            "identifier              {current}\r\n" +
            "description             AtlasOS 11 v0.5.0\r\n" +
            "useplatformclock        Yes\r\n" +
            "Nx                      OptIn\r\n";
        var b = Bcd.Parse(txt);
        Assert.Equal("AtlasOS 11 v0.5.0", b["description"]);
        Assert.Equal("Yes", b["useplatformclock"]);
        Assert.Equal("OptIn", b["nx"]);
        Assert.False(b.ContainsKey("windows"));
    }

    [Fact]
    public void PowerCfg_TakesPenultimateHex()
    {
        const string o = "Índice de Configuração de Energia CA atual: 0x00000064\n Índice de Configuração de Energia CC atual: 0x00000032\n";
        Assert.Equal(100, PowerCfg.ParseAc(o));
    }
}
