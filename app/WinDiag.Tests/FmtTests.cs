using WinDiag.Core;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

public class FmtTests
{
    [Fact]
    public void Date_UsesDayMonthYear() =>
        Assert.Equal("07/11/2026", Fmt.Date(new DateTime(2026, 11, 7)));

    [Fact]
    public void Num_UsesCommaDecimal() => Assert.Equal("15,87", Fmt.Num(15.8712));

    [Fact]
    public void Val_EmptyIsNA() => Assert.Equal("n/d", Fmt.Val(""));

    [Fact]
    public void WithUnit_NoUnitWhenMissing() => Assert.Equal("n/d", Fmt.WithUnit(null, "C"));

    [Fact]
    public void RegSplit_AcceptsPowerShellStyle()
    {
        var (root, sub) = Reg.Split(@"HKCU:\Software\Microsoft\GameBar");
        Assert.NotNull(root);
        Assert.Equal(@"Software\Microsoft\GameBar", sub);
    }
}
