using WinDiag.Core.Collectors;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

public class GpuTests
{
    [Fact]
    public void Pcie_Gen2x8_AboutFour() => Assert.Equal(4.0, Pcie.Bandwidth(2, 8));

    [Fact]
    public void Pcie_Gen3x8() => Assert.Equal(7.9, Pcie.Bandwidth(3, 8));

    [Fact]
    public void Pcie_Gen5x8() => Assert.Equal(31.5, Pcie.Bandwidth(5, 8));

    [Fact]
    public void Pcie_Invalid_IsNull()
    {
        Assert.Null(Pcie.Bandwidth(0, 8));
        Assert.Null(Pcie.Bandwidth(3, 0));
    }

    [Fact]
    public void Smi_CsvLine_NaBecomesNull()
    {
        var r = NvidiaSmi.ParseCsvLine("60, 2535, [N/A], 29.97, 3, 656, [Not Supported], P0");
        Assert.Equal(8, r.Length);
        Assert.Equal("60", r[0]);
        Assert.Null(r[2]);
        Assert.Equal("29.97", r[3]);
        Assert.Null(r[6]);
        Assert.Equal("P0", r[7]);
    }

    [Fact]
    public void Smi_EventReasons_Mask()
    {
        Assert.Equal("nenhuma", NvidiaSmi.DescribeEventReasons("0x0000000000000000"));
        Assert.Equal("Limite de potência (software)", NvidiaSmi.DescribeEventReasons("0x0000000000000004"));
    }

    [Fact]
    public void Smi_BoardVendorAndDriver()
    {
        Assert.Equal("MSI", NvidiaSmi.BoardVendor("0x50801462"));
        Assert.Equal("617.42", NvidiaSmi.FormatNvidiaDriver("32.0.16.1742"));
    }

    [Fact]
    public void Monitor_NameDecodedFromUshortArray()
    {
        var man = new ushort[] { 65, 79, 67, 0 };
        var name = new ushort[] { 50, 48, 53, 48, 0, 0, 0 };
        Assert.Equal("AOC 2050", $"{DisplayCollector.DecodeWmi(man)} {DisplayCollector.DecodeWmi(name)}".Trim());
        Assert.Equal("", DisplayCollector.DecodeWmi(null));
    }

    [Fact]
    public void Monitor_IdNormalizationMatchesWmiAndGdi()
    {
        var gdi = @"\\?\DISPLAY#AOC2050#5&1c2d3e4&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var wmi = @"DISPLAY\AOC2050\5&1c2d3e4&0&UID4352_0";
        Assert.Equal(DisplayCollector.NormalizeMonitorId(wmi), DisplayCollector.NormalizeMonitorId(gdi));
    }
}
