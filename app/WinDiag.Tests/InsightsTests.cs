using WinDiag.Core;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.Tests;

public class InsightsTests
{
    /// <summary>Perfil real da máquina de referência (relatório de 08/11/2026).</summary>
    internal static SystemFacts ReferenceMachine() => new()
    {
        CpuName = "Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz", CpuCores = 4, CpuThreads = 8,
        BoardModel = "Gigabyte Technology Co., Ltd. H510M H", Chipset = "H510", BiosVersion = "F20e",
        FirmwareUefi = true, SecureBoot = true,
        RamConfiguredMTs = 2666, RamRatedMTs = 2666, RamType = "DDR4", RamModules = 2, RamChannels = 2, DualChannel = true,
        GpuName = "NVIDIA GeForce RTX 5060", GpuVramMiB = 8151, IsNvidia = true, GpuModern = true,
        GpuPcieGenMax = 5, GpuPcieHostMax = 2, GpuPcieGenCurrent = 2, GpuPcieWidthCurrent = 8, GpuPcieWidthMax = 16,
        Bar1MiB = 8192, ReBarActive = true,
        Displays =
        {
            new DisplayInfo { Width = 1600, Height = 900, RefreshHz = 60, Connection = "HDMI" },
            new DisplayInfo { Width = 1366, Height = 768, RefreshHz = 59.96, Connection = "DP" },
        },
    };

    private static SectionResult Run(SystemFacts f) => Insights.Analyze(f, new List<SectionResult>());

    private static Finding Get(SectionResult r, string titlePart) =>
        r.Findings.First(x => x.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Reference_Section_Identity()
    {
        var r = Run(ReferenceMachine());
        Assert.Equal("analise", r.Key);
        Assert.Equal("Análise da plataforma", r.Title);
        foreach (var g in new[] { "Cadeia PCIe da placa de vídeo", "Memória RAM", "Resizable BAR", "Equilíbrio do sistema", "Caminho de upgrade" })
            Assert.Contains(r.Groups, x => x.Title == g);
    }

    [Fact]
    public void Reference_Pcie_LimitIsConfiguration()
    {
        var r = Run(ReferenceMachine());
        var f = Get(r, "Link PCIe abaixo");
        Assert.Equal(Severity.Atencao, f.Sev);
        Assert.Equal(95, f.Impact);
        Assert.StartsWith("Onde está o limite: configuração", f.Explain);
        Assert.Contains("Gen 3", f.Explain);
        Assert.Contains("BIOS", f.Explain);
        Assert.StartsWith("O que dá para melhorar:", f.Action);
        Assert.Contains("Gen3", f.Action);
        Assert.Contains("(custo: grátis)", f.Action);
        Assert.Contains("4 → 7,9 GB/s", f.Action);
        Assert.Contains("Gen 2", f.Evidence);
        var pcie = r.Groups.First(g => g.Title == "Cadeia PCIe da placa de vídeo").Tables[0];
        Assert.Equal("Elo", pcie.Columns[0]);
        Assert.Equal("Suporta", pcie.Columns[1]);
        Assert.Equal("Observação", pcie.Columns[2]);
        Assert.Contains(pcie.Rows, row => row[0] == "Elo que limita" && row[1].StartsWith("configuração"));
        Assert.Contains(pcie.Rows, row => row[0] == "Chipset e placa" && row[1].Contains("Gen 3"));
    }

    [Fact]
    public void Reference_Ram_IsAtCpuMaximum()
    {
        var r = Run(ReferenceMachine());
        var f = Get(r, "RAM no máximo");
        Assert.Equal(Severity.Ok, f.Sev);
        Assert.Contains("máximo que a CPU suporta", f.Explain);
        Assert.Contains("não ajudaria sem trocar a CPU", f.Explain);
        Assert.StartsWith("Onde está o limite:", f.Explain);
        Assert.DoesNotContain(r.Findings, x => x.Sev >= Severity.Aviso && x.Section == "analise" && x.Title.Contains("RAM"));
    }

    [Fact]
    public void Reference_Upgrade_SuggestsI5_11400F()
    {
        var r = Run(ReferenceMachine());
        var f = Get(r, "Caminho de upgrade");
        Assert.Equal(Severity.Info, f.Sev);
        Assert.Contains("i5-11400F", f.Evidence);
        Assert.Contains("BIOS compatível", f.Explain);
        Assert.Contains("(custo: hardware)", f.Action);
        Assert.Contains("15,8 GB/s", f.Action);
        Assert.Contains("DDR4-3200", f.Action);
    }

    [Fact]
    public void Reference_ReBar_ActiveButLinkLimited()
    {
        var f = Get(Run(ReferenceMachine()), "Resizable BAR");
        Assert.Equal(Severity.Ok, f.Sev);
        Assert.Equal("Resizable BAR ativo", f.Title);
        Assert.Contains("perfil de jogo", f.Explain);
        Assert.Contains("link PCIe", f.Explain);
    }

    [Fact]
    public void Reference_Balance_CpuTendsToLimit()
    {
        var f = Get(Run(ReferenceMachine()), "Equilíbrio");
        Assert.Equal(Severity.Info, f.Sev);
        Assert.Equal(40, f.Impact);
        Assert.Contains("Taxa máxima de quadros", f.Action);
        Assert.Contains("(custo: grátis)", f.Action);
        Assert.Contains("upgrade", f.Action);
        Assert.DoesNotContain("100%", f.Explain + f.Action);
    }

    [Fact]
    public void Platform_Limits_IsInfo_NotAttention()
    {
        var f = ReferenceMachine();
        f.CpuName = "Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz"; f.CpuCores = 6; f.CpuThreads = 12;
        f.BoardModel = "ASRock H410M-HDV"; f.Chipset = null;
        f.GpuPcieHostMax = 3; f.GpuPcieGenCurrent = 3;
        var r = Run(f);
        var p = Get(r, "Link PCIe limitado pela plataforma");
        Assert.Equal(Severity.Info, p.Sev);
        Assert.Equal(30, p.Impact);
        Assert.StartsWith("Onde está o limite: processador e chipset", p.Explain);
        Assert.DoesNotContain(r.Findings, x => x.Sev == Severity.Atencao);
    }

    [Fact]
    public void Ram_XmpOff_OnRyzenB550_IsAviso()
    {
        var f = new SystemFacts
        {
            CpuName = "AMD Ryzen 5 5600 6-Core Processor", CpuCores = 6, CpuThreads = 12,
            BoardModel = "MSI MAG B550 TOMAHAWK", RamConfiguredMTs = 2133, RamRatedMTs = 3200, RamType = "DDR4",
            RamModules = 2, DualChannel = true,
        };
        var r = Run(f);
        var x = Get(r, "XMP/DOCP desligado");
        Assert.Equal(Severity.Aviso, x.Sev);
        Assert.StartsWith("Onde está o limite: configuração da BIOS", x.Explain);
        Assert.Contains("(custo: grátis)", x.Action);
        Assert.Contains("+50%", x.Action);
    }

    [Fact]
    public void Ram_SingleChannel_IsAviso()
    {
        var f = ReferenceMachine();
        f.RamModules = 1; f.DualChannel = false; f.RamChannels = 1;
        var m = Get(Run(f), "canal único");
        Assert.Equal(Severity.Aviso, m.Sev);
        Assert.Contains("(custo: hardware)", m.Action);
    }

    [Fact]
    public void Ram_SameChannel_FixIsFree()
    {
        var f = ReferenceMachine();
        f.DualChannel = false; f.RamChannels = 1;
        var m = Get(Run(f), "mesmo canal");
        Assert.Contains("(custo: grátis)", m.Action);
    }

    [Fact]
    public void Unknown_Hardware_DoesNotClaimCause()
    {
        var f = ReferenceMachine();
        f.CpuName = "Processador Desconhecido 9000"; f.BoardModel = "Placa Genérica"; f.Chipset = null;
        f.GpuName = "Fabricante Estranho X1"; f.GpuPcieGenMax = 4; f.GpuPcieHostMax = 2;
        var r = Run(f);
        var p = Get(r, "Link PCIe abaixo do máximo da GPU");
        // Sem plataforma reconhecida não afirma a causa, mas Gen 2 ainda é destacado (a verificação da GPU agora é só INFO).
        Assert.Equal(Severity.Aviso, p.Sev);
        Assert.Equal(70, p.Impact);
        Assert.Contains("não foi possível separar", p.Explain);
        Assert.DoesNotContain(r.Findings, x => x.Sev == Severity.Atencao);

        // Gen 3 com plataforma desconhecida continua só informativo.
        f.GpuPcieHostMax = 3;
        var p3 = Get(Run(f), "Link PCIe abaixo do máximo da GPU");
        Assert.Equal(Severity.Info, p3.Sev);
        Assert.Equal(20, p3.Impact);
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("Caminho de upgrade"));
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Empty_Facts_DoNotThrow()
    {
        var r = Run(new SystemFacts());
        Assert.Equal("analise", r.Key);
        Assert.DoesNotContain(r.Findings, x => x.Sev >= Severity.Aviso);
    }

    [Fact]
    public void ReBar_Off_WithPrerequisites_IsAviso()
    {
        var f = ReferenceMachine();
        f.GpuPcieHostMax = 3; f.Bar1MiB = 256; f.ReBarActive = false;
        var b = Get(Run(f), "Resizable BAR desligado");
        Assert.Equal(Severity.Aviso, b.Sev);
        Assert.Contains("Above 4G Decoding", b.Action);
    }

    [Fact]
    public void ReBar_Off_InLegacyBoot_IsInfo()
    {
        var f = ReferenceMachine();
        f.Bar1MiB = 256; f.ReBarActive = false; f.FirmwareUefi = false;
        var b = Get(Run(f), "Resizable BAR desligado");
        Assert.Equal(Severity.Info, b.Sev);
    }

    // ---------------- PlatformDb

    [Theory]
    [InlineData("Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz", 10, 3, 2666)]
    [InlineData("Intel(R) Core(TM) i3-10105 CPU @ 3.70GHz", 10, 3, 2666)]
    [InlineData("Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", 10, 3, 2933)]
    [InlineData("Intel(R) Core(TM) i5-11400F @ 2.60GHz", 11, 4, 3200)]
    [InlineData("Intel(R) Core(TM) i3-8100 CPU @ 3.60GHz", 8, 3, 2400)]
    [InlineData("Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz", 8, 3, 2666)]
    [InlineData("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", 6, 3, 2133)]
    [InlineData("Intel(R) Core(TM) i5-12400F", 12, 5, 3200)]
    public void ResolveCpu_Intel(string name, int gen, int pcie, int ddr4)
    {
        var c = PlatformDb.ResolveCpu(name);
        Assert.NotNull(c);
        Assert.Equal(gen, c!.Gen);
        Assert.Equal(pcie, c.PcieGen);
        Assert.Equal(ddr4, c.Ddr4MTs);
    }

    [Theory]
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor", 3, 2666)]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor", 3, 2933)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", 4, 3200)]
    [InlineData("AMD Ryzen 5 5600 6-Core Processor", 4, 3200)]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", 3, 3200)]
    [InlineData("AMD Ryzen 7 5700G with Radeon Graphics", 3, 3200)]
    [InlineData("AMD Ryzen 5 5500", 3, 3200)]
    public void ResolveCpu_Amd(string name, int pcie, int ddr4)
    {
        var c = PlatformDb.ResolveCpu(name);
        Assert.NotNull(c);
        Assert.Equal("AMD", c!.Vendor);
        Assert.Equal(pcie, c.PcieGen);
        Assert.Equal(ddr4, c.Ddr4MTs);
    }

    [Fact]
    public void ResolveCpu_Am5_UsesDdr5()
    {
        var c = PlatformDb.ResolveCpu("AMD Ryzen 5 7600X 6-Core Processor");
        Assert.NotNull(c);
        Assert.Equal(5200, c!.MaxRamMTs("DDR5"));
        Assert.Null(c.MaxRamMTs("DDR4"));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-1165G7 @ 2.80GHz")]
    [InlineData("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz")]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics")]
    [InlineData("Qualquer coisa")]
    [InlineData("")]
    public void ResolveCpu_UnknownOrMobile_IsNull(string name) => Assert.Null(PlatformDb.ResolveCpu(name));

    [Fact]
    public void ResolveChipset_FromBoardName_AndGenerations()
    {
        var h510 = PlatformDb.ResolveChipset("Gigabyte Technology Co., Ltd. H510M H");
        Assert.NotNull(h510);
        Assert.Equal("H510", h510!.Name);
        Assert.False(h510.MemoryOcAllowed);
        Assert.Equal(3, h510.X16Gen(PlatformDb.ResolveCpu("Intel(R) Core(TM) i3-10100F")));
        Assert.Equal(4, h510.X16Gen(PlatformDb.ResolveCpu("Intel(R) Core(TM) i5-11400F")));
        Assert.Null(h510.X16Gen(PlatformDb.ResolveCpu("Intel(R) Core(TM) i5-12400F")));
        Assert.True(PlatformDb.ResolveChipset("MSI MAG B550 TOMAHAWK")!.MemoryOcAllowed);
        Assert.Equal("B650E", PlatformDb.ResolveChipset("ASUS ROG STRIX B650E-F")!.Name);
        Assert.Equal("B650", PlatformDb.ResolveChipset("ASUS TUF B650M-PLUS")!.Name);
        Assert.Null(PlatformDb.ResolveChipset("Placa Genérica"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5060", 5, 8)]
    [InlineData("NVIDIA GeForce RTX 5060 Ti", 5, 8)]
    [InlineData("NVIDIA GeForce RTX 5080", 5, 16)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", 4, 8)]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 4, 16)]
    [InlineData("NVIDIA GeForce RTX 3050", 4, 8)]
    [InlineData("NVIDIA GeForce RTX 3070", 4, 16)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", 3, 16)]
    [InlineData("NVIDIA GeForce RTX 2060", 3, 16)]
    [InlineData("AMD Radeon RX 6500 XT", 4, 4)]
    [InlineData("AMD Radeon RX 7600", 4, 8)]
    [InlineData("AMD Radeon RX 6700 XT", 4, 16)]
    [InlineData("AMD Radeon RX 9070 XT", 5, 16)]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", 4, 8)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 4, 16)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 4, 8)]
    public void ResolveGpu_Table(string name, int gen, int lanes)
    {
        var g = PlatformDb.ResolveGpu(name);
        Assert.NotNull(g);
        Assert.Equal(gen, g!.Gen);
        Assert.Equal(lanes, g.Lanes);
    }

    [Fact]
    public void ResolveGpu_Unknown_IsNull()
    {
        Assert.Null(PlatformDb.ResolveGpu("Microsoft Basic Display Adapter"));
        Assert.Null(PlatformDb.ResolveGpu(null));
    }

    [Fact]
    public void UpgradePath_H510_Gives11400F_And_AM4_Gives5600()
    {
        var up = PlatformDb.UpgradePath(PlatformDb.ResolveCpu("Intel(R) Core(TM) i3-10100F"), PlatformDb.ResolveChipset("H510"), 4, 5);
        Assert.Equal("Core i5-11400F", up!.Cpu);
        Assert.Equal(4, up.PcieGen);
        Assert.Equal(3200, up.RamMTs);
        var am4 = PlatformDb.UpgradePath(PlatformDb.ResolveCpu("AMD Ryzen 5 2600"), PlatformDb.ResolveChipset("B450"), 6, 4);
        Assert.Equal("Ryzen 5 5600", am4!.Cpu);
        Assert.Equal(3, am4.PcieGen);
        Assert.Null(PlatformDb.UpgradePath(PlatformDb.ResolveCpu("AMD Ryzen 5 5600"), PlatformDb.ResolveChipset("B550"), 6, 4));
    }

    [Fact]
    public void ScanEngine_SectionCount_IncludesAnalysis() =>
        Assert.Equal(ScanEngine.CreateCollectors().Count + 1, ScanEngine.SectionCount);
}
