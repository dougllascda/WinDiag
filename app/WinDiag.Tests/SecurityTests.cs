using WinDiag.Core.Collectors;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

public class SecurityTests
{
    [Fact]
    public void ProductState_Ativo_AssinaturasEmDia()
    {
        var (on, old) = SecurityCollector.DecodeProductState(397568);
        Assert.True(on);
        Assert.False(old);
    }

    [Fact]
    public void ProductState_Inativo()
    {
        var (on, _) = SecurityCollector.DecodeProductState(393472);
        Assert.False(on);
    }

    [Fact]
    public void Fltmc_Parse()
    {
        const string txt = "\r\nNome do Filtro                Núm. de Instâncias   Altitude    Quadro\r\n------------------------------  -------------  ------------  -----\r\nbindflt                                 1       409800         0\r\nWdFilter                                7       328010         0\r\nklif                                    9       323600         0\r\n";
        var l = Filters.Parse(txt);
        Assert.Equal(3, l.Count);
        Assert.Equal("WdFilter", l[1].Name);
        Assert.Equal(7, l[1].Instances);
        Assert.Equal("328010", l[1].Altitude);
        Assert.Contains("Kaspersky", Filters.Describe("klif"));
        Assert.Contains("Defender", Filters.Describe("WdFilter"));
    }

    [Fact]
    public void StartupApproved_Bytes()
    {
        Assert.True(Startup.IsEnabled(new byte[] { 0x02, 0, 0, 0 }));
        Assert.False(Startup.IsEnabled(new byte[] { 0x03, 0, 0, 0 }));
        Assert.True(Startup.IsEnabled(null));
    }
}
