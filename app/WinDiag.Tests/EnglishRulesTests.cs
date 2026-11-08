using WinDiag.Core;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Report;
using WinDiag.Core.Rules;

namespace WinDiag.Tests;

/// <summary>Testes em inglês de Rules, Knowledge e Report. Cada teste troca o idioma e restaura pt-BR ao final.</summary>
[Collection("Loc")]
public class EnglishRulesTests
{
    private static readonly string[] ExpectedGroups = { "Graphics card PCIe chain", "RAM", "Resizable BAR", "System balance", "Upgrade path" };
    private static readonly string[] Grades = { "Excellent", "Good", "Fair", "Limited" };
    private static readonly string?[] Boards = { "Gigabyte H510M", "ASUS PRIME B560", "MSI B450", "ASRock B660", null };

    private static void InEnglish(Action body)
    {
        Loc.Set("en");
        try { body(); }
        finally { Loc.Set("pt-BR"); }
    }

    [Fact]
    public void Insights_Reference_Profile_InEnglish()
    {
        InEnglish(() =>
        {
            var r = Insights.Analyze(InsightsTests.ReferenceMachine(), new List<SectionResult>());
            Assert.Equal("analise", r.Key);
            Assert.Equal("Platform analysis", r.Title);
            foreach (var g in ExpectedGroups)
                Assert.Contains(r.Groups, x => x.Title == g);
            Assert.All(r.Findings, f => Assert.True(f.Title.All(ch => ch < 128), "título com acento: " + f.Title));

            var pcie = r.Findings.First(x => x.Title == "PCIe link below what the platform allows");
            Assert.Equal(Severity.Atencao, pcie.Sev);
            Assert.StartsWith("Where the limit is: configuration", pcie.Explain);
            Assert.StartsWith("What can be improved:", pcie.Action);
            Assert.Contains("(cost: free)", pcie.Action);
            Assert.Contains("Expected gain:", pcie.Action);
            Assert.Contains("4 → 7.9 GB/s", pcie.Action);

            var up = r.Findings.First(x => x.Title.StartsWith("Upgrade path"));
            Assert.Contains("(cost: hardware)", up.Action);
            Assert.Equal("Platform analysis", Insights.SectionTitle);
        });
    }

    [Fact]
    public void ActionPlan_ParsesEnglishMarkers()
    {
        InEnglish(() =>
        {
            var r = Insights.Analyze(InsightsTests.ReferenceMachine(), new List<SectionResult>());
            var steps = ActionPlan.Build(r.Findings);
            var pcie = steps.First(s => s.FindingId == "pcie.config_limited");
            Assert.Equal(ActionCost.Gratis, pcie.Cost);
            Assert.StartsWith("configuration, not hardware", pcie.Where);
            Assert.StartsWith("In the BIOS", pcie.What);
            Assert.DoesNotContain("What can be improved", pcie.What);
            Assert.DoesNotContain("cost:", pcie.What);
            Assert.Contains("GB/s", pcie.Gain);
            var hw = new SectionResult { Key = "analise", Title = "x" };
            hw.Find(Severity.Aviso, "Weak PSU", "ev", "Where the limit is: power supply. More text.",
                "What can be improved: replace the PSU (cost: hardware). Expected gain: stability.", impact: 90, id: "t.psu");
            var plan = ActionPlan.Build(r.Findings.Concat(hw.Findings));
            var psu = plan.First(s => s.FindingId == "t.psu");
            Assert.Equal(ActionCost.Hardware, psu.Cost);
            Assert.Equal("power supply", psu.Where);
            Assert.Equal("Replace the PSU.", psu.What);
            Assert.Equal("stability.", psu.Gain);
            Assert.True(plan.First(s => s.FindingId == "pcie.config_limited").Order < psu.Order, "grátis vem antes de hardware");
        });
    }

    [Fact]
    public void ActionPlan_StillParsesPortuguese()
    {
        var r = Insights.Analyze(InsightsTests.ReferenceMachine(), new List<SectionResult>());
        var pcie = ActionPlan.Build(r.Findings).First(s => s.FindingId == "pcie.config_limited");
        Assert.Equal(ActionCost.Gratis, pcie.Cost);
        Assert.StartsWith("Configuração, não hardware", pcie.Where, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameScore_InEnglish()
    {
        InEnglish(() =>
        {
            var r = Insights.Analyze(InsightsTests.ReferenceMachine(), new List<SectionResult>());
            var s = GameScore.Compute(r.Findings);
            Assert.Contains(s.Grade, Grades);
            Assert.Contains("not a performance benchmark", s.Summary);
            if (s.Parts.Count > 0) Assert.Equal(GameScore.AreaOf("gpu"), s.Parts.Select(p => p.Area).First(a => a == GameScore.AreaOf("gpu")));
            Assert.Equal("Platform and GPU", GameScore.AreaOf("gpu"));
        });
    }

    [Fact]
    public void Glossary_Lookup_ByEnglishTitle()
    {
        InEnglish(() =>
        {
            Assert.Equal("processador", Glossary.Lookup("Processor (CPU)")!.Key);
            Assert.Equal("analise", Glossary.Lookup("Platform analysis")!.Key);
            Assert.Equal("rebar", Glossary.Lookup("Resizable BAR (ReBAR)")!.Key);
            Assert.Equal("disco", Glossary.Lookup("Disk 0 [D:]")!.Key);
            Assert.Equal("cadeia_pcie", Glossary.Lookup("Graphics card PCIe chain")!.Key);
            var e = Glossary.Lookup("rebar")!;
            Assert.Equal("Resizable BAR (ReBAR)", e.Title);
            Assert.StartsWith("Lets the processor", e.Text);
            Assert.Equal("placa_de_video", Glossary.Lookup("Any name", "gpu")!.Key);
        });
        // Em português continua achando pelo título em inglês e vice-versa.
        Assert.Equal("rebar", Glossary.Lookup("Resizable BAR (ReBAR)")!.Key);
        Assert.Equal("Resizable BAR (ReBAR)", Glossary.Lookup("rebar")!.Title);
        Assert.StartsWith("Deixa o processador", Glossary.Lookup("rebar")!.Text);
    }

    [Fact]
    public void Glossary_EveryEntry_HasEnglishVersion()
    {
        Assert.All(Glossary.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.TitleEn), e.Key);
            Assert.True(e.TextEn.Length > 60, e.Key);
            Assert.NotEqual(e.TextPt, e.TextEn);
        });
    }

    [Fact]
    public void BiosGuides_InEnglish()
    {
        InEnglish(() =>
        {
            foreach (var topic in Enum.GetValues<BiosTopic>())
                foreach (var board in Boards)
                {
                    var g = BiosGuides.For(topic, board);
                    Assert.NotEmpty(g.Steps);
                    Assert.Contains("Menu names may vary", string.Join(" ", g.Steps));
                    Assert.DoesNotContain("podem variar", string.Join(" ", g.Steps));
                    Assert.False(string.IsNullOrWhiteSpace(g.Revert));
                    Assert.False(string.IsNullOrWhiteSpace(g.Caution));
                    Assert.Matches("^[\\x20-\\x7E]+$", g.Title);
                }
            Assert.Equal("Generic", BiosGuides.For(BiosTopic.Xmp, null).Vendor);
            Assert.Equal("Turn on the memory profile (XMP / DOCP / EXPO)", BiosGuides.For(BiosTopic.Xmp, "Gigabyte H510M").Title);
            Assert.Contains(BiosGuides.For(BiosTopic.PcieSlot, "Gigabyte H510M").Steps, s => s.Contains("PCIEX16 Slot Configuration"));
            Assert.Contains(BiosGuides.For(BiosTopic.ReBar, "Gigabyte H510M").Steps, s => s.Contains("CSM Support"));
        });
    }

    private static ScanEngine Manual()
    {
        var e = new ScanEngine();
        var s = new SectionResult { Key = "sys", Title = "System" };
        var g = s.Group("Test");
        g.Add("Serial number", "E558274B");
        s.Find(Severity.Aviso, "Something", "evidence", "means", "do", impact: 50);
        s.Note("S.M.A.R.T.: requires administrator");
        e.Results.Add(s);
        return e;
    }

    [Fact]
    public void ReportWriter_InEnglish()
    {
        InEnglish(() =>
        {
            var html = ReportWriter.BuildHtml(Manual());
            Assert.Contains("lang=\"en\"", html);
            Assert.DoesNotContain("lang=\"pt-BR\"", html);
            Assert.Contains("<h2>Summary</h2>", html);
            Assert.Contains("Partial readings", html);
            Assert.Contains("Diagnosis", html);
            Assert.Contains("****274B", html);
            Assert.DoesNotContain("E558274B", html);

            var txt = ReportWriter.BuildTxt(Manual());
            Assert.Contains("DIAGNOSIS", txt);
            Assert.Contains("Partial readings:", txt);
            Assert.Contains("Likely bottlenecks for gaming", txt);
            Assert.Contains("[WARNING]", txt);

            Assert.Matches(@"^WinDiag_Report_\d{8}_\d{4}\.html$", ReportWriter.DefaultFileName("html"));
            Assert.Equal(@"C:\Users\<USER>\AppData\x", ReportMask.User(@"C:\Users\Fulano\AppData\x", "fulano"));
            Assert.Equal("<USER> on <PC>", ReportMask.User("fulano on MYPC", "fulano", "", "MYPC"));
        });
        Assert.Equal(@"C:\Users\<USUARIO>\AppData\x", ReportMask.User(@"C:\Users\Fulano\AppData\x", "fulano"));
    }
}
