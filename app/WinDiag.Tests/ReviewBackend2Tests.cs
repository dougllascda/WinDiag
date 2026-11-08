using WinDiag.Core;
using WinDiag.Core.History;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Settings;
using Xunit;

namespace WinDiag.Tests;

/// <summary>Correções de revisão no backend: preferências, plano de ação, histórico, estimativa de fonte e área da nota.</summary>
[Collection("Loc")]
public class ReviewBackend2Tests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinDiagRev2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------------------------------------------------------- UserPrefs

    [Fact]
    public void UserPrefs_InvalidJson_IsCopiedToBad_AndDefaultsAreNotFallback()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ não é json");
            var p = UserPrefs.Load(path);
            Assert.Equal("Auto", p.Theme);
            Assert.False(p.IsFallback);
            Assert.True(File.Exists(path + ".bad"));
            Assert.Equal("{ não é json", File.ReadAllText(path + ".bad"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UserPrefs_LockedFile_IsFallback_AndSaveDoesNotOverwrite()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"Theme\":\"Dark\",\"PsuWatts\":650}");
            UserPrefs p;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                p = UserPrefs.Load(path);
            Assert.True(p.IsFallback);
            Assert.False(p.Save(path));
            Assert.Contains("650", File.ReadAllText(path));

            // Depois de liberado, a leitura normal volta a funcionar.
            var ok = UserPrefs.Load(path);
            Assert.False(ok.IsFallback);
            Assert.Equal("Dark", ok.Theme);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UserPrefs_Save_IsAtomic_NoTmpLeft_AndReplacesExisting()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.True(new UserPrefs { Theme = "Dark" }.Save(path));
            Assert.True(new UserPrefs { Theme = "Light" }.Save(path));
            Assert.Equal("Light", UserPrefs.Load(path).Theme);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- ActionPlan

    private static Finding F(string id, string section, string title, string fixId = "")
        => new(Severity.Aviso, title, "ev", "Explicação.", "Faça algo " + id + fixId + ".", fixId, 30) { Id = id, Section = section };

    [Fact]
    public void ActionPlan_Key_IsStableAcrossLanguages()
    {
        var pt = ActionPlan.Build(new[] { F("", "gpu", "Título em português", "HAGS"), F("pcie.x", "gpu", "Outro título") });
        Loc.Set("en");
        List<ActionStep> en;
        try { en = ActionPlan.Build(new[] { F("", "gpu", "Title in English", "HAGS"), F("pcie.x", "gpu", "Another title") }); }
        finally { Loc.Set("pt-BR"); }
        Assert.Equal(pt.Select(s => s.Key).OrderBy(x => x), en.Select(s => s.Key).OrderBy(x => x));
        Assert.Contains(pt, s => s.Key == "gpu:HAGS");
        Assert.Contains(pt, s => s.Key == "pcie.x");
    }

    [Fact]
    public void ActionPlan_GroupTitleWithParenthesisInPrefix_KeyUsesStableId_AndTitleKeepsPrefix()
    {
        var a = new Finding(Severity.Aviso, "Disco (SSD): C:", "ev", "", "Mesma ação.", "", 20) { Id = "x.b", Section = "armazenamento" };
        var b = new Finding(Severity.Aviso, "Disco (SSD): D:", "ev", "", "Mesma ação.", "", 20) { Id = "x.a", Section = "armazenamento" };
        var step = Assert.Single(ActionPlan.Build(new[] { a, b }));
        Assert.Equal("x.a", step.Key);
        Assert.StartsWith("Disco (SSD) (", step.Title);

        // sem Id nem FixId, o título-base do grupo (sem a lista) vira a chave
        var c = new Finding(Severity.Aviso, "Disco (SSD): C:", "ev", "", "Mesma ação.", "", 20) { Section = "armazenamento" };
        var d = new Finding(Severity.Aviso, "Disco (SSD): D:", "ev", "", "Mesma ação.", "", 20) { Section = "armazenamento" };
        Assert.Equal("armazenamento:disco ssd", Assert.Single(ActionPlan.Build(new[] { c, d })).Key);
    }

    // ---------------------------------------------------------------- ScanHistory

    private static ScanSummary Summary(string sources, params FindingSummary[] findings)
        => new() { When = DateTime.Now, Facts = new() { ["Admin"] = "sim", ["sensorsources"] = sources }, Findings = findings.ToList() };

    private static FindingSummary FS(string key, string section, string title, Severity sev)
        => new() { Key = key, Section = section, Title = title, Sev = sev, Evidence = "ev" };

    [Fact]
    public void ScanHistory_SensorsSectionIgnored_WhenSourcesDiffer()
    {
        var hot = FS("sensors.cpu_hot", "sensores", "Temperatura da CPU muito alta", Severity.Atencao);
        var diff = ScanHistory.Compare(Summary("HWiNFO+LibreHardwareMonitor", hot), Summary("LibreHardwareMonitor"));
        Assert.Empty(diff.Resolved);

        // mesmas fontes: resolvido de verdade
        var same = ScanHistory.Compare(Summary("LibreHardwareMonitor", hot), Summary("LibreHardwareMonitor"));
        Assert.Single(same.Resolved);
    }

    [Fact]
    public void ScanHistory_RepeatedIds_AreMatchedByIdAndTitle()
    {
        var a1 = FS("dup.id", "armazenamento", "Disco A", Severity.Aviso);
        var a2 = FS("dup.id", "armazenamento", "Disco B", Severity.Aviso);
        var diff = ScanHistory.Compare(Summary("none", a1, a2), Summary("none", a1.With("Disco A", Severity.Ok), a2));
        var r = Assert.Single(diff.Resolved);
        Assert.Equal("Disco A", r.Title);
    }

    // ---------------------------------------------------------------- PowerEstimator

    private static int Gpu(string name) => PowerEstimator.Estimate(new SystemFacts { GpuName = name, CpuTdpW = 65 })!.GpuW;

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070 Ti", 290)]
    [InlineData("NVIDIA GeForce RTX 3080 Ti", 350)]
    [InlineData("NVIDIA GeForce RTX 3090 Ti", 450)]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 220)]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", 285)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", 250)]
    [InlineData("AMD Radeon RX 7900 XT", 315)]
    [InlineData("AMD Radeon RX 7900 XTX", 355)]
    [InlineData("AMD Radeon RX 9060 XT", 160)]
    [InlineData("AMD Radeon RX 580 Series", 185)]
    [InlineData("NVIDIA GeForce RTX 3070", 220)]
    [InlineData("NVIDIA GeForce RTX 4070", 200)]
    [InlineData("Intel(R) UHD Graphics 630", 15)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 15)]
    [InlineData("Intel(R) Arc(TM) Graphics", 15)]
    [InlineData("AMD Radeon(TM) Graphics", 15)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", 15)]
    public void PowerEstimator_GpuTable_VariantsBeforeBase_AndIntegrated(string name, int watts) => Assert.Equal(watts, Gpu(name));

    [Fact]
    public void PowerEstimator_DiscreteArc_IsNotIntegrated() => Assert.Equal(225, Gpu("Intel(R) Arc(TM) A750 Graphics"));

    // ---------------------------------------------------------------- GameScore

    [Fact]
    public void GameScore_SensorsSection_BelongsToProcessorAndMemoryArea()
    {
        Assert.Equal(GameScore.AreaOf("cpu"), GameScore.AreaOf("sensores"));
        var s = GameScore.Compute(new[] { F("sensors.cpu_hot", "sensores", "Temperatura da CPU muito alta") });
        Assert.Equal(GameScore.AreaOf("cpu"), Assert.Single(s.Parts).Area);
    }
}

internal static class FindingSummaryExt
{
    /// <summary>Cópia do resumo com título e gravidade trocados (apoio dos testes).</summary>
    public static FindingSummary With(this FindingSummary f, string title, Severity sev)
        => new() { Key = f.Key, Section = f.Section, Title = title, Sev = sev, Evidence = f.Evidence };
}
