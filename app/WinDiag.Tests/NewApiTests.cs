using System.Text.RegularExpressions;
using WinDiag.Core;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Settings;

namespace WinDiag.Tests;

/// <summary>Testes de Id/Simple/GuideTopic dos achados, glossário, guias de BIOS, estimativa de fonte e preferências.</summary>
public class NewApiTests
{
    private static SystemFacts Reference() => new()
    {
        CpuName = "Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz", CpuCores = 4, CpuThreads = 8,
        BoardModel = "Gigabyte Technology Co., Ltd. H510M H", Chipset = "H510", BiosVersion = "F20e",
        FirmwareUefi = true, SecureBoot = true,
        RamConfiguredMTs = 2666, RamRatedMTs = 2666, RamType = "DDR4", RamModules = 2, RamChannels = 2, DualChannel = true,
        GpuName = "NVIDIA GeForce RTX 5060", GpuVramMiB = 8151, IsNvidia = true, GpuModern = true,
        GpuPcieGenMax = 5, GpuPcieHostMax = 2, GpuPcieGenCurrent = 2, GpuPcieWidthCurrent = 8, GpuPcieWidthMax = 16,
        Bar1MiB = 8192, ReBarActive = true, GpuPowerLimitW = 145, HddCount = 2, SsdCount = 2,
        Displays =
        {
            new DisplayInfo { Width = 1600, Height = 900, RefreshHz = 60, Connection = "HDMI" },
            new DisplayInfo { Width = 1366, Height = 768, RefreshHz = 59.96, Connection = "DP" },
        },
    };

    private static SectionResult Run(SystemFacts f) => Insights.Analyze(f, new List<SectionResult>());

    // ---------------------------------------------------------------- Id / Simple / Guide

    [Fact]
    public void Insights_Findings_HaveUniqueIdsPerSection_AndSimpleText()
    {
        var r = Run(Reference());
        Assert.NotEmpty(r.Findings);
        Assert.All(r.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Id), f.Title));
        Assert.Equal(r.Findings.Count, r.Findings.Select(f => f.Id).Distinct().Count());
        var relevant = r.Findings.Where(f => f.Sev >= Severity.Aviso || f.Id is "analise.rebar" or "analise.psu");
        Assert.All(relevant, f => Assert.False(string.IsNullOrWhiteSpace(f.Simple), f.Title));
    }

    [Fact]
    public void DefaultId_IsAsciiSlug()
    {
        Assert.Equal("gpu.captura_xbox_game_dvr", SectionResult.DefaultId("gpu", "Captura Xbox Game DVR"));
        Assert.Equal("memoria.compressao_de_memoria", SectionResult.DefaultId("memoria", "Compressão de memória"));
        var res = new SectionResult { Key = "x", Title = "x" };
        res.Find(Severity.Info, "Título sem id", "ev");
        res.Find(Severity.Info, "Com id", "ev", id: "x.abc", simple: "s", guide: "Xmp");
        Assert.Equal("x.titulo_sem_id", res.Findings[0].Id);
        Assert.Equal("x.abc", res.Findings[1].Id);
        Assert.Equal("Xmp", res.Findings[1].GuideTopic);
    }

    [Fact]
    public void Reference_Pcie_HasGuideTopic_AndId()
    {
        var f = Run(Reference()).Findings.First(x => x.Title.StartsWith("Link PCIe abaixo"));
        Assert.Equal("pcie.config_limited", f.Id);
        Assert.Equal("PcieSlot", f.GuideTopic);
        Assert.True(BiosGuides.TryParse(f.GuideTopic, out _));
    }

    [Fact]
    public void ReBar_Active_IsOk_AndPointsAtPcieLink()
    {
        var r = Run(Reference());
        var f = r.Findings.Single(x => x.Id == "analise.rebar");
        Assert.Equal(Severity.Ok, f.Sev);
        Assert.Equal("Resizable BAR ativo", f.Title);
        Assert.Contains("Está certo", f.Explain);
        Assert.Contains("o que pede atenção é a geração do link PCIe, que é outro ajuste da BIOS (veja Link PCIe)", f.Explain);
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("com link limitado"));
        Console.WriteLine("REBAR: " + f.Title + " | " + f.Evidence + " | " + f.Explain);
    }

    [Fact]
    public void ReBar_Off_And_Xmp_Off_HaveGuideTopics()
    {
        var f = Reference();
        f.ReBarActive = false; f.Bar1MiB = 256;
        var r = Run(f);
        Assert.Equal("ReBar", r.Findings.Single(x => x.Id == "analise.rebar_off").GuideTopic);

        var g = Reference();
        g.Chipset = "Z490"; g.BoardModel = "Gigabyte Z490 AORUS"; g.RamConfiguredMTs = 2133; g.RamRatedMTs = 3200;
        var xr = Run(g);
        Assert.Equal("Xmp", xr.Findings.Single(x => x.Id == "analise.xmp").GuideTopic);
    }

    [Fact]
    public void ActionPlan_UsesFindingId_AsKey_AndCarriesGuide()
    {
        var steps = ActionPlan.Build(Run(Reference()).Findings);
        var s = steps.First(x => x.FindingId == "pcie.config_limited");
        Assert.Equal("pcie.config_limited", s.Key);
        Assert.Equal("PcieSlot", s.GuideTopic);
        Assert.False(string.IsNullOrWhiteSpace(s.Simple));
    }

    [Fact]
    public void GameScore_AreaOf_IsPublic()
    {
        Assert.Equal("Plataforma e GPU", GameScore.AreaOf("gpu"));
        Assert.Equal("Armazenamento", GameScore.AreaOf("armazenamento"));
        Assert.Equal("Sistema e segurança", GameScore.AreaOf("desconhecida"));
    }

    [Fact]
    public async Task LiveScan_Findings_HaveIds_UniquePerSection()
    {
        var engine = new ScanEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await engine.RunAsync(cts.Token);
        foreach (var sec in engine.Results)
        {
            Assert.All(sec.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Id), sec.Key + ": " + f.Title));
            var dup = sec.Findings.GroupBy(f => f.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, sec.Key + " tem ids repetidos: " + string.Join(", ", dup));
        }
    }

    // ---------------------------------------------------------------- Glossário

    [Fact]
    public void Glossary_HasEntries_WithUniqueKeys()
    {
        Assert.True(Glossary.All.Count >= 65, "verbetes: " + Glossary.All.Count);
        Assert.Equal(Glossary.All.Count, Glossary.All.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Glossary.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.True(e.Text.Length > 60, e.Key);
        });
    }

    [Fact]
    public void Glossary_Lookup_ByKey_Title_AndGroup()
    {
        Assert.Equal("rebar", Glossary.Lookup("rebar")!.Key);
        Assert.Equal("hags", Glossary.Lookup("HAGS (agendamento de GPU acelerado por hardware)")!.Key);
        Assert.Equal("disco", Glossary.Lookup("Disco 0 [D:]")!.Key);
        Assert.Equal("disco", Glossary.Lookup("Disco 1 [C: E:]")!.Key);
        Assert.Equal("placa_de_video", Glossary.Lookup("NVIDIA GeForce RTX 5060")!.Key);
        Assert.Equal("monitores", Glossary.Lookup("AOC 2050")!.Key);
        Assert.Equal("monitores", Glossary.Lookup("Generic PnP Monitor", "monitores")!.Key);
        Assert.Equal("placa_de_video", Glossary.Lookup("Qualquer nome", "gpu")!.Key);
        Assert.Equal("memoria", Glossary.Lookup("memória")!.Key);
        Assert.Null(Glossary.Lookup("assunto inexistente xyz"));
        Assert.Null(Glossary.Lookup(""));
    }

    private static string? FindCoreDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var c = Path.Combine(d.FullName, "WinDiag.Core");
            if (Directory.Exists(Path.Combine(c, "Collectors"))) return c;
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void Glossary_CoversEveryFixedGroupTitle_OfCollectorsAndInsights()
    {
        var core = FindCoreDir();
        Assert.NotNull(core);
        var files = Directory.GetFiles(Path.Combine(core!, "Collectors"), "*.cs").Append(Path.Combine(core!, "Rules", "Insights.cs"));
        // Títulos fixos: .Group("pt") ou .Group(Loc.T("pt", "en")); exige verbete para os dois idiomas.
        var rx = new Regex("\\.Group\\(\"([^\"]+)\"\\)");
        var rxT = new Regex("\\.Group\\(Loc\\.T\\(\"((?:[^\"\\\\]|\\\\.)+)\",\\s*\"((?:[^\"\\\\]|\\\\.)+)\"\\)\\)");
        var titles = files.SelectMany(f =>
        {
            var text = File.ReadAllText(f);
            return rx.Matches(text).Select(m => m.Groups[1].Value)
                .Concat(rxT.Matches(text).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }));
        }).Distinct().ToList();
        Assert.True(titles.Count >= 20, "grupos encontrados: " + titles.Count);
        var missing = titles.Where(t => Glossary.Lookup(t) is null).ToList();
        Assert.True(missing.Count == 0, "Sem verbete: " + string.Join(" | ", missing));

        foreach (var c in ScanEngine.CreateCollectors())
            Assert.NotNull(Glossary.Lookup(c.Title));
        Assert.NotNull(Glossary.Lookup(Insights.SectionTitle));
    }

    // ---------------------------------------------------------------- Guias de BIOS

    [Fact]
    public void BiosGuides_AllTopicsAndVendors_HaveSteps()
    {
        foreach (var topic in Enum.GetValues<BiosTopic>())
            foreach (var board in new string?[] { "Gigabyte H510M", "AORUS B550", "ASUS PRIME B560", "ROG STRIX Z790", "MSI B450", "ASRock B660", "Contoso X1", "", null })
            {
                var g = BiosGuides.For(topic, board);
                Assert.Equal(topic, g.Topic);
                Assert.NotEmpty(g.Steps);
                Assert.All(g.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
                Assert.False(string.IsNullOrWhiteSpace(g.Title));
                Assert.False(string.IsNullOrWhiteSpace(g.EnterKey));
                Assert.False(string.IsNullOrWhiteSpace(g.Revert));
                Assert.False(string.IsNullOrWhiteSpace(g.Caution));
                Assert.Contains("podem variar", string.Join(" ", g.Steps));
            }
    }

    [Fact]
    public void BiosGuides_VendorDetection_AndPaths()
    {
        Assert.Equal("Gigabyte / Aorus", BiosGuides.For(BiosTopic.Xmp, "Gigabyte Technology Co., Ltd. H510M H").Vendor);
        Assert.Contains(BiosGuides.For(BiosTopic.PcieSlot, "Gigabyte H510M").Steps, s => s.Contains("PCIEX16 Slot Configuration"));
        Assert.Contains(BiosGuides.For(BiosTopic.ReBar, "Gigabyte H510M").Steps, s => s.Contains("CSM Support"));
        Assert.Contains(BiosGuides.For(BiosTopic.Xmp, "ASUSTeK ROG STRIX").Steps, s => s.Contains("XMP I"));
        Assert.Contains(BiosGuides.For(BiosTopic.PcieSlot, "MSI MAG B550").Steps, s => s.Contains("PEG0 Max Link Speed"));
        Assert.Contains(BiosGuides.For(BiosTopic.Xmp, "ASRock B450").Steps, s => s.Contains("Load XMP Setting"));
        Assert.Equal("Genérico", BiosGuides.For(BiosTopic.Xmp, null).Vendor);
        Assert.Contains("nunca desligue", BiosGuides.For(BiosTopic.BiosUpdate, null).Caution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CMOS", BiosGuides.For(BiosTopic.Xmp, null).Revert);
    }

    [Fact]
    public void BiosGuides_TryParse()
    {
        Assert.True(BiosGuides.TryParse("PcieSlot", out var t));
        Assert.Equal(BiosTopic.PcieSlot, t);
        Assert.True(BiosGuides.TryParse("rebar", out t));
        Assert.Equal(BiosTopic.ReBar, t);
        Assert.False(BiosGuides.TryParse("", out _));
        Assert.False(BiosGuides.TryParse(null, out _));
        Assert.False(BiosGuides.TryParse("Nada", out _));
        Assert.False(BiosGuides.TryParse("99", out _));
    }

    // ---------------------------------------------------------------- Fonte

    [Fact]
    public void PowerEstimate_ReferenceMachine()
    {
        var e = PowerEstimator.Estimate(Reference());
        Assert.NotNull(e);
        Assert.Equal(98, e!.CpuW);       // 65 W x 1,5
        Assert.Equal(145, e.GpuW);
        Assert.Equal(80, e.RestW);       // 50 + 2x10 + 2x5
        Assert.InRange(e.PeakW, 280, 330);
        Assert.InRange(e.RecommendedW, 450, 500);
        Assert.Contains("TDP de 65 W", e.Basis);
        Console.WriteLine($"PSU: pico {e.PeakW} W, recomendada {e.RecommendedW} W. {e.Basis}");
    }

    [Fact]
    public void PowerEstimate_Fallbacks()
    {
        var f = new SystemFacts { CpuName = "CPU Desconhecida X", GpuName = "GPU Desconhecida" };
        var e = PowerEstimator.Estimate(f)!;
        Assert.Equal(95, e.CpuW);
        Assert.Equal(150, e.GpuW);
        Assert.Equal(50, e.RestW);
        Assert.True(e.RecommendedW >= 400 && e.RecommendedW % 50 == 0);
        Assert.Contains("fora da tabela", e.Basis);

        var t = new SystemFacts { CpuName = "Ryzen 5 5600", GpuName = "NVIDIA GeForce RTX 4060" };
        Assert.Equal(115, PowerEstimator.Estimate(t)!.GpuW);
        Assert.Null(PowerEstimator.Estimate(new SystemFacts()));
    }

    [Fact]
    public void PlatformDb_Tdp()
    {
        Assert.Equal(65, PlatformDb.ResolveCpu("Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz")!.TdpW);
        Assert.Equal(65, PlatformDb.ResolveCpu("Intel(R) Core(TM) i5-12400F")!.TdpW);
        Assert.Equal(65, PlatformDb.ResolveCpu("AMD Ryzen 5 5600 6-Core Processor")!.TdpW);
        Assert.Equal(105, PlatformDb.ResolveCpu("AMD Ryzen 7 5800X 8-Core Processor")!.TdpW);
        Assert.Null(PlatformDb.ResolveCpu("AMD Ryzen 5 5555XYZ 6-Core Processor")?.TdpW);
    }

    [Fact]
    public void Psu_Findings_BySize()
    {
        var f = Reference();
        var none = Run(f).Findings.Single(x => x.Id == "analise.psu");
        Assert.Equal(Severity.Info, none.Sev);
        Assert.Contains("Informe sua fonte em Configurações para checar", none.Explain);

        f.PsuWatts = 300;
        var low = Run(f).Findings.Single(x => x.Id == "analise.psu");
        Assert.Equal(Severity.Atencao, low.Sev);

        f.PsuWatts = 400;
        Assert.Equal(Severity.Aviso, Run(f).Findings.Single(x => x.Id == "analise.psu").Sev);

        f.PsuWatts = 650;
        var ok = Run(f).Findings.Single(x => x.Id == "analise.psu");
        Assert.Equal(Severity.Ok, ok.Sev);
        Assert.Contains(Run(f).Groups, g => g.Title == "Fonte de alimentação");
        Console.WriteLine("PSU sem fonte: " + none.Title + " | " + none.Evidence + " | " + none.Explain);
    }

    // ---------------------------------------------------------------- UserPrefs

    [Fact]
    public void UserPrefs_ReadsOldFormat_AndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinDiagPrefs_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"Theme\":\"Light\"}");
            var p = UserPrefs.Load(path);
            Assert.Equal("Light", p.Theme);
            Assert.Equal("Auto", p.Language);
            Assert.Equal("Simple", p.Mode);
            Assert.Null(p.PsuWatts);
            Assert.Empty(p.PlanDone);

            p.PsuWatts = 550; p.PsuModel = "Corsair CV550"; p.PlanDone.Add("pcie.config_limited");
            p.Save(path);
            var q = UserPrefs.Load(path);
            Assert.Equal(550, q.PsuWatts);
            Assert.Equal("Corsair CV550", q.PsuModel);
            Assert.Single(q.PlanDone);
            Assert.Equal("pcie.config_limited", q.PlanDone[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UserPrefs_CorruptOrMissing_ReturnsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinDiagPrefs_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            Assert.Equal("Auto", UserPrefs.Load(path).Theme);
            File.WriteAllText(path, "{ não é json");
            var p = UserPrefs.Load(path);
            Assert.Equal("Auto", p.Theme);
            Assert.Equal("Simple", p.Mode);
            File.WriteAllText(path, "[1,2,3]");
            Assert.Equal("Auto", UserPrefs.Load(path).Theme);
            Assert.EndsWith(Path.Combine("WinDiag", "settings.json"), UserPrefs.FilePath);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
