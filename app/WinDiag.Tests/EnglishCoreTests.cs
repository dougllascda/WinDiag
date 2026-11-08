using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.History;
using WinDiag.Core.Live;
using WinDiag.Core.Model;
using WinDiag.Core.Sensors;
using Xunit;

namespace WinDiag.Tests;

/// <summary>Textos em inglês do núcleo (modelo, otimizações, captura, histórico e sensores).</summary>
[Collection("Loc")]
public class EnglishCoreTests
{
    private static void InEnglish(Action body)
    {
        Loc.Set("en");
        try { body(); }
        finally { Loc.Set("pt-BR"); }
    }

    [Fact]
    public void SevLabel_EmIngles()
    {
        InEnglish(() =>
        {
            Assert.Equal("OK", new Finding(Severity.Ok, "t", "e").SevLabel);
            Assert.Equal("INFO", new Finding(Severity.Info, "t", "e").SevLabel);
            Assert.Equal("WARNING", new Finding(Severity.Aviso, "t", "e").SevLabel);
            Assert.Equal("ATTENTION", new Finding(Severity.Atencao, "t", "e").SevLabel);
        });
        Assert.Equal("ATENÇÃO", new Finding(Severity.Atencao, "t", "e").SevLabel);
    }

    [Fact]
    public void FixCatalog_NomesERiscoEmIngles()
    {
        var hyp = FixCatalog.Get("HYPERVISOR_OFF")!;
        Assert.Equal("Alto", hyp.RiskLabel);
        InEnglish(() =>
        {
            Assert.Equal("High", hyp.RiskLabel);
            Assert.Equal("Medium", FixCatalog.Get("WSEARCH")!.RiskLabel);
            Assert.Equal("Low", FixCatalog.Get("HAGS")!.RiskLabel);
            Assert.Contains("hypervisor", hyp.Name, StringComparison.OrdinalIgnoreCase);
            foreach (var f in FixCatalog.All) Assert.False(string.IsNullOrWhiteSpace(f.Explain));
        });
    }

    [Fact]
    public void FixStatus_StatusTextEmIngles()
    {
        var fix = FixCatalog.Get("GAMEDVR")!;
        InEnglish(() =>
        {
            Assert.Equal("Available", new FixStatus { Fix = fix, Kind = FixStatusKind.Disponivel }.StatusText);
            Assert.Equal("Not applicable to this PC", new FixStatus { Fix = fix, Kind = FixStatusKind.NaoAplicavel }.StatusText);
            Assert.StartsWith("Already set", new FixStatus { Fix = fix, Kind = FixStatusKind.AtivoExterno }.StatusText);
            var on = new FixStatus { Fix = fix, Kind = FixStatusKind.AtivoWinDiag, RebootPending = true }.StatusText;
            Assert.StartsWith("ON (applied by WinDiag on ", on);
            Assert.EndsWith("restart to finish", on);
        });
        Assert.Equal("Disponível", new FixStatus { Fix = fix, Kind = FixStatusKind.Disponivel }.StatusText);
    }

    [Fact]
    public void Capture_ResumoEmIngles()
    {
        var samples = Enumerable.Range(1, 10).Select(i => new CaptureSample { Second = i, CpuPct = 95, Gpu3DPct = 40, DiskBusyPct = 10, DpcPct = 0.5 }).ToList();
        InEnglish(() =>
        {
            var s = Capture.Summarize(samples);
            Assert.Contains(s.Rows, r => r.Metric == "Total CPU");
            var text = string.Join(" ", s.Interpretation);
            Assert.Contains("CPU-bound", text);
            Assert.DoesNotContain("limitado", text);
        });
        var pt = Capture.Summarize(samples);
        Assert.Contains(pt.Rows, r => r.Metric == "CPU total");
    }

    [Fact]
    public void ScanHistory_TrocaDeIdiomaNaoGeraMudancaDeFatos()
    {
        var facts = new Dictionary<string, string>
        {
            ["cpu"] = "Intel i3", ["rebar"] = "ativo", ["memcomp"] = "ligada", ["monitors"] = "1920x1080@59,94",
            ["boot"] = "UEFI, Secure Boot ligado", ["ram"] = "15,9 GB", ["gpu"] = "n/d", ["Admin"] = "sim",
        };
        ScanSummary Mk() => new() { When = new DateTime(2026, 1, 1), Facts = new(facts) };
        var a = Mk();
        var b = Mk();
        InEnglish(() => Assert.True(ScanHistory.Compare(a, b).IsEmpty));
        Assert.True(ScanHistory.Compare(a, b).IsEmpty);

        // Arquivo antigo (chaves em português) contra o novo: nada muda.
        var legacy = new ScanSummary
        {
            When = new DateTime(2025, 1, 1),
            Facts = new() { ["CPU"] = "Intel i3", ["Resizable BAR"] = "ativo", ["Compressão de memória"] = "ligada", ["Monitores"] = "1920x1080@59,94",
                ["Modo de boot"] = "UEFI, Secure Boot ligado", ["RAM instalada"] = "15,9 GB", ["GPU"] = "n/d", ["Admin"] = "sim" },
        };
        InEnglish(() => Assert.True(ScanHistory.Compare(legacy, b).IsEmpty));
        Assert.True(ScanHistory.Compare(legacy, b).IsEmpty);
    }

    [Fact]
    public void ScanHistory_RotuloEValorDaMudancaSeguemOIdiomaAtual()
    {
        var a = new ScanSummary { When = new DateTime(2026, 1, 1), Facts = new() { ["ramspeed"] = "2133 MT/s", ["rebar"] = "inativo" } };
        var b = new ScanSummary { When = new DateTime(2026, 1, 2), Facts = new() { ["ramspeed"] = "3200 MT/s", ["rebar"] = "ativo" } };
        InEnglish(() =>
        {
            var d = ScanHistory.Compare(a, b);
            Assert.Contains(d.FactChanges, c => c.Label == "RAM speed");
            var rebar = Assert.Single(d.FactChanges, c => c.Label == "Resizable BAR");
            Assert.Equal("inactive", rebar.Old);
            Assert.Equal("active", rebar.New);
        });
        Assert.Contains(ScanHistory.Compare(a, b).FactChanges, c => c.Label == "RAM velocidade");
    }

    [Fact]
    public void HardwareKindLabel_SegueOIdioma()
    {
        Assert.Equal("Placa de vídeo", HardwareKinds.HardwareKindLabel("Placa de vídeo"));
        InEnglish(() =>
        {
            Assert.Equal("CPU", HardwareKinds.HardwareKindLabel("CPU"));
            Assert.Equal("Motherboard", HardwareKinds.HardwareKindLabel("Placa-mãe"));
            Assert.Equal("Graphics card", HardwareKinds.HardwareKindLabel("Placa de vídeo"));
            Assert.Equal("Memory", HardwareKinds.HardwareKindLabel("Memória"));
            Assert.Equal("Storage", HardwareKinds.HardwareKindLabel("Armazenamento"));
            Assert.Equal("Power supply", HardwareKinds.HardwareKindLabel("Fonte"));
            Assert.Equal("Controller", HardwareKinds.HardwareKindLabel("Controlador"));
            Assert.Equal("Other", HardwareKinds.HardwareKindLabel("Outro"));
            Assert.Equal("Desconhecido", HardwareKinds.HardwareKindLabel("Desconhecido"));
        });
    }
}
