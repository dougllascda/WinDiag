using WinDiag.Core;
using WinDiag.Core.History;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;

namespace WinDiag.Tests;

public class ScoreHistoryTests
{
    private static Finding F(string sec, Severity sev, string title, int impact, string action = "", string explain = "", string fix = "", string ev = "e")
        => new(sev, title, ev, explain, action, fix, impact) { Section = sec };

    private static List<Finding> Machine() => new()
    {
        F("analise", Severity.Atencao, "PCIe limitado por configuração", 95,
            "O que dá para melhorar: na BIOS, use Auto no slot PCIEX16 (custo: grátis). Ganho esperado: até 10% em cenas pesadas. Acima disso só trocando a CPU (custo: hardware).",
            "Onde está o limite: BIOS do slot PCIEX16. A GPU suporta Gen4."),
        F("monitores", Severity.Aviso, "Taxas de atualização diferentes", 70, "Igualar a taxa dos monitores.", "", "fix.hz"),
        F("armazenamento", Severity.Aviso, "Jogos Steam em HDD: D:", 55, "Mover o jogo para um SSD."),
        F("armazenamento", Severity.Aviso, "Jogos Steam em HDD: F:", 55, "Mover o jogo para um SSD."),
        F("armazenamento", Severity.Aviso, "Pouco espaço livre em C:", 20, "Liberar espaço."),
        F("analise", Severity.Info, "Equilíbrio CPU/GPU", 40, "Limitar o FPS."),
    };

    [Fact]
    public void Score_PerfilDaMaquina_FicaRegularOuBoaEPcieMaiorPerda()
    {
        var r = GameScore.Compute(Machine());
        Assert.InRange(r.Score, 55, 89);
        Assert.True(r.Grade == "Regular" || r.Grade == "Boa");
        Assert.Equal("Plataforma e GPU", r.Parts[0].Area);
        Assert.Contains("benchmark", r.Summary);
        Assert.Equal(r.Parts.OrderByDescending(p => p.Lost).Select(p => p.Lost), r.Parts.Select(p => p.Lost));
    }

    [Fact]
    public void Score_SemAchados_Excelente() => Assert.Equal(100, GameScore.Compute(new List<Finding>()).Score);

    [Fact]
    public void Plano_DedupOrdemEParse()
    {
        var plan = ActionPlan.Build(Machine());
        Assert.Equal(Enumerable.Range(1, plan.Count), plan.Select(p => p.Order));
        Assert.Contains(plan, p => p.Title == "Jogos Steam em HDD (D:, F:)");
        Assert.Single(plan, p => p.Title.StartsWith("Jogos Steam"));
        var pcie = plan.Single(p => p.SectionKey == "analise" && p.Title.StartsWith("PCIe"));
        Assert.Equal("BIOS do slot PCIEX16", pcie.Where);
        Assert.Equal(ActionCost.Gratis, pcie.Cost);
        Assert.DoesNotContain("custo", pcie.What);
        Assert.StartsWith("até 10%", pcie.Gain);
        Assert.Equal("analise:pcie limitado por configuracao", pcie.Key);
        Assert.Equal("PCIe limitado por configuração", plan[0].Title);
    }

    [Fact]
    public void Plano_HardwareVemDepoisDeGratis()
    {
        var plan = ActionPlan.Build(new[]
        {
            F("cpu", Severity.Atencao, "CPU fraca", 90, "O que dá para melhorar: trocar a CPU (custo: hardware). Ganho esperado: muito."),
            F("gpu", Severity.Aviso, "Driver", 30, "Atualizar o driver."),
        });
        Assert.Equal(ActionCost.Gratis, plan[0].Cost);
        Assert.Equal(ActionCost.Hardware, plan[1].Cost);
    }

    private static ScanSummary S(bool admin, params FindingSummary[] f)
    {
        var s = new ScanSummary { When = new DateTime(2026, 1, 1), Facts = new() { ["Admin"] = admin ? "sim" : "não", ["RAM velocidade"] = "2133 MT/s" } };
        s.Findings.AddRange(f);
        return s;
    }
    private static FindingSummary FS(string sec, string t, Severity sev) => new() { Key = sec + ":" + t, Section = sec, Title = t, Sev = sev };

    [Fact]
    public void Diff_ResolvidoNovoEFatos()
    {
        var a = S(true, FS("gpu", "A", Severity.Aviso), FS("gpu", "B", Severity.Atencao), FS("gpu", "C", Severity.Aviso), FS("gpu", "D", Severity.Info));
        var b = S(true, FS("gpu", "B", Severity.Aviso), FS("gpu", "C", Severity.Atencao), FS("gpu", "D", Severity.Aviso), FS("gpu", "E", Severity.Aviso));
        b.Facts["RAM velocidade"] = "3200 MT/s";
        var d = ScanHistory.Compare(a, b);
        Assert.Equal("A", Assert.Single(d.Resolved).Title);
        Assert.Equal("B", Assert.Single(d.Improved).Title);
        Assert.Equal("C,D", string.Join(",", d.Worsened.Select(x => x.Title).OrderBy(x => x)));
        Assert.Equal("E", Assert.Single(d.New).Title);
        Assert.Equal("RAM velocidade", Assert.Single(d.FactChanges).Label);
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void Diff_ModoAdminDiferente_IgnoraSecoesDependentes()
    {
        var a = S(true, FS("armazenamento", "SMART", Severity.Aviso), FS("gpu", "X", Severity.Aviso));
        var b = S(false, FS("gpu", "X", Severity.Aviso));
        var d = ScanHistory.Compare(a, b);
        Assert.Empty(d.Resolved);
        Assert.Equal("Modo de leitura", Assert.Single(d.FactChanges).Label);
    }

    [Fact]
    public void Historico_RotacionaEm30Arquivos()
    {
        string dir = Path.Combine(Path.GetTempPath(), "windiag_hist_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 35; i++)
                File.WriteAllText(Path.Combine(dir, $"scan_20250101_0000{i:00}.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "scan_20260101_000000.json"), "lixo");
            var engine = new ScanEngine();
            var diff = ScanHistory.RecordAndCompare(engine, dir);
            Assert.Equal(30, Directory.GetFiles(dir, "scan_*.json").Length);
            Assert.NotNull(diff); // "{}" é legível; o arquivo corrompido é ignorado
            Assert.NotNull(ScanHistory.LoadLatest(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

