using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Fixes;
using WinDiag.Core.Knowledge;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Settings;
using WinDiag.Core.Work;

namespace WinDiag.Tests;

/// <summary>Perfil Trabalho: escopo, lista de programas de fábrica, desinstalador, relatório de bateria, catálogo e regras do WorkCollector.</summary>
[Collection("Loc")]
public class WorkProfileTests
{
    private static readonly string[] GamingTop = { "gpu.hags", "sistema.cpu_max_limited" };
    private static readonly string[] WorkTop = { "sistema.cpu_max_limited", "trabalho.print_stuck" };
    private static readonly string[] WorkAreas = { "Impressão", "Bateria e energia", "Atualizações e segurança", "Armazenamento e memória", "Desempenho e inicialização" };

    private static Finding F(string id, string section, Severity sev, int impact, Scope scope, string action = "Faça algo.") =>
        new(sev, "Título " + id, "ev", "", action, "", impact) { Section = section, Id = id, Scope = scope };

    // ---------------------------------------------------------------- BloatwareDb

    [Theory]
    [InlineData("McAfee LiveSafe", "McAfee, LLC", BloatVerdict.Remove)]
    [InlineData("Norton 360 Trial", "NortonLifeLock", BloatVerdict.Remove)]
    [InlineData("WildTangent Games App", "WildTangent", BloatVerdict.Remove)]
    [InlineData("Candy Crush Saga", "king.com", BloatVerdict.Remove)]
    [InlineData("CandyCrushSaga", "king", BloatVerdict.Remove)]
    [InlineData("CyberLink PowerDVD 14", "CyberLink Corp.", BloatVerdict.Remove)]
    [InlineData("Booking.com", "", BloatVerdict.Remove)]
    [InlineData("Spotify", "Spotify AB", BloatVerdict.Optional)]
    [InlineData("HP JumpStart", "HP Inc.", BloatVerdict.Optional)]
    [InlineData("Dell Customer Connect", "Dell Inc.", BloatVerdict.Optional)]
    [InlineData("Lenovo Welcome", "Lenovo", BloatVerdict.Optional)]
    [InlineData("Armoury Crate", "ASUSTeK", BloatVerdict.Keep)]
    [InlineData("Lenovo Vantage", "Lenovo", BloatVerdict.Keep)]
    [InlineData("Lenovo System Update", "Lenovo", BloatVerdict.Keep)]
    [InlineData("Dell SupportAssist", "Dell Inc.", BloatVerdict.Keep)]
    [InlineData("HP Support Assistant", "HP Inc.", BloatVerdict.Keep)]
    [InlineData("MyASUS", "ASUSTeK", BloatVerdict.Keep)]
    [InlineData("Realtek Audio Driver", "Realtek", BloatVerdict.Keep)]
    [InlineData("NVIDIA Graphics Driver 560.94", "NVIDIA Corporation", BloatVerdict.Keep)]
    [InlineData("Intel(R) Wireless Bluetooth(R)", "Intel Corporation", BloatVerdict.Keep)]
    [InlineData("Qualquer ferramenta Intel", "Intel Corporation", BloatVerdict.Keep)]
    public void BloatwareDb_ClassifiesKnownPrograms(string name, string publisher, BloatVerdict expected)
    {
        var c = BloatwareDb.Classify(name, publisher);
        Assert.NotNull(c);
        Assert.Equal(expected, c!.Value.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(c.Value.Reason));
    }

    [Theory]
    [InlineData("7-Zip", "Igor Pavlov")]
    [InlineData("Microsoft Office Professional", "Microsoft Corporation")]
    [InlineData("Visual Studio Code", "Microsoft Corporation")]
    [InlineData("Amazon Music", "Amazon")]
    [InlineData("", null)]
    public void BloatwareDb_UnknownIsNull(string name, string? publisher) => Assert.Null(BloatwareDb.Classify(name, publisher));

    [Fact]
    public void BloatwareDb_KeepBeatsRemove_AndAvastFreeIsOnlyOptional()
    {
        Assert.Equal(BloatVerdict.Keep, BloatwareDb.Classify("Lenovo Vantage", "Lenovo")!.Value.Verdict);
        Assert.Equal(BloatVerdict.Optional, BloatwareDb.Classify("Avast Free Antivirus", "Avast")!.Value.Verdict);
        Assert.Equal(BloatVerdict.Optional, BloatwareDb.Classify("Avast Premium Security", "Avast")!.Value.Verdict); // pago: nunca Remover
    }

    [Fact]
    public void BloatwareDb_ReasonFollowsLanguage()
    {
        Assert.Contains("Mantenha", BloatwareDb.Classify("Realtek Audio Driver", "Realtek")!.Value.Reason);
        Loc.Set("en");
        try { Assert.Contains("Keep it", BloatwareDb.Classify("Realtek Audio Driver", "Realtek")!.Value.Reason); }
        finally { Loc.Set("pt-BR"); }
    }

    // ---------------------------------------------------------------- Uninstaller

    /// <summary>Cria um .exe vazio (só precisa existir) numa pasta temporária e devolve o caminho.</summary>
    private static string TempExe(params string[] parts)
    {
        var path = Path.Combine(new[] { Path.GetTempPath(), "wdun_" + Guid.NewGuid().ToString("N") }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    [Fact]
    public void Uninstaller_Parse_QuotedPathWithArguments()
    {
        var exe = TempExe("App X", "unins000.exe");
        var p = Uninstaller.Parse($"\"{exe}\" /SILENT /LOG=x");
        Assert.Equal((exe, "/LOG=x"), p!.Value); // /SILENT descartado
        Assert.Equal((exe, ""), Uninstaller.Parse($"\"{exe}\"")!.Value);
    }

    [Fact]
    public void Uninstaller_Parse_UnquotedPathWithSpaces()
    {
        var exe = TempExe("Foo Bar", "uninst.exe");
        var p = Uninstaller.Parse($"{exe} -runfromtemp -removeonly");
        Assert.Equal((exe, "-runfromtemp -removeonly"), p!.Value);
    }
    [Fact]
    public void Uninstaller_Parse_MsiExecBecomesInteractiveUninstall()
    {
        const string g = "{12345678-ABCD-4321-9876-0123456789AB}";
        Assert.Equal(("msiexec.exe", "/X" + g), Uninstaller.Parse("MsiExec.exe /X" + g)!.Value);
        Assert.Equal(("msiexec.exe", "/X" + g), Uninstaller.Parse("MsiExec.exe /I" + g)!.Value);
        // opções silenciosas são descartadas
        Assert.Equal(("msiexec.exe", "/X" + g), Uninstaller.Parse("\"C:\\Windows\\System32\\msiexec.exe\" /X" + g + " /quiet /qn /norestart")!.Value);
        Assert.Null(Uninstaller.Parse("MsiExec.exe /X"));
    }

    [Fact]
    public void Uninstaller_Parse_EmptyIsNull()
    {
        Assert.Null(Uninstaller.Parse(null));
        Assert.Null(Uninstaller.Parse("   "));
    }

    [Fact]
    public void Uninstaller_Launch_WithoutCommandOrMissingFile_FailsWithoutStartingAnything()
    {
        var none = Uninstaller.Launch(new BloatItem { Name = "Sem comando" });
        Assert.False(none.Ok);
        Assert.False(string.IsNullOrWhiteSpace(none.Error));
        var missing = Uninstaller.Launch(new BloatItem { Name = "Sumiu", UninstallCommand = "\"C:\\naoexiste_windiag\\unins.exe\" /x" });
        Assert.False(missing.Ok);
        Assert.Contains("utilizável", missing.Error);
    }

    // ---------------------------------------------------------------- Relatório de bateria

    private const string BatteryXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <ReportInformation><ComputerName>PC</ComputerName></ReportInformation>
          <Batteries>
            <Battery>
              <Id>DELL ABC</Id>
              <Manufacturer>SMP</Manufacturer>
              <DesignCapacity>50000</DesignCapacity>
              <FullChargeCapacity>30000</FullChargeCapacity>
              <CycleCount>312</CycleCount>
            </Battery>
          </Batteries>
        </BatteryReport>
        """;

    [Fact]
    public void BatteryReport_Parse_ReadsCapacitiesAndWear()
    {
        var c = BatteryReport.Parse(BatteryXml);
        Assert.Equal(50000, c.DesignMWh);
        Assert.Equal(30000, c.FullChargeMWh);
        Assert.Equal(40.0, c.WearPct);
    }

    [Fact]
    public void BatteryReport_Parse_SumsBatteries_AndToleratesGarbage()
    {
        var two = BatteryXml.Replace("</Batteries>", "<Battery><DesignCapacity>10000</DesignCapacity><FullChargeCapacity>10000</FullChargeCapacity></Battery></Batteries>");
        var c = BatteryReport.Parse(two);
        Assert.Equal(60000, c.DesignMWh);
        Assert.Equal(40000, c.FullChargeMWh);
        Assert.Equal(33.3, c.WearPct);
        Assert.Null(BatteryReport.Parse("isto não é xml").WearPct);
        Assert.Null(BatteryReport.Parse(null).DesignMWh);
        Assert.Equal(0.0, new BatteryCapacity(1000, 1200).WearPct); // carga completa acima do projeto: sem desgaste negativo
    }

    [Fact]
    public void WorkCollector_ParsesBootTimeAndInstalledOn()
    {
        var xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData><Data Name='BootTime'>45123</Data><Data Name='MainPathBootTime'>30000</Data></EventData></Event>";
        Assert.Equal(45123, WorkCollector.ParseBootTimeMs(xml));
        Assert.Null(WorkCollector.ParseBootTimeMs("<Event/>"));
        Assert.Equal(new DateTime(2026, 10, 14), WorkCollector.ParseInstalledOn("10/14/2026"));
        Assert.Null(WorkCollector.ParseInstalledOn(""));
        Assert.True(WorkCollector.OnAc(2) == true && WorkCollector.OnAc(1) == false && WorkCollector.OnAc(10) is null);
    }

    // ---------------------------------------------------------------- Escopo: Find, mapa, perfil

    [Fact]
    public void Find_UsesScopeMap_ForGamingIds_AndExplicitScope()
    {
        var res = new SectionResult { Key = "gpu", Title = "x" };
        res.Find(Severity.Aviso, "HAGS", "ev", id: "gpu.hags");
        res.Find(Severity.Aviso, "Outro", "ev", id: "gpu.outro");
        res.Find(Severity.Aviso, "Trab", "ev", id: "trabalho.x", scope: Scope.Work);
        Assert.Equal(Scope.Gaming, res.Findings[0].Scope);
        Assert.Equal(Scope.Both, res.Findings[1].Scope);
        Assert.Equal(Scope.Work, res.Findings[2].Scope);
        foreach (var id in new[] { "pcie.config_limited", "pcie.ok", "analise.balance", "analise.balance_cpu", "display.mixed_rates", "storage.steam_hdd.D", "gpu.gamedvr", "gpu.mpo",
                     "gpu.windowed_opt", "gpu.game_mode", "seguranca.hypervisor", "seguranca.hvci", "processos.background_apps" })
            Assert.Equal(Scope.Gaming, ScopeMap.For(id));
        foreach (var id in new[] { "analise.psu", "analise.xmp", "sistema.fast_startup", "memoria.low_available", "sensors.cpu_hot", "sensors.gpu_hot", "sensors.vrm_hot", "" })
            Assert.Equal(Scope.Both, ScopeMap.For(id));
    }

    [Fact]
    public void UserPrefs_ProfileOf_DefaultsToGaming()
    {
        Assert.Equal(UsageProfile.Gaming, UserPrefs.ProfileOf(""));
        Assert.Equal(UsageProfile.Gaming, UserPrefs.ProfileOf(null));
        Assert.Equal(UsageProfile.Gaming, UserPrefs.ProfileOf("Gaming"));
        Assert.Equal(UsageProfile.Gaming, UserPrefs.ProfileOf("qualquer"));
        Assert.Equal(UsageProfile.Work, UserPrefs.ProfileOf("Work"));
        Assert.Equal(UsageProfile.Work, UserPrefs.ProfileOf(" work "));
        Assert.Equal("", new UserPrefs().Profile);
    }

    [Fact]
    public void UserPrefs_Profile_RoundTripsAndOldFileWithoutIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "wd_prefs_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Theme\":\"Dark\"}");
            Assert.Equal("", UserPrefs.Load(path).Profile);
            var p = UserPrefs.Load(path); p.Profile = "Work";
            Assert.True(p.Save(path));
            Assert.Equal(UsageProfile.Work, UserPrefs.ProfileOf(UserPrefs.Load(path).Profile));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- Filtros por escopo

    private static List<Finding> Mixed() => new()
    {
        F("gpu.hags", "gpu", Severity.Aviso, 60, Scope.Gaming, "Ação de jogo."),
        F("sistema.cpu_max_limited", "sistema", Severity.Aviso, 50, Scope.Both, "Ação comum."),
        F("trabalho.print_stuck", "trabalho", Severity.Atencao, 40, Scope.Work, "Ação de trabalho."),
    };

    [Fact]
    public void GameScore_FiltersByProfile_AndOldOverloadIsGaming()
    {
        var all = Mixed();
        var g = GameScore.Compute(all, UsageProfile.Gaming);
        var w = GameScore.Compute(all, UsageProfile.Work);
        Assert.Equal(100 - (int)Math.Round(60 * 0.12) - (int)Math.Round(50 * 0.12), g.Score);
        Assert.Equal(100 - (int)Math.Round(40 * 0.20) - (int)Math.Round(50 * 0.12), w.Score);
        Assert.Equal(g.Score, GameScore.Compute(all).Score);
        Assert.Contains("para jogos", g.Summary);
        Assert.Contains("para trabalho", w.Summary);
        Assert.DoesNotContain(w.Parts, p => p.Reason.Contains("gpu.hags"));
        Assert.Equal("Configuração para jogos", GameScore.TitleOf(UsageProfile.Gaming));
        Assert.Equal("Configuração para trabalho", GameScore.TitleOf(UsageProfile.Work));
    }

    [Fact]
    public void GameScore_WorkAreas_AreTheFiveNewOnes()
    {
        Assert.Equal("Armazenamento e memória", GameScore.AreaOf("memoria", UsageProfile.Work));
        Assert.Equal("Armazenamento e memória", GameScore.AreaOf("armazenamento", UsageProfile.Work));
        Assert.Equal("Desempenho e inicialização", GameScore.AreaOf("processos", UsageProfile.Work));
        Assert.Equal("Atualizações e segurança", GameScore.AreaOf("seguranca", UsageProfile.Work));
        Assert.Equal("Plataforma e GPU", GameScore.AreaOf("gpu"));
        Assert.Equal("Sistema e segurança", GameScore.AreaOf("desconhecida"));

        Assert.Equal("Impressão", GameScore.AreaOf(F("trabalho.print_stuck", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
        Assert.Equal("Bateria e energia", GameScore.AreaOf(F("trabalho.battery_wear", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
        Assert.Equal("Atualizações e segurança", GameScore.AreaOf(F("trabalho.reboot_pending", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
        Assert.Equal("Armazenamento e memória", GameScore.AreaOf(F("trabalho.low_ram", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
        Assert.Equal("Desempenho e inicialização", GameScore.AreaOf(F("trabalho.slow_boot", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));

        var w = GameScore.Compute(Mixed(), UsageProfile.Work);
        Assert.Contains(w.Parts, p => p.Area == "Impressão");
        Loc.Set("en");
        try
        {
            Assert.Equal("Printing", GameScore.AreaOf(F("trabalho.print_stuck", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
            Assert.Equal("Battery and power", GameScore.AreaOf(F("trabalho.battery_wear", "trabalho", Severity.Aviso, 1, Scope.Work), UsageProfile.Work));
            Assert.Contains("for work", GameScore.Compute(Mixed(), UsageProfile.Work).Summary);
        }
        finally { Loc.Set("pt-BR"); }
    }

    [Fact]
    public void ActionPlan_AndBottlenecks_FilterByProfile()
    {
        var all = Mixed();
        var gp = ActionPlan.Build(all, UsageProfile.Gaming);
        var wp = ActionPlan.Build(all, UsageProfile.Work);
        Assert.Contains(gp, s => s.FindingId == "gpu.hags");
        Assert.DoesNotContain(gp, s => s.FindingId == "trabalho.print_stuck");
        Assert.Contains(wp, s => s.FindingId == "trabalho.print_stuck");
        Assert.DoesNotContain(wp, s => s.FindingId == "gpu.hags");
        Assert.Contains(wp, s => s.FindingId == "sistema.cpu_max_limited");
        Assert.Equal(gp.Select(s => s.Key), ActionPlan.Build(all).Select(s => s.Key));

        Assert.Equal(GamingTop, Bottlenecks.Top(all, UsageProfile.Gaming).Select(f => f.Id));
        Assert.Equal(WorkTop, Bottlenecks.Top(all, UsageProfile.Work).Select(f => f.Id));
        Assert.Equal(Bottlenecks.Top(all).Select(f => f.Id), Bottlenecks.Top(all, UsageProfile.Gaming).Select(f => f.Id));
        Assert.Single(Bottlenecks.Top(all, UsageProfile.Work, 1));
    }

    // ---------------------------------------------------------------- Catálogo

    [Fact]
    public void Catalog_Scopes_AndNewItems()
    {
        foreach (var id in new[] { "HAGS", "GAMEDVR", "MPO_OFF", "WINDOWED_OPT", "GAMEMODE_ON", "HYPERVISOR_OFF", "HVCI_OFF" })
            Assert.Equal(Scope.Gaming, FixCatalog.Get(id)!.Scope);
        Assert.Equal(Scope.Both, FixCatalog.Get("TEMP")!.Scope);
        Assert.Equal(Scope.Both, FixCatalog.Get("STARTUP_APPS")!.Scope);
        var plan = FixCatalog.Get("POWER_PLAN_WORK")!;
        Assert.Equal(Scope.Work, plan.Scope);
        Assert.True(plan.Reversible);
        Assert.False(plan.IsAction);
        foreach (var id in new[] { "SPOOLER_RESTART", "PRINT_QUEUE_CLEAR" })
        {
            var a = FixCatalog.Get(id)!;
            Assert.Equal(Scope.Work, a.Scope);
            Assert.True(a.IsAction);
            Assert.False(a.Reversible);
        }
        Assert.Equal(FixCatalog.All.Count, FixCatalog.All.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void PowerPlanWork_ApplicableAndActive_FollowRecommendedPlan()
    {
        var fix = FixCatalog.Get("POWER_PLAN_WORK")!;
        var all = new[] { FixCatalog.BalancedPlanGuid, FixCatalog.HighPerformancePlanGuid, FixCatalog.PowerSaverPlanGuid };
        var desk = new SystemFacts { IsLaptop = false, ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid, PowerPlanGuids = all.ToList() };
        Assert.Equal(FixCatalog.HighPerformancePlanGuid, FixCatalog.WorkPlanGuid(desk));
        Assert.True(fix.Applicable(desk));
        Assert.False(fix.IsActive(desk));
        desk.ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid.ToUpperInvariant();
        Assert.False(fix.Applicable(desk));
        Assert.True(fix.IsActive(desk));
        desk.ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid; // Equilibrado também é aceitável em desktop
        Assert.False(fix.Applicable(desk));
        Assert.True(fix.IsActive(desk));

        var lap = new SystemFacts { IsLaptop = true, ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid, PowerPlanGuids = all.ToList() };
        Assert.Equal(FixCatalog.BalancedPlanGuid, FixCatalog.WorkPlanGuid(lap));
        Assert.True(fix.Applicable(lap));
        lap.ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid;
        Assert.True(fix.IsActive(lap));

        Assert.False(fix.Applicable(new SystemFacts { ActivePowerPlanGuid = null }));
        Assert.False(fix.Applicable(new SystemFacts { IsAtlas = true, ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid }));
    }

    [Fact]
    public void PrintActions_Applicable_OnlyWithStuckJobsOrSpoolerProblem()
    {
        var restart = FixCatalog.Get("SPOOLER_RESTART")!;
        var clear = FixCatalog.Get("PRINT_QUEUE_CLEAR")!;
        var ok = new SystemFacts { SpoolerRunning = true, Printers = { new PrinterInfo { Name = "P" } } };
        Assert.False(restart.Applicable(ok));
        Assert.False(clear.Applicable(ok));
        var stuck = new SystemFacts { SpoolerRunning = true, Printers = { new PrinterInfo { Name = "P", JobCount = 2, StuckJobs = 2 } } };
        Assert.True(restart.Applicable(stuck));
        Assert.True(clear.Applicable(stuck));
        var down = new SystemFacts { SpoolerRunning = false };
        Assert.True(restart.Applicable(down));
        Assert.False(clear.Applicable(down));
    }

    [Fact]
    public void GetStatuses_ReturnsAllScopes_AndActionsAreNeverToggleable()
    {
        var store = new SnapshotStore(Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N")));
        var facts = new SystemFacts { SpoolerRunning = false, ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid, PowerPlanGuids = { FixCatalog.HighPerformancePlanGuid } };
        var st = new FixEngine(store, dryRun: true).GetStatuses(facts);
        Assert.Equal(FixCatalog.All.Count, st.Count);
        Assert.Contains(st, s => s.Fix.Scope == Scope.Gaming);
        var act = st.Single(s => s.Fix.Id == "SPOOLER_RESTART");
        Assert.Equal(FixStatusKind.Disponivel, act.Kind);
        Assert.False(act.CanToggle);
        Assert.Equal(FixStatusKind.Disponivel, st.Single(s => s.Fix.Id == "POWER_PLAN_WORK").Kind);
    }

    [Fact]
    public void RunAction_DryRun_ChangesNothing_AndWritesNoSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N"));
        var eng = new FixEngine(new SnapshotStore(dir), dryRun: true);
        var r1 = eng.RunAction(FixCatalog.Get("SPOOLER_RESTART")!);
        var r2 = eng.RunAction(FixCatalog.Get("PRINT_QUEUE_CLEAR")!);
        Assert.True(r1.Ok && r2.Ok);
        Assert.All(r1.Messages.Concat(r2.Messages), m => Assert.StartsWith("[simulação]", m));
        Assert.Null(r1.SnapshotPath);
        Assert.False(Directory.Exists(dir));
        var notAction = eng.RunAction(FixCatalog.Get("HAGS")!);
        Assert.False(notAction.Ok);
    }

    [Fact]
    public void PowerPlanWork_DryRunEnable_DoesNotWriteSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N"));
        var r = new FixEngine(new SnapshotStore(dir), dryRun: true).Enable(FixCatalog.Get("POWER_PLAN_WORK")!, new SystemFacts { IsLaptop = true });
        Assert.True(r.Ok);
        Assert.Null(r.SnapshotPath);
        Assert.Contains(FixCatalog.BalancedPlanGuid, string.Join(" ", r.Messages));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void FixState_PowerPlan_RoundTripsInSnapshot_AndRestoreSimulatesSetActive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N"));
        var store = new SnapshotStore(dir);
        try
        {
            var snap = new Snapshot { Version = "t", Fixes = { new SnapshotFix { Id = "POWER_PLAN_WORK", Name = "x", Reversible = true, State = new FixState { PowerPlan = FixCatalog.BalancedPlanGuid } } } };
            var path = store.Save(snap);
            Assert.Equal(FixCatalog.BalancedPlanGuid, store.List().Single().Data.Fixes[0].State.PowerPlan);
            var msgs = new FixEngine(store, dryRun: true).Restore(store.List().Single().Data.Fixes[0].State);
            Assert.Contains(msgs, m => m.Contains(FixCatalog.BalancedPlanGuid));
            Assert.True(FixEngine.StateDiffers(new FixState { PowerPlan = "a" }, new FixState { PowerPlan = "b" }));
            Assert.False(FixEngine.StateDiffers(new FixState { PowerPlan = "A" }, new FixState { PowerPlan = "a" }));
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void ClearSpoolDirectory_DeletesFilesOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wd_spool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "00001.SPL"), "x");
        File.WriteAllText(Path.Combine(dir, "00001.SHD"), "x");
        try
        {
            Assert.Equal((2, 0), FixEngine.ClearSpoolDirectory(dir));
            Assert.True(Directory.Exists(Path.Combine(dir, "sub")));
            Assert.Equal((0, 0), FixEngine.ClearSpoolDirectory(Path.Combine(dir, "nao_existe")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- Regras do WorkCollector

    private static SectionResult Rules(SystemFacts f, WorkChecks? x = null)
    {
        var res = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(res, f, x);
        return res;
    }

    [Fact]
    public void WorkRules_Printing()
    {
        var f = new SystemFacts
        {
            SpoolerRunning = true,
            Printers =
            {
                new PrinterInfo { Name = "HP LaserJet", Offline = true, StatusText = "offline", JobCount = 3, StuckJobs = 2 },
                new PrinterInfo { Name = "PDF" },
            },
        };
        var r = Rules(f);
        var off = r.Findings.Single(x => x.Id == "trabalho.printer_offline");
        Assert.Equal(Severity.Aviso, off.Sev);
        Assert.Contains("HP LaserJet", off.Evidence);
        var stuck = r.Findings.Single(x => x.Id == "trabalho.print_stuck");
        Assert.Equal("PRINT_QUEUE_CLEAR", stuck.FixId);
        Assert.All(r.Findings, x => { Assert.Equal(Scope.Work, x.Scope); Assert.False(string.IsNullOrWhiteSpace(x.Simple)); });
        Assert.DoesNotContain(r.Findings, x => x.Id.StartsWith("trabalho.spooler"));

        var down = Rules(new SystemFacts { SpoolerRunning = false }).Findings.Single();
        Assert.Equal(("trabalho.spooler_stopped", Severity.Atencao, "SPOOLER_RESTART"), (down.Id, down.Sev, down.FixId));
        var disabled = Rules(new SystemFacts { SpoolerRunning = false }, new WorkChecks(SpoolerDisabled: true)).Findings.Single();
        Assert.Equal(("trabalho.spooler_disabled", Severity.Info, ""), (disabled.Id, disabled.Sev, disabled.FixId));
    }

    [Fact]
    public void WorkRules_Bloat_UpdatesAndReboot()
    {
        var now = new DateTime(2026, 11, 8);
        var f = new SystemFacts
        {
            Bloat =
            {
                new BloatItem { Name = "McAfee LiveSafe", Verdict = BloatVerdict.Remove },
                new BloatItem { Name = "Candy Crush", Verdict = BloatVerdict.Remove },
                new BloatItem { Name = "Spotify", Verdict = BloatVerdict.Optional },
                new BloatItem { Name = "Lenovo Vantage", Verdict = BloatVerdict.Keep },
            },
            RebootPending = true,
            LastUpdateInstalled = now.AddDays(-46),
        };
        var r = Rules(f, new WorkChecks(Now: now));
        var bloat = r.Findings.Single(x => x.Id == "trabalho.bloat");
        Assert.Equal(Severity.Aviso, bloat.Sev);
        Assert.StartsWith("2 programa(s)", bloat.Title);
        Assert.Equal(16, bloat.Impact);
        Assert.Equal(Severity.Aviso, r.Findings.Single(x => x.Id == "trabalho.reboot_pending").Sev);
        Assert.Equal(Severity.Aviso, r.Findings.Single(x => x.Id == "trabalho.update_old").Sev);
        Assert.Contains("(46 dias)", r.Findings.Single(x => x.Id == "trabalho.update_old").Evidence);
        Assert.Contains("23/09/2026", r.Findings.Single(x => x.Id == "trabalho.update_old").Evidence);

        f.LastUpdateInstalled = now.AddDays(-45);
        f.RebootPending = false;
        f.Bloat.Clear();
        Assert.Empty(Rules(f, new WorkChecks(Now: now)).Findings);
    }

    [Theory]
    [InlineData(39.9, null)]
    [InlineData(40.0, Severity.Aviso)]
    [InlineData(59.9, Severity.Aviso)]
    [InlineData(60.0, Severity.Atencao)]
    public void WorkRules_BatteryWear_Thresholds(double wear, Severity? expected)
    {
        var f = new SystemFacts { HasBattery = true, IsLaptop = true, BatteryWearPct = wear, BatteryDesignMWh = 50000, BatteryFullMWh = (int)(50000 * (1 - wear / 100)) };
        var fi = Rules(f).Findings.Single(x => x.Id == "trabalho.battery_wear");
        if (expected is null) Assert.Equal(Severity.Ok, fi.Sev);
        else Assert.Equal(expected, fi.Sev);
        Assert.Empty(Rules(new SystemFacts { HasBattery = false, BatteryWearPct = 70 }).Findings);
    }

    [Fact]
    public void WorkRules_Power_Laptop_NeverSuggestsHibernationOff()
    {
        var lap = new SystemFacts { IsLaptop = true, HasBattery = true, OnAcPower = false, HibEnabled = true, ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid, ActivePowerPlanName = "Alto desempenho", PowerPlanGuids = { FixCatalog.BalancedPlanGuid } };
        var r = Rules(lap);
        var f1 = r.Findings.Single(x => x.Id == "trabalho.power_battery_high");
        Assert.Equal(Severity.Info, f1.Sev);
        Assert.Equal("POWER_PLAN_WORK", f1.FixId);
        Assert.DoesNotContain(r.Findings, x => x.Id == "trabalho.power_plan");
        Assert.DoesNotContain(r.Findings, x => x.FixId == "HIBER");
        Assert.False(FixCatalog.Get("HIBER")!.Applicable(lap)); // HIBER nunca se aplica a notebook

        // plano personalizado: nada é sugerido
        var other = Rules(new SystemFacts { IsLaptop = true, ActivePowerPlanGuid = "11111111-2222-3333-4444-555555555555", ActivePowerPlanName = "Personalizado", PowerPlanGuids = { FixCatalog.BalancedPlanGuid } });
        Assert.DoesNotContain(other.Findings, x => x.Id == "trabalho.power_plan");
        // desktop em Economia de energia: sugere Alto desempenho
        var saver = Rules(new SystemFacts { IsLaptop = false, ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid, ActivePowerPlanName = "Economia de energia", PowerPlanGuids = { FixCatalog.HighPerformancePlanGuid } });
        var p = saver.Findings.Single(x => x.Id == "trabalho.power_plan");
        Assert.Equal((Severity.Info, "POWER_PLAN_WORK"), (p.Sev, p.FixId));

        var okPlan = Rules(new SystemFacts { IsLaptop = false, ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid, ActivePowerPlanName = "Alto desempenho" });
        Assert.Equal(Severity.Ok, okPlan.Findings.Single(x => x.Id == "trabalho.power_plan").Sev);
    }

    [Fact]
    public void WorkRules_Boot_Ram_AndSystemHdd()
    {
        Assert.Equal(Severity.Aviso, Rules(new SystemFacts { BootTimeMs = 61000 }).Findings.Single(x => x.Id == "trabalho.slow_boot").Sev);
        Assert.Equal(Severity.Ok, Rules(new SystemFacts { BootTimeMs = 60000 }).Findings.Single(x => x.Id == "trabalho.slow_boot").Sev);
        Assert.Empty(Rules(new SystemFacts()).Findings);

        var ram = Rules(new SystemFacts(), new WorkChecks(InstalledRamGB: 4)).Findings.Single(x => x.Id == "trabalho.low_ram");
        Assert.Equal(Severity.Aviso, ram.Sev);
        Assert.Contains("(custo: hardware)", ram.Action);
        Assert.Empty(Rules(new SystemFacts(), new WorkChecks(InstalledRamGB: 8)).Findings);
        Assert.Empty(Rules(new SystemFacts { RamGB = 7.8 }).Findings); // 8 GB com parte reservada não conta como pouca RAM

        var hdd = Rules(new SystemFacts(), new WorkChecks(SystemDriveIsHdd: true)).Findings.Single();
        Assert.Equal(("trabalho.system_hdd", Severity.Atencao), (hdd.Id, hdd.Sev));
        Assert.Empty(Rules(new SystemFacts(), new WorkChecks(SystemDriveIsHdd: false)).Findings);
    }

    [Fact]
    public void WorkRules_AllIdsUnique_AndPlansMakeSense()
    {
        var now = new DateTime(2026, 11, 8);
        var f = new SystemFacts
        {
            IsLaptop = true, HasBattery = true, OnAcPower = false, BatteryWearPct = 65, BatteryDesignMWh = 50000, BatteryFullMWh = 17500,
            ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid, ActivePowerPlanName = "High performance",
            SpoolerRunning = false, RebootPending = true, LastUpdateInstalled = now.AddDays(-90), BootTimeMs = 90000,
            Printers = { new PrinterInfo { Name = "P", Offline = true, StuckJobs = 1, JobCount = 1, StatusText = "offline" } },
            Bloat = { new BloatItem { Name = "Norton 360", Verdict = BloatVerdict.Remove } },
        };
        var r = Rules(f, new WorkChecks(InstalledRamGB: 4, SystemDriveIsHdd: true, Now: now));
        Assert.Equal(r.Findings.Count, r.Findings.Select(x => x.Id).Distinct().Count());
        Assert.All(r.Findings, x => Assert.StartsWith("trabalho.", x.Id));
        var plan = ActionPlan.Build(r.Findings, UsageProfile.Work);
        Assert.NotEmpty(plan);
        Assert.Empty(ActionPlan.Build(r.Findings, UsageProfile.Gaming));
        var score = GameScore.Compute(r.Findings, UsageProfile.Work);
        Assert.InRange(score.Score, 0, 99);
        Assert.Empty(GameScore.Compute(r.Findings, UsageProfile.Gaming).Parts);
        Assert.All(score.Parts.Select(p => p.Area), a => Assert.Contains(a, WorkAreas));
    }

    [Fact]
    public void WorkRules_InEnglish_HaveNoPortuguese()
    {
        Loc.Set("en");
        try
        {
            var now = new DateTime(2026, 11, 8);
            var f = new SystemFacts
            {
                IsLaptop = true, HasBattery = true, OnAcPower = false, BatteryWearPct = 65, BatteryDesignMWh = 50000, BatteryFullMWh = 17500,
                ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid, ActivePowerPlanName = "High performance",
                SpoolerRunning = false, RebootPending = true, LastUpdateInstalled = now.AddDays(-90), BootTimeMs = 90000,
                Printers = { new PrinterInfo { Name = "P", Offline = true, StuckJobs = 1, JobCount = 1, StatusText = "offline" } },
                Bloat = { new BloatItem { Name = "Norton 360", Verdict = BloatVerdict.Remove } },
            };
            var pt = new System.Text.RegularExpressions.Regex(@"[ãõçáéíóúâêôà]|\b(não|você|placa|memória|leitura|exige|disponível)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var all = new List<SectionResult> { Rules(f, new WorkChecks(InstalledRamGB: 4, SystemDriveIsHdd: true, Now: now)), Rules(new SystemFacts { SpoolerRunning = false }, new WorkChecks(SpoolerDisabled: true)) };
            foreach (var x in all.SelectMany(s => s.Findings))
                foreach (var t in new[] { x.Title, x.Explain, x.Action, x.Simple, x.SevLabel })
                    Assert.False(pt.IsMatch(t), x.Id + ": " + t);
            foreach (var fx in new[] { "POWER_PLAN_WORK", "SPOOLER_RESTART", "PRINT_QUEUE_CLEAR" })
            {
                var d = FixCatalog.Get(fx)!;
                foreach (var t in new[] { d.Name, d.ShortName, d.Explain }) Assert.False(pt.IsMatch(t), fx + ": " + t);
            }
            foreach (var s in new[] { GameScore.TitleOf(UsageProfile.Work), BloatwareDb.Label(BloatVerdict.Remove) }) Assert.False(pt.IsMatch(s), s);
        }
        finally { Loc.Set("pt-BR"); }
    }

    // ---------------------------------------------------------------- Coletor e glossário

    [Fact]
    public void Collector_IsRegistered_AndLiveCollectIsSafe()
    {
        Assert.Contains(ScanEngine.CreateCollectors(), c => c.Key == "trabalho");
        var facts = new SystemFacts();
        var r = new WorkCollector().Collect(facts);
        Assert.Equal("trabalho", r.Key);
        Assert.Equal("Trabalho e escritório", r.Title);
        Assert.All(r.Findings, f => { Assert.Equal(Scope.Work, f.Scope); Assert.StartsWith("trabalho.", f.Id); });
        Assert.Equal(r.Findings.Count, r.Findings.Select(f => f.Id).Distinct().Count());
        Assert.Equal(6, r.Groups.Count);
        Assert.NotNull(facts.ActivePowerPlanGuid ?? "n/d");
    }

    [Fact]
    public void Glossary_CoversWorkTermsAndGroups_InBothLanguages()
    {
        foreach (var t in new[] { "Spooler de impressão", "Fila de impressão", "Programas de fábrica (bloatware)", "Saúde da bateria", "Plano de energia", "Reinício pendente", "Tempo de inicialização",
                     "Trabalho e escritório", "Impressoras", "Programas de fábrica", "Windows Update", "Bateria" })
            Assert.NotNull(Glossary.Lookup(t));
        foreach (var t in new[] { "Print spooler", "Print queue", "Factory programs (bloatware)", "Battery health", "Power plan", "Pending restart", "Boot time", "Work and office", "Printers", "Factory programs", "Battery" })
            Assert.NotNull(Glossary.Lookup(t));
        Assert.Equal("saude_bateria", Glossary.Lookup("Saúde da bateria")!.Key);
        Assert.Equal("plano_energia", Glossary.Lookup("Plano de energia")!.Key);
        Assert.Equal("bloatware", Glossary.Lookup("Factory programs")!.Key);
        foreach (var k in new[] { "spooler", "fila_impressao", "bloatware", "saude_bateria", "plano_energia", "reinicio_pendente", "tempo_inicializacao" })
        {
            var e = Glossary.Lookup(k)!;
            Assert.NotEqual(e.TitlePt, e.TitleEn);
            Assert.NotEqual(e.TextPt, e.TextEn);
        }
    }
}
