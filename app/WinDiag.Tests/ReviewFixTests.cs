using Microsoft.Win32;
using WinDiag.Core.Collectors;
using WinDiag.Core.Fixes;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Live;
using WinDiag.Core.Model;
using WinDiag.Core.Report;
using WinDiag.Core.Rules;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

/// <summary>Testes das correções da revisão do backend (serviços, RAM instalada, canais, PCIe, upgrade, snapshots, captura, máscara, codinome).</summary>
public class ReviewFixTests
{
    // ------------------------------------------------------------------ 1. serviços

    [Theory]
    [InlineData(2, true, "config SysMain start= delayed-auto")]
    [InlineData(2, false, "config SysMain start= auto")]
    [InlineData(3, false, "config SysMain start= demand")]
    [InlineData(4, false, "config SysMain start= disabled")]
    [InlineData(0, false, "config SysMain start= boot")]
    [InlineData(1, false, "config SysMain start= system")]
    [InlineData(3, true, "config SysMain start= demand")] // atraso só vale com Start = 2
    public void Servico_ArgumentosDoScConfig(int start, bool delayed, string esperado) =>
        Assert.Equal(esperado, FixEngine.ServiceConfigArgs("SysMain", start, delayed));

    [Fact]
    public void Servico_TipoDesconhecidoLanca() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FixEngine.ServiceConfigArgs("X", 9, false));

    // ------------------------------------------------------------------ 2. RAM instalada

    [Fact]
    public void Mmc_UsaRamInstalada_32GbVisiveis31_8()
    {
        var f = new SystemFacts { RamGB = 31.8, RamInstalledGB = 32, MemCompression = true, AvailPct = 60, AvailGB = 19 };
        Assert.True(FixCatalog.Get("MMC")!.Applicable(f));
        f = new SystemFacts { RamGB = 31.8, RamInstalledGB = 32, MemCompression = false };
        Assert.True(FixCatalog.Get("MMC")!.IsActive(f));
    }

    [Fact]
    public void MmcOn_UsaRamInstalada_16Gb()
    {
        var f = new SystemFacts { RamGB = 15.9, RamInstalledGB = 16, MemCompression = false, AvailGB = 8, AvailPct = 50 };
        Assert.True(FixCatalog.Get("MMC_ON")!.Applicable(f));
        f.RamInstalledGB = 32; f.RamGB = 31.8;
        Assert.False(FixCatalog.Get("MMC_ON")!.Applicable(f));
    }

    [Fact]
    public void RamSemInstalada_CaiParaVisivel()
    {
        var f = new SystemFacts { RamGB = 15.9 };
        Assert.Equal(15.9, f.RamForThresholdGB);
    }

    // ------------------------------------------------------------------ 3. dual channel

    [Fact]
    public void Canal_ControladoresDiferentesSaoCanaisDistintos()
    {
        var a = MemoryCollector.ChannelKey("Controller0-ChannelA-DIMM0", "BANK 0");
        var b = MemoryCollector.ChannelKey("Controller1-ChannelA-DIMM0", "BANK 0");
        Assert.Equal("Controller0-ChannelA", a);
        Assert.Equal("Controller1-ChannelA", b);
        Assert.NotEqual(a, b);
        Assert.True(MemoryCollector.DualChannelFrom(2, new[] { a, b }.Distinct().Count(), true));
    }

    [Fact]
    public void Canal_MesmoControladorEMesmoCanalNaoEDual()
    {
        var a = MemoryCollector.ChannelKey("Controller0-ChannelA-DIMM0", null);
        var b = MemoryCollector.ChannelKey("Controller0-ChannelA-DIMM1", null);
        Assert.Equal(a, b);
        Assert.False(MemoryCollector.DualChannelFrom(2, 1, true));
    }

    [Fact]
    public void Canal_PadraoNaoReconhecidoDaNull()
    {
        Assert.Null(MemoryCollector.ChannelKey("DIMM 1", "BANK 1"));
        Assert.Null(MemoryCollector.DualChannelFrom(2, 0, false));
        Assert.Null(MemoryCollector.DualChannelFrom(2, 1, false));
        Assert.False(MemoryCollector.DualChannelFrom(1, 0, false));
    }

    // ------------------------------------------------------------------ 4. PCIe: a medição manda

    private static SystemFacts B760Rtx5070(int host) => new()
    {
        CpuName = "Intel(R) Core(TM) i5-13400F", CpuCores = 10, CpuThreads = 16,
        BoardModel = "MSI PRO B760M-A", Chipset = "B760", GpuName = "NVIDIA GeForce RTX 5070",
        IsNvidia = true, GpuModern = true, GpuPcieGenMax = 5, GpuPcieHostMax = host, GpuPcieGenCurrent = host, GpuPcieWidthCurrent = 16, GpuPcieWidthMax = 16,
    };

    [Fact]
    public void Pcie_HostIgualAGpu_NaoDizLimitadoPelaPlataforma()
    {
        var r = Insights.Analyze(B760Rtx5070(5), new List<SectionResult>());
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("limitado pela plataforma", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("abaixo do que a plataforma permite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pcie_HostMenorQueAGpu_AfirmaLimiteComAMedicao()
    {
        var r = Insights.Analyze(B760Rtx5070(4), new List<SectionResult>());
        var f = r.Findings.First(x => x.Title.Contains("limitado pela plataforma", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("medição", f.Explain);
    }

    [Fact]
    public void Pcie_B760_EhGen4OuGen5ConformeAPlaca()
    {
        Assert.True(PlatformDb.ResolveChipset("B760")!.DependsOnBoard);
        Assert.True(PlatformDb.ResolveChipset("B660")!.DependsOnBoard);
        Assert.True(PlatformDb.ResolveChipset("B650")!.DependsOnBoard);
        Assert.True(PlatformDb.ResolveChipset("X670")!.DependsOnBoard);
        Assert.True(PlatformDb.ResolveChipset("A620")!.DependsOnBoard);
        Assert.False(PlatformDb.ResolveChipset("Z790")!.DependsOnBoard);
        var r = Insights.Analyze(B760Rtx5070(4), new List<SectionResult>());
        var t = r.Groups.First(g => g.Title == "Cadeia PCIe da placa de vídeo").Tables[0];
        Assert.Contains(t.Rows, row => row[0] == "Chipset e placa" && row[1].Contains("Gen 4 ou Gen 5 conforme a placa"));
    }

    // ------------------------------------------------------------------ 5. upgrade nunca regride

    [Fact]
    public void Upgrade_I7De10aGeracaoNaoRecebeSugestao()
    {
        var cpu = PlatformDb.ResolveCpu("Intel(R) Core(TM) i7-10700 CPU @ 2.90GHz");
        var chip = PlatformDb.ResolveChipset("B560");
        Assert.Null(PlatformDb.UpgradePath(cpu, chip, 8, 16, 4));
        Assert.Null(PlatformDb.UpgradePath(PlatformDb.ResolveCpu("Intel(R) Core(TM) i9-10900K"), PlatformDb.ResolveChipset("Z490"), 10, 20, 4));
    }

    [Fact]
    public void Upgrade_I3De4NucleosAindaRecebeI5_11400F()
    {
        var up = PlatformDb.UpgradePath(PlatformDb.ResolveCpu("Intel(R) Core(TM) i3-10100F"), PlatformDb.ResolveChipset("B560"), 4, 8, 4);
        Assert.Equal("Core i5-11400F", up?.Cpu);
    }

    [Fact]
    public void Upgrade_Ryzen7ou9NaoRecebeRyzen5_5600()
    {
        var b450 = PlatformDb.ResolveChipset("B450");
        Assert.Null(PlatformDb.UpgradePath(PlatformDb.ResolveCpu("AMD Ryzen 7 3700X 8-Core Processor"), b450, 8, 16, 4));
        Assert.Null(PlatformDb.UpgradePath(PlatformDb.ResolveCpu("AMD Ryzen 9 3900X 12-Core Processor"), b450, 12, 24, 4));
        Assert.Equal("Ryzen 5 5600", PlatformDb.UpgradePath(PlatformDb.ResolveCpu("AMD Ryzen 3 3100 4-Core Processor"), b450, 4, 8, 4)?.Cpu);
    }

    [Fact]
    public void Upgrade_SemContagemDeNucleosNaoSugere() =>
        Assert.Null(PlatformDb.UpgradePath(PlatformDb.ResolveCpu("Intel(R) Core(TM) i3-10100F"), PlatformDb.ResolveChipset("B560"), 0, 0, 4));

    // ------------------------------------------------------------------ 10. GPU de notebook e GTX 1030

    [Fact]
    public void Gpu_NotebookNaoResolve()
    {
        Assert.Null(PlatformDb.ResolveGpu("NVIDIA GeForce RTX 4060 Laptop GPU"));
        Assert.Null(PlatformDb.ResolveGpu("NVIDIA GeForce RTX 3050 Ti Mobile"));
        Assert.Null(PlatformDb.ResolveGpu("NVIDIA GeForce RTX 3070 Max-Q"));
        Assert.Null(PlatformDb.ResolveGpu("NVIDIA GeForce RTX 4060", isLaptop: true));
        Assert.NotNull(PlatformDb.ResolveGpu("NVIDIA GeForce RTX 4060"));
    }

    [Fact]
    public void Gpu_Gtx1030EhX4()
    {
        var g = PlatformDb.ResolveGpu("NVIDIA GeForce GTX 1030")!;
        Assert.Equal(4, g.Lanes);
        Assert.Equal(16, PlatformDb.ResolveGpu("NVIDIA GeForce GTX 1060 6GB")!.Lanes);
    }

    // ------------------------------------------------------------------ 12/13. snapshots

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "snapshots"));
        return dir;
    }

    private static string Snap(string created, string id, string? applied = null, string action = "Aplicar")
    {
        string ap = applied is null ? "" : ", \"Applied\": " + applied;
        return "{ \"Created\": \"" + created + "\", \"Version\": \"1.0.0\", \"Fixes\": [ { \"Id\": \"" + id + "\", \"Name\": \"x\", \"Reversible\": true, \"Action\": \"" + action
            + "\", \"State\": { \"Reg\": [], \"Bcd\": [], \"Power\": [], \"Svc\": [] }" + ap + " } ] }";
    }

    private static string AppliedReg(string path, bool exists, string value) =>
        "{ \"Reg\": [ { \"Path\": \"" + path.Replace("\\", "\\\\") + "\", \"Name\": \"v\", \"Exists\": " + (exists ? "true" : "false")
        + ", \"Value\": " + value + ", \"Kind\": \"DWord\" } ], \"Bcd\": [], \"Power\": [], \"Svc\": [] }";

    private static readonly string[] OrdemEsperada = { "C", "B", "A" };

    [Fact]
    public void Snapshots_MesmoSegundo_DesempataPeloSufixoENoMtime()
    {
        var dir = NewDir();
        try
        {
            var s = Path.Combine(dir, "snapshots");
            File.WriteAllText(Path.Combine(s, "snapshot_20261108_100000.json"), Snap("2026-11-08T10:00:00", "A"));
            File.WriteAllText(Path.Combine(s, "snapshot_20261108_100000_1.json"), Snap("2026-11-08T10:00:00", "B"));
            File.WriteAllText(Path.Combine(s, "snapshot_20261108_100000_2.json"), Snap("2026-11-08T10:00:00", "C"));
            var list = new SnapshotStore(dir).List();
            Assert.Equal(OrdemEsperada, list.Select(x => x.Data.Fixes[0].Id).ToArray());
            Assert.Equal(2, SnapshotStore.FileSuffix("snapshot_20261108_100000_2.json"));
            Assert.Equal(0, SnapshotStore.FileSuffix("snapshot_20261108_100000.json"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Snapshots_MesmoSufixo_MaisNovoNoDiscoVemPrimeiro()
    {
        var dir = NewDir();
        try
        {
            var s = Path.Combine(dir, "snapshots");
            var a = Path.Combine(s, "snapshot_20261108_100000.json");
            File.WriteAllText(a, Snap("2026-11-08T10:00:00", "VELHO"));
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc));
            // Mesma data/sufixo em pasta idêntica não existe; simula pelo método de desempate com outro nome de mesmo sufixo.
            var b = Path.Combine(s, "snapshot_20261108_100000_x.json");
            File.WriteAllText(b, Snap("2026-11-08T10:00:00", "NOVO"));
            File.SetLastWriteTimeUtc(b, new DateTime(2026, 11, 8, 13, 0, 5, DateTimeKind.Utc));
            Assert.Equal("NOVO", new SnapshotStore(dir).List()[0].Data.Fixes[0].Id);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Applied_EstadoIgual_ContinuaAtivoWinDiag()
    {
        var key = @"HKCU:\Software\WinDiagTest_" + Guid.NewGuid().ToString("N"); // chave inexistente
        var applied = AppliedReg(key, false, "null");
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "snapshots", "snapshot_20261108_100000.json"), Snap("2026-11-08T10:00:00", "GAMEDVR", applied));
            var st = new FixEngine(new SnapshotStore(dir), true).GetStatuses(new SystemFacts { GameDVR = 0 }).Single(x => x.Fix.Id == "GAMEDVR");
            Assert.Equal(FixStatusKind.AtivoWinDiag, st.Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Applied_EstadoDiferente_ViraAtivoExterno()
    {
        var key = @"HKCU:\Software\WinDiagTest_" + Guid.NewGuid().ToString("N"); // não existe, mas o snapshot diz que existia com 0
        var applied = AppliedReg(key, true, "0");
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "snapshots", "snapshot_20261108_100000.json"), Snap("2026-11-08T10:00:00", "GAMEDVR", applied));
            var st = new FixEngine(new SnapshotStore(dir), true).GetStatuses(new SystemFacts { GameDVR = 0 }).Single(x => x.Fix.Id == "GAMEDVR");
            Assert.Equal(FixStatusKind.AtivoExterno, st.Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Applied_Ausente_MantemComportamentoAntigo()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "snapshots", "snapshot_20261108_100000.json"), Snap("2026-11-08T10:00:00", "GAMEDVR"));
            var st = new FixEngine(new SnapshotStore(dir), true).GetStatuses(new SystemFacts { GameDVR = 0 }).Single(x => x.Fix.Id == "GAMEDVR");
            Assert.Equal(FixStatusKind.AtivoWinDiag, st.Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void StateDiffers_ComparaRegistroServicoBcdEPower()
    {
        FixState Make(int start, string? bcd, long? power) => new()
        {
            Svc = { new SvcState { Name = "S", Start = new RegState { Path = "p", Name = "Start", Exists = true, Value = start } } },
            Bcd = { new BcdState { Name = "b", Old = bcd } },
            Power = { new PowerState { Sub = "s", Set = "t", Old = power } },
        };
        Assert.False(FixEngine.StateDiffers(Make(4, "off", 0), Make(4, "OFF", 0)));
        Assert.True(FixEngine.StateDiffers(Make(4, "off", 0), Make(2, "off", 0)));
        Assert.True(FixEngine.StateDiffers(Make(4, "off", 0), Make(4, null, 0)));
        Assert.True(FixEngine.StateDiffers(Make(4, "off", 0), Make(4, "off", 1)));
    }

    // ------------------------------------------------------------------ 9. BCD / Reg.Delete

    [Theory]
    [InlineData("The element was not found.", true)]
    [InlineData("O elemento não foi encontrado.", true)]
    [InlineData("O elemento especificado não foi encontrado. Elemento não encontrado.", true)]
    [InlineData("Access is denied.", false)]
    [InlineData("O acesso foi negado.", false)]
    [InlineData("", false)]
    public void Bcd_ReconheceSomenteElementoNaoEncontrado(string texto, bool esperado) =>
        Assert.Equal(esperado, FixEngine.IsBcdElementNotFound(texto));

    [Fact]
    public void RegDelete_ChaveInexistenteNaoLanca() =>
        Reg.Delete(@"HKCU:\Software\WinDiagTest_" + Guid.NewGuid().ToString("N"), "v");

    // ------------------------------------------------------------------ 18. energia

    [Fact]
    public void Power_EsquemaSalvoOuSchemeCurrent()
    {
        Assert.Equal("SCHEME_CURRENT", FixEngine.SchemeArg(null));
        Assert.Equal("SCHEME_CURRENT", FixEngine.SchemeArg("lixo; rm"));
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", FixEngine.SchemeArg("381B4222-F694-41F0-9685-FF5BB260DF2E"));
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerCfg.ParseSchemeGuid("GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)"));
    }

    // ------------------------------------------------------------------ 8. S.M.A.R.T. por instância

    [Fact]
    public void Smart_DoisDiscosDoMesmoModelo_CasamPorInstancia()
    {
        var disks = new (string?, string?)[]
        {
            (@"SCSI\DISK&VEN_WDC&PROD_WD10EZEX-00BN5A0\4&AAA&0&000000", "WDC WD10EZEX-00BN5A0"),
            (@"SCSI\DISK&VEN_WDC&PROD_WD10EZEX-00BN5A0\4&BBB&0&000000", "WDC WD10EZEX-00BN5A0"),
        };
        var inst = new[]
        {
            @"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&bbb&0&000000_0",
            @"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&aaa&0&000000_0",
        };
        var r = Smart.MatchInstances(disks, inst, out var amb);
        Assert.False(amb);
        Assert.Equal(new int?[] { 1, 0 }, r);
    }

    [Fact]
    public void Smart_MesmoModeloSemPnp_NaoAtribui()
    {
        var disks = new (string?, string?)[] { (null, "WDC WD10EZEX-00BN5A0"), (null, "WDC WD10EZEX-00BN5A0") };
        var inst = new[] { @"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&bbb&0&000000_0", @"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&aaa&0&000000_0" };
        var r = Smart.MatchInstances(disks, inst, out var amb);
        Assert.True(amb);
        Assert.Equal(new int?[] { null, null }, r);
    }

    [Fact]
    public void Smart_ModeloUnicoSemPnp_Atribui()
    {
        var disks = new (string?, string?)[] { (null, "WDC WD10EZEX-00BN5A0"), (null, "Samsung SSD 860") };
        var inst = new[] { @"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&bbb&0&000000_0" };
        var r = Smart.MatchInstances(disks, inst, out var amb);
        Assert.False(amb);
        Assert.Equal(new int?[] { 0, null }, r);
    }

    // ------------------------------------------------------------------ 14. captura

    private static List<CaptureSample> Samples(Func<int, CaptureSample> f) => Enumerable.Range(1, 10).Select(f).ToList();

    [Fact]
    public void Captura_VramDedicada95PorCento()
    {
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 90, CpuPct = 30, VramDedicatedMB = 7800 });
        Assert.Contains(Capture.Summarize(s, 8192).Interpretation, t => t.Contains("VRAM dedicada próxima do limite"));
        Assert.DoesNotContain(Capture.Summarize(s, 12288).Interpretation, t => t.Contains("VRAM"));
    }

    [Fact]
    public void Captura_CompartilhadaSozinhaNaoDispara()
    {
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 90, CpuPct = 30, VramDedicatedMB = 3000, VramSharedMB = 900 });
        Assert.DoesNotContain(Capture.Summarize(s, 8192).Interpretation, t => t.Contains("VRAM"));
        Assert.DoesNotContain(Capture.Summarize(s).Interpretation, t => t.Contains("VRAM"));
    }

    [Fact]
    public void Captura_CompartilhadaComDedicadaAlta_Dispara()
    {
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 90, CpuPct = 30, VramDedicatedMB = 7500, VramSharedMB = 900 });
        Assert.Contains(Capture.Summarize(s, 8192).Interpretation, t => t.Contains("memória compartilhada"));
    }

    [Fact]
    public void Captura_CpuTotal85()
    {
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 50, CpuPct = 87 });
        Assert.Contains(Capture.Summarize(s).Interpretation, t => t.Contains("CPU-bound"));
    }

    [Fact]
    public void Captura_UmaThreadSaturada()
    {
        // 12 threads: um núcleo inteiro = 8,3% do total; 90% disso = 7,5%
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 60, CpuPct = 20, GameCpuPct = 8.2 });
        Assert.Contains(Capture.Summarize(s, null, 12).Interpretation, t => t.Contains("CPU-bound") && t.Contains("uma thread"));
        var baixo = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 60, CpuPct = 20, GameCpuPct = 3 });
        Assert.DoesNotContain(Capture.Summarize(baixo, null, 12).Interpretation, t => t.Contains("CPU-bound"));
    }

    [Fact]
    public void Captura_GpuNoLimiteNaoEhCpuBound()
    {
        var s = Samples(i => new CaptureSample { Second = i, Gpu3DPct = 99, CpuPct = 95, GameCpuPct = 30 });
        Assert.DoesNotContain(Capture.Summarize(s, null, 12).Interpretation, t => t.Contains("CPU-bound"));
    }

    // ------------------------------------------------------------------ 15. máscara

    [Fact]
    public void Mascara_NomeDoPerfilEPcSaoMascarados()
    {
        var r = ReportMask.User(@"C:\Users\dougllascda\AppData e DESKTOP-ABC123 ok", "Dougll", "dougllascda", "DESKTOP-ABC123");
        Assert.Equal(@"C:\Users\<USUARIO>\AppData e <PC> ok", r);
    }

    [Fact]
    public void Mascara_NaoCorrompePalavras()
    {
        var r = ReportMask.User("Administrator e Admin, grupo Administradores", "Admin", "", "");
        Assert.Equal("Administrator e <USUARIO>, grupo Administradores", r);
        Assert.Equal("PCIe do PC", ReportMask.User("PCIe do PC", "", "", "PC1"));
    }

    [Fact]
    public void Mascara_RespeitaLimiteDePalavra_ComPontuacao()
    {
        Assert.Equal("<USUARIO>.pc e (<USUARIO>)", ReportMask.User("fulano.pc e (fulano)", "fulano", "", ""));
        Assert.Equal("fulano2 e xfulano", ReportMask.User("fulano2 e xfulano", "fulano", "", ""));
    }

    // ------------------------------------------------------------------ 16. HVCI

    [Fact]
    public void Hvci_RegistroZeroMasEmExecucao_PendenteDeReinicio()
    {
        var facts = new SystemFacts { HvciRunning = true };
        // O IsActive do HVCI lê o registro real; aqui só validamos o caminho de status externo/pendente via item sem snapshot.
        var st = new FixEngine(new SnapshotStore(NewDir()), true).GetStatuses(facts).Single(x => x.Fix.Id == "HVCI_OFF");
        if (st.IsOn) Assert.True(st.RebootPending);
        else Assert.False(st.RebootPending);
    }

    // ------------------------------------------------------------------ 17. fechar apps

    [Theory]
    [InlineData(true, true, false, false)]   // processo com janela: nunca à força
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]   // auxiliar enquanto a janela ainda está aberta
    [InlineData(false, true, false, true)]   // auxiliar depois que a janela fechou
    [InlineData(false, false, false, true)]  // app sem nenhuma janela
    public void Close_RegraDeEncerramentoForcado(bool thisWin, bool groupWin, bool windowedAlive, bool esperado) =>
        Assert.Equal(esperado, GameSession.CanForceKill(thisWin, groupWin, windowedAlive));

    // ------------------------------------------------------------------ 19. OEM

    [Fact]
    public void Proc_UsaCodificacaoOemSemLancar()
    {
        var r = Proc.Run(Proc.Sys32("cmd.exe"), "/c echo ok", 5000);
        Assert.True(r.Ok);
        Assert.Contains("ok", r.StdOut);
        Assert.NotNull(Proc.OemEncoding);
    }

    // ------------------------------------------------------------------ 21. codinome

    [Theory]
    [InlineData(9, "Kaby Lake (14 nm)")]
    [InlineData(10, "Coffee Lake (14 nm)")]
    [InlineData(12, "Coffee Lake (14 nm)")]
    [InlineData(null, "Kaby Lake/Coffee Lake (14 nm)")]
    public void Codinome_Modelo158PorStepping(int? stepping, string esperado) =>
        Assert.Equal(esperado, CpuCodename.Lookup(true, false, 6, 158, stepping));

    [Fact]
    public void Codinome_Modelo158ViaDescricao()
    {
        Assert.Equal("Kaby Lake (14 nm)", CpuCodename.FromDescription("Intel64 Family 6 Model 158 Stepping 9", "GenuineIntel"));
        Assert.Equal("Coffee Lake (14 nm)", CpuCodename.FromDescription("Intel64 Family 6 Model 158 Stepping 10", "GenuineIntel"));
    }

    // ------------------------------------------------------------------ 22. teto de eventos

    [Fact]
    public void Eventos_TetoMostraMais()
    {
        Assert.Equal("300+", EventsCollector.CountText(300, true, 300));
        Assert.Equal("42", EventsCollector.CountText(42, false, 300));
    }

    // ------------------------------------------------------------------ 23. Fast Startup

    [Fact]
    public void Aplicado_PropriedadeOpcionalSerializaNoSnapshot()
    {
        var dir = NewDir();
        try
        {
            var store = new SnapshotStore(dir);
            var fx = new SnapshotFix { Id = "GAMEDVR", Name = "x", Reversible = true, Action = SnapshotActions.Apply };
            var snap = new Snapshot { Version = "1", Fixes = { fx } };
            var path = store.Save(snap);
            fx.Applied = new FixState { Reg = { new RegState { Path = "HKCU:\\x", Name = "v", Exists = true, Value = 0 } } };
            store.Rewrite(path, snap);
            var back = store.List().Single();
            Assert.NotNull(back.Data.Fixes[0].Applied);
            Assert.Equal("v", back.Data.Fixes[0].Applied!.Reg[0].Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
