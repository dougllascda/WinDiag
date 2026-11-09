using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Fixes;
using WinDiag.Core.History;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Work;

namespace WinDiag.Tests;

/// <summary>Achados da terceira revisão do backend: lista de programas de fábrica, desinstalador, histórico por perfil, escopos, fila de impressão, atualização, nota parcial, bateria e planos de energia.</summary>
[Collection("Loc")]
public class ReviewBackend3Tests
{
    // ---------------------------------------------------------------- BloatwareDb: antivírus e utilitários do fabricante

    [Theory]
    [InlineData("McAfee Agent")]
    [InlineData("McAfee Endpoint Security")]
    [InlineData("McAfee ePolicy Orchestrator")]
    [InlineData("Norton Business Suite")]
    [InlineData("Avast Business Security")]
    [InlineData("Avast Premium Security")]
    [InlineData("Norton 360")]
    [InlineData("Norton 360 Deluxe")]
    [InlineData("McAfee Total Protection")]
    [InlineData("McAfee Internet Security")]
    [InlineData("McAfee Security Center")]
    [InlineData("McAfee WebAdvisor")]
    [InlineData("Avast Free Antivirus")]
    public void Bloat_PaidOrManagedAntivirus_IsNeverRemove(string name)
    {
        var c = BloatwareDb.Classify(name, "");
        Assert.NotNull(c);
        Assert.Equal(BloatVerdict.Optional, c!.Value.Verdict);
        Assert.Contains("licença paga ou gerenciada pela empresa", c.Value.Reason);
    }

    [Theory]
    [InlineData("McAfee LiveSafe")]
    [InlineData("Norton 360 Trial")]
    [InlineData("McAfee Internet Security Trial")]
    [InlineData("Avast Premium Security (Trial)")]
    public void Bloat_ClearTrialAntivirus_IsRemove(string name) =>
        Assert.Equal(BloatVerdict.Remove, BloatwareDb.Classify(name, "")!.Value.Verdict);

    [Fact]
    public void Bloat_ManagedBeatsTrial()
    {
        // corporativo nunca é Remover, mesmo com a palavra Trial
        Assert.Equal(BloatVerdict.Optional, BloatwareDb.Classify("McAfee Endpoint Security Trial", "")!.Value.Verdict);
    }

    [Theory]
    [InlineData("Lenovo Utility")]
    [InlineData("Lenovo Hotkeys")]
    [InlineData("Lenovo Hotkey Features Integration")]
    [InlineData("ASUS Armoury Crate")]
    [InlineData("Armoury Crate")]
    [InlineData("MyASUS")]
    [InlineData("HP Hotkey Support")]
    [InlineData("Dell Power Manager")]
    [InlineData("Alienware Command Center")]
    [InlineData("Predator Sense")]
    public void Bloat_FnOsdFanUtilities_AreKeep_WithoutClaimingMeasurements(string name)
    {
        var c = BloatwareDb.Classify(name, "")!.Value;
        Assert.Equal(BloatVerdict.Keep, c.Verdict);
        Assert.Contains("costuma controlar", c.Reason);
        Assert.DoesNotContain("100%", c.Reason);
    }

    // ---------------------------------------------------------------- Uninstaller

    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wdun3_" + Guid.NewGuid().ToString("N"))).FullName;

    private static string MakeFile(string dir, params string[] rel)
    {
        var path = Path.Combine(new[] { dir }.Concat(rel).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    [Fact]
    public void Uninstaller_Parse_UnquotedUsesLongestExistingExePrefix_NotFirstExe()
    {
        var dir = TempDir();
        var exe = MakeFile(dir, "my.exe tools", "unins.exe"); // o primeiro ".exe" está no nome da pasta
        var p = Uninstaller.Parse($"{exe} /foo");
        Assert.Equal((exe, "/foo"), p!.Value);
        // prefixo curto inexistente não é aceito
        Assert.Null(Uninstaller.Parse($"{Path.Combine(dir, "nada.exe")} /foo"));
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/SILENT")]
    [InlineData("/VERYSILENT")]
    [InlineData("/SUPPRESSMSGBOXES")]
    [InlineData("/quiet")]
    [InlineData("/qn")]
    [InlineData("/qb-")]
    [InlineData("/passive")]
    [InlineData("--silent")]
    [InlineData("--force-uninstall")]
    [InlineData("-silent")]
    public void Uninstaller_Parse_DropsSilentFlags(string flag)
    {
        var exe = MakeFile(TempDir(), "un.exe");
        var p = Uninstaller.Parse($"\"{exe}\" {flag} --keep=1");
        Assert.Equal((exe, "--keep=1"), p!.Value);
    }

    [Theory]
    [InlineData("un.bat")]
    [InlineData("un.cmd")]
    [InlineData("un.vbs")]
    [InlineData("un.js")]
    [InlineData("un.ps1")]
    [InlineData("un.lnk")]
    public void Uninstaller_Parse_RejectsScriptsAndShortcuts(string file)
    {
        var path = MakeFile(TempDir(), file);
        Assert.Null(Uninstaller.Parse($"\"{path}\""));
        Assert.Null(Uninstaller.Parse(path));
    }

    [Fact]
    public void Uninstaller_Parse_RejectsRundll32_Unc_Relative_AndMissing()
    {
        Assert.Null(Uninstaller.Parse(@"rundll32.exe shell32.dll,Control_RunDLL"));
        Assert.Null(Uninstaller.Parse($"\"{Path.Combine(Environment.SystemDirectory, "rundll32.exe")}\" x,y"));
        Assert.Null(Uninstaller.Parse($"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c del x"));
        Assert.Null(Uninstaller.Parse(@"""\\servidor\share\unins.exe"""));
        Assert.Null(Uninstaller.Parse(@"\\servidor\share\unins.exe /x"));
        Assert.Null(Uninstaller.Parse(@"unins.exe /x"));
        Assert.Null(Uninstaller.Parse(@"""C:\naoexiste_windiag_3\unins.exe"""));
    }

    [Fact]
    public void Uninstaller_Launch_RefusesUserHiveEntries()
    {
        var exe = MakeFile(TempDir(), "un.exe");
        var r = Uninstaller.Launch(new BloatItem { Name = "App do usuário", UninstallCommand = $"\"{exe}\"", FromUserHive = true });
        Assert.False(r.Ok);
        Assert.Contains("Configurações > Aplicativos", r.Error);
    }

    // ---------------------------------------------------------------- ScanHistory por perfil

    private static readonly JsonSerializerOptions HistJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static void Write(string dir, string name, ScanSummary s) =>
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(s, HistJson), new UTF8Encoding(false));

    [Fact]
    public void ScanHistory_LoadLatest_FiltersByProfile_AndOldFilesAreGaming()
    {
        var dir = TempDir();
        // arquivo antigo, sem o campo Profile
        File.WriteAllText(Path.Combine(dir, "scan_20240101_000000.json"), "{\"When\":\"2024-01-01T00:00:00\",\"Version\":\"1\",\"Score\":70}");
        Write(dir, "scan_20240102_000000.json", new ScanSummary { When = new DateTime(2024, 1, 2), Score = 80, Profile = UsageProfile.Work });
        Assert.Equal(80, ScanHistory.LoadLatest(dir, UsageProfile.Work)!.Score);
        var g = ScanHistory.LoadLatest(dir, UsageProfile.Gaming)!;
        Assert.Equal(70, g.Score);
        Assert.Equal(UsageProfile.Gaming, g.Profile);
        Assert.Equal(80, ScanHistory.LoadLatest(dir)!.Score);
    }

    [Fact]
    public void ScanHistory_RecordAndCompare_OnlyComparesSameProfile()
    {
        var dir = TempDir();
        Write(dir, "scan_20240101_000000.json", new ScanSummary { When = new DateTime(2024, 1, 1), Score = 50, Profile = UsageProfile.Gaming });
        var eng = new ScanEngine();
        eng.Facts.Profile = UsageProfile.Work;
        Assert.Null(ScanHistory.RecordAndCompare(eng, dir)); // só há leitura de Jogos
        Thread.Sleep(1100); // o nome do arquivo tem resolução de 1 s
        var diff = ScanHistory.RecordAndCompare(eng, dir);
        Assert.NotNull(diff);
        Assert.Equal(UsageProfile.Work, ScanHistory.Summarize(eng).Profile);
    }

    // ---------------------------------------------------------------- Escopo

    [Theory]
    [InlineData("gpu.rebar_on")]
    [InlineData("gpu.rebar_off")]
    [InlineData("display.below_max_rate")]
    [InlineData("display.low_resolution")]
    [InlineData("sistema.hpet")]
    [InlineData("sistema.tick")]
    [InlineData("sistema.aspm")]
    [InlineData("sistema.usb_suspend")]
    [InlineData("processos.vpn_adapter")]
    [InlineData("memoria.below_rated")]
    public void ScopeMap_GamingOnlyIds(string id) => Assert.Equal(Scope.Gaming, ScopeMap.For(id));

    [Theory]
    [InlineData("sensors.cpu_hot")]
    [InlineData("sensors.cpu_warm")]
    [InlineData("sensors.gpu_hot")]
    [InlineData("sensors.fan_stopped_cpu")]
    [InlineData("sensors.disk_hot.0")]
    [InlineData("sensors.vrm_hot")]
    public void ScopeMap_SensorsStayBoth(string id) => Assert.Equal(Scope.Both, ScopeMap.For(id));

    [Fact]
    public void FixCatalog_UsbAspmHpet_AreGaming()
    {
        foreach (var id in new[] { "USB_SUSPEND_OFF", "ASPM_OFF", "HPET_OFF" })
            Assert.Equal(Scope.Gaming, FixCatalog.Get(id)!.Scope);
    }

    // ---------------------------------------------------------------- Fila de impressão

    [Fact]
    public void ParseJobTime_PlusZeroOffset_IsUtc_AndUntrustedDatesAreDropped()
    {
        var nowLocal = DateTime.Now;
        string Wmi(DateTime utc) => utc.ToString("yyyyMMddHHmmss") + ".000000+000";
        // 20 min atrás em UTC, com offset +000 -> lido como UTC -> 20 min atrás na hora local
        var t = WorkCollector.ParseJobTime(Wmi(DateTime.UtcNow.AddMinutes(-20)), nowLocal)!.Value;
        Assert.InRange((nowLocal - t).TotalMinutes, 19, 21);
        // futuro e mais de 7 dias: não confiáveis
        Assert.Null(WorkCollector.ParseJobTime(Wmi(DateTime.UtcNow.AddHours(3)), nowLocal));
        Assert.Null(WorkCollector.ParseJobTime(Wmi(DateTime.UtcNow.AddDays(-8)), nowLocal));
        Assert.Null(WorkCollector.ParseJobTime("lixo", nowLocal));
        Assert.Null(WorkCollector.ParseJobTime(null, nowLocal));
    }

    [Fact]
    public void IsJobStuck_RequiresAgeAndErrorOrNoProgress()
    {
        var now = new DateTime(2025, 5, 5, 12, 0, 0);
        var old = now.AddMinutes(-30);
        Assert.False(WorkCollector.IsJobStuck(now.AddMinutes(-5), now, 0x2, null, false)); // novo demais
        Assert.False(WorkCollector.IsJobStuck(null, now, 0x2, "Error", false));           // data não confiável
        Assert.True(WorkCollector.IsJobStuck(old, now, 0x2, null, true));                  // erro
        Assert.True(WorkCollector.IsJobStuck(old, now, null, "Paused", true));             // pausado
        Assert.True(WorkCollector.IsJobStuck(old, now, 0x8, null, false));                 // fila não andou
        Assert.False(WorkCollector.IsJobStuck(old, now, 0x0, null, true));                 // fila andando atrás de outro trabalho
        Assert.True(WorkCollector.JobIsActive(0x10, null));
        Assert.False(WorkCollector.JobIsActive(0x1, null));
    }

    [Fact]
    public void SpoolerDisabled_MakesPrintActionsNotApplicable()
    {
        var restart = FixCatalog.Get("SPOOLER_RESTART")!;
        var clear = FixCatalog.Get("PRINT_QUEUE_CLEAR")!;
        var f = new SystemFacts { SpoolerRunning = false, SpoolerDisabled = true, Printers = { new PrinterInfo { Name = "P", JobCount = 1, StuckJobs = 1 } } };
        Assert.False(restart.Applicable(f));
        Assert.False(clear.Applicable(f));
        f.SpoolerDisabled = false;
        Assert.True(restart.Applicable(f));
        Assert.True(clear.Applicable(f));
    }

    [Fact]
    public void SpoolerDisabledFact_ProducesInfoFinding_WithoutFix()
    {
        var res = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(res, new SystemFacts { SpoolerRunning = false, SpoolerDisabled = true });
        var fi = res.Findings.Single(x => x.Id == "trabalho.spooler_disabled");
        Assert.Equal(Severity.Info, fi.Sev);
        Assert.Equal("", fi.FixId);
    }

    [Fact]
    public void FixEngine_ParseRunningServices_ReadsOnlyRunning()
    {
        const string text = "SERVICE_NAME: Fax\r\nDISPLAY_NAME: Fax\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n\r\n" +
            "SERVICE_NAME: PrintNotify\r\nDISPLAY_NAME: x\r\n        STATE              : 1  STOPPED\r\n";
        Assert.Equal("Fax", Assert.Single(FixEngine.ParseRunningServices(text)));
        Assert.Empty(FixEngine.ParseRunningServices(null));
    }

    // ---------------------------------------------------------------- Windows Update

    [Fact]
    public void UpdateOld_UnconfirmedByHistory_IsInfoWithExplanation()
    {
        var now = new DateTime(2025, 6, 1);
        var f = new SystemFacts { LastUpdateInstalled = now.AddDays(-90), LastUpdateUnconfirmed = true };
        var res = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(res, f, new WorkChecks(Now: now));
        var fi = res.Findings.Single(x => x.Id == "trabalho.update_old");
        Assert.Equal(Severity.Info, fi.Sev);
        Assert.Contains("não foi possível confirmar a data pelo histórico do Windows Update", fi.Evidence);

        f.LastUpdateUnconfirmed = false;
        var res2 = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(res2, f, new WorkChecks(Now: now));
        Assert.Equal(Severity.Aviso, res2.Findings.Single(x => x.Id == "trabalho.update_old").Sev);
    }

    // ---------------------------------------------------------------- GameScore parcial

    [Fact]
    public void GameScore_PartialWork_CapsAt95_AndSaysSo()
    {
        var none = new List<Finding>();
        var full = GameScore.Compute(none, UsageProfile.Work);
        Assert.Equal(100, full.Score);
        var partial = GameScore.Compute(none, UsageProfile.Work, partialReadings: true);
        Assert.Equal(95, partial.Score);
        Assert.Contains("Nota parcial: algumas leituras não foram possíveis (ex.: exige administrador).", partial.Summary);
        Assert.Equal(100, GameScore.Compute(none, UsageProfile.Gaming, partialReadings: true).Score); // só vale no perfil Trabalho
        var sec = new SectionResult { Key = "trabalho", Title = "t" };
        Assert.False(GameScore.HasPartialWork(new[] { sec }));
        sec.Note("Boot: exige administrador");
        Assert.True(GameScore.HasPartialWork(new[] { sec }));
        Loc.Set("en");
        try { Assert.Contains("Partial score:", GameScore.Compute(none, UsageProfile.Work, true).Summary); }
        finally { Loc.Set("pt-BR"); }
    }

    // ---------------------------------------------------------------- Bateria

    [Fact]
    public void BatteryReport_SumsDesignOnlyOfBatteriesWithFullCharge_AndFlagsPartial()
    {
        const string xml = "<BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\"><Batteries>" +
            "<Battery><DesignCapacity>50000</DesignCapacity><FullChargeCapacity>40000</FullChargeCapacity></Battery>" +
            "<Battery><DesignCapacity>30000</DesignCapacity><FullChargeCapacity>0</FullChargeCapacity></Battery>" +
            "</Batteries></BatteryReport>";
        var cap = BatteryReport.Parse(xml);
        Assert.Equal(50000, cap.DesignMWh);
        Assert.Equal(40000, cap.FullChargeMWh);
        Assert.True(cap.Partial);
        Assert.Equal(20d, cap.WearPct);

        var ok = BatteryReport.Parse("<R><Battery><DesignCapacity>50000</DesignCapacity><FullChargeCapacity>45000</FullChargeCapacity></Battery></R>");
        Assert.False(ok.Partial);
        var none = BatteryReport.Parse("<R><Battery><DesignCapacity>50000</DesignCapacity></Battery></R>");
        Assert.Null(none.DesignMWh);
        Assert.True(none.Partial);
    }

    // ---------------------------------------------------------------- Plano de energia

    private static readonly string[] AcceptableDesktop = { FixCatalog.BalancedPlanGuid, FixCatalog.HighPerformancePlanGuid };

    [Fact]
    public void PowerPlanWork_Applicability_RequiresMatchingCurrentAndExistingTarget()
    {
        var fix = FixCatalog.Get("POWER_PLAN_WORK")!;
        // desktop em Economia: precisa do GUID alvo na lista
        var desk = new SystemFacts { ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid };
        Assert.False(fix.Applicable(desk));
        desk.PowerPlanGuids.Add(FixCatalog.HighPerformancePlanGuid);
        Assert.True(fix.Applicable(desk));
        // desktop em Equilibrado ou Alto desempenho: aceitável, nada a sugerir
        foreach (var g in AcceptableDesktop)
        {
            desk.ActivePowerPlanGuid = g;
            Assert.False(fix.Applicable(desk));
            Assert.True(fix.IsActive(desk));
        }
        // notebook: Alto desempenho ou Desempenho máximo -> Equilibrado, se existir
        var lap = new SystemFacts { IsLaptop = true, ActivePowerPlanGuid = FixCatalog.UltimatePlanGuid };
        Assert.False(fix.Applicable(lap));
        lap.PowerPlanGuids.Add(FixCatalog.BalancedPlanGuid);
        Assert.True(fix.Applicable(lap));
        lap.ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid;
        Assert.False(fix.Applicable(lap));
        lap.ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid;
        Assert.True(fix.IsActive(lap));
    }
}
