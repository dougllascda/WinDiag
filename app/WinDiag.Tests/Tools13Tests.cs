#pragma warning disable CA1861 // arrays constantes em asserts de teste
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Settings;
using WinDiag.Core.Tools;
using WinDiag.Core.Update;

namespace WinDiag.Tests;

/// <summary>Testes da versão 1.3: ferramentas, atualização do app, novos ajustes e notas. Nenhum acessa a rede nem altera o sistema real.</summary>
[Collection("Loc")]
public class Tools13Tests
{
    private static string Tmp(string tag) => Path.Combine(Path.GetTempPath(), "wd13_" + tag + "_" + Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- catálogo e DryRun

    [Fact]
    public void Catalog_HasAllIds_UniqueAndDescribed()
    {
        var expected = new[]
        {
            "PRINT_UNSTICK", "SPOOLER_RESTART", "PRINT_TEST_PAGE", "NET_TEST", "NET_DNS_FLUSH", "NET_IP_RENEW", "NET_STACK_RESET", "TIME_SYNC",
            "DISK_CLEANUP", "SHADER_CACHE_CLEAR", "SYS_SFC", "SYS_DISM", "SYS_CHKDSK", "WU_RESET", "EXPLORER_RESTART", "MEM_DIAG", "RESTORE_POINT",
            "BATTERY_REPORT", "OPEN_DEVMGMT", "OPEN_PRINTERS", "OPEN_APPS", "OPEN_WINUPDATE", "OPEN_DISKMGMT", "OPEN_RELIABILITY",
        };
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var id in expected) Assert.NotNull(ToolCatalog.Get(id));
        foreach (var t in ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.NameText.Pt) || string.IsNullOrWhiteSpace(t.NameText.En), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.ExplainText.Pt) || string.IsNullOrWhiteSpace(t.ExplainText.En), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.DurationText.Pt) || string.IsNullOrWhiteSpace(t.DurationText.En), t.Id);
            Assert.NotEmpty(t.Commands);
            Assert.All(t.CommandLines, c => Assert.False(string.IsNullOrWhiteSpace(c.Pt) || string.IsNullOrWhiteSpace(c.En)));
            Assert.False(string.IsNullOrWhiteSpace(ToolCatalog.CategoryName(t.Category)));
        }
        Assert.True(ToolCatalog.Get("SYS_SFC")!.LongRunning && ToolCatalog.Get("SYS_SFC")!.NeedsAdmin);
        Assert.True(ToolCatalog.Get("NET_STACK_RESET")!.NeedsRestart);
        Assert.True(ToolCatalog.Get("NET_TEST")!.ReadOnly && !ToolCatalog.Get("NET_TEST")!.NeedsAdmin);
        Assert.Equal(new[] { "DISK_CLEANUP" }, ToolCatalog.All.Where(t => t.HasOptions).Select(t => t.Id));
        Assert.All(ToolCatalog.All.Where(t => t.Category == ToolCategory.Shortcuts), t => Assert.True(t.ReadOnly));
        Assert.Equal(ToolCatalog.Get("net_test"), ToolCatalog.Get("NET_TEST"));
        Assert.Null(ToolCatalog.Get("nao_existe"));
    }

    [Fact]
    public void Catalog_TextsFollowLanguage()
    {
        var t = ToolCatalog.Get("NET_DNS_FLUSH")!;
        try
        {
            Loc.Set("en");
            Assert.Equal(t.NameText.En, t.Name);
            Assert.Equal(t.ExplainText.En, t.Explain);
            Assert.Equal(t.DurationText.En, t.Duration);
            Assert.Equal(t.CommandLines.Select(c => c.En), t.Commands);
        }
        finally { Loc.Set("pt-BR"); }
        Assert.Equal(t.NameText.Pt, t.Name);
    }

    [Fact]
    public async Task DryRun_OfAllTools_ChangesNothing()
    {
        var dir = Tmp("dry");
        var runner = new ToolRunner(new SnapshotStore(dir), dryRun: true);
        foreach (var t in ToolCatalog.All)
        {
            var r = await runner.RunAsync(t, null, CancellationToken.None);
            Assert.True(r.Ok, t.Id + ": " + r.Error);
            Assert.NotEmpty(r.Messages);
            Assert.All(r.Messages, m => Assert.StartsWith("[simulação]", m));
            Assert.Null(r.SnapshotPath);
        }
        Assert.False(Directory.Exists(dir));
        Assert.Empty(runner.History());
    }

    [Fact]
    public async Task DryRun_DiskCleanup_ListsOnlySelectedOptions()
    {
        var runner = new ToolRunner(new SnapshotStore(Tmp("dry2")), dryRun: true);
        var r = await runner.RunAsync(ToolCatalog.Get("DISK_CLEANUP")!, null, CancellationToken.None, new[] { "thumbnails" });
        Assert.Single(r.Messages);
        Assert.Contains(DiskCleanup.Label("thumbnails"), r.Messages[0]);
        var def = await runner.RunAsync(ToolCatalog.Get("DISK_CLEANUP")!, null, CancellationToken.None);
        Assert.Equal(DiskCleanup.AllOptionIds.Count(DiskCleanup.DefaultOn), def.Messages.Count);
        Assert.DoesNotContain(def.Messages, m => m.Contains(DiskCleanup.Label("recycle")));
    }

    [Fact]
    public void BlockReason_ChecksAdminPrinterAndBattery()
    {
        var runner = new ToolRunner(new SnapshotStore(Tmp("blk")), dryRun: true);
        var noAdmin = new SystemFacts { IsAdmin = false };
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("SYS_SFC")!, noAdmin));
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("NET_DNS_FLUSH")!, noAdmin));
        Assert.Null(runner.BlockReason(ToolCatalog.Get("NET_DNS_FLUSH")!, new SystemFacts { IsAdmin = true }));
        Assert.Null(runner.BlockReason(ToolCatalog.Get("NET_TEST")!, noAdmin));
        Assert.Null(runner.BlockReason(ToolCatalog.Get("SPOOLER_RESTART")!, new SystemFacts { IsAdmin = true }));
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("SPOOLER_RESTART")!, new SystemFacts { IsAdmin = true, SpoolerDisabled = true }));
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("PRINT_TEST_PAGE")!, new SystemFacts()));
        Assert.Null(runner.BlockReason(ToolCatalog.Get("PRINT_TEST_PAGE")!, new SystemFacts { Printers = { new PrinterInfo { Name = "P", IsDefault = true } } }));
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("BATTERY_REPORT")!, new SystemFacts()));
        Assert.Null(runner.BlockReason(ToolCatalog.Get("BATTERY_REPORT")!, new SystemFacts { HasBattery = true }));
    }

    // ---------------------------------------------------------------- histórico

    [Fact]
    public async Task History_WritesReadsAndToleratesCorruption()
    {
        var dir = Tmp("hist");
        try
        {
            var runner = new ToolRunner(new SnapshotStore(dir));
            var unknown = new ToolDefinition { Id = "TEST_UNKNOWN", NameText = ("a", "a"), ExplainText = ("b", "b") };
            var r = await runner.RunAsync(unknown, null, CancellationToken.None);
            Assert.False(r.Ok);
            var h = runner.History();
            Assert.Single(h);
            Assert.Equal("TEST_UNKNOWN", h[0].ToolId);
            Assert.False(h[0].Ok);
            Assert.Contains("TEST_UNKNOWN", File.ReadAllText(new SnapshotStore(dir).ChangeLog));

            await runner.RunAsync(unknown, null, CancellationToken.None);
            Assert.Equal(2, runner.History().Count);
            Assert.Single(runner.History(1));

            File.WriteAllText(runner.HistoryPath, "{ isto não é json ]");
            Assert.Empty(runner.History());
            await runner.RunAsync(unknown, null, CancellationToken.None); // sobrescreve o arquivo ruim
            Assert.Single(runner.History());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task History_KeepsLast200()
    {
        var dir = Tmp("hist200");
        try
        {
            var runner = new ToolRunner(new SnapshotStore(dir));
            Directory.CreateDirectory(dir);
            var entries = Enumerable.Range(0, 205).Select(i => new ToolHistoryEntry(new DateTime(2026, 1, 1).AddMinutes(i), "X" + i, true, "ok"));
            File.WriteAllText(runner.HistoryPath, System.Text.Json.JsonSerializer.Serialize(entries));
            await runner.RunAsync(new ToolDefinition { Id = "TEST_UNKNOWN", NameText = ("a", "a"), ExplainText = ("b", "b") }, null, CancellationToken.None);
            var all = runner.History(1000);
            Assert.Equal(200, all.Count);
            Assert.Equal("TEST_UNKNOWN", all[0].ToolId);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- rede

    [Fact]
    public void NetDiag_JitterLossAndAverage()
    {
        Assert.Null(NetDiag.Jitter(new double[] { 10 }));
        Assert.Equal(5.0, NetDiag.Jitter(new double[] { 10, 15, 10, 15 })!.Value, 6);
        var s = NetDiag.Compute("x", new double?[] { 10, null, 20, 10, null, 20, 10, 20, 10, 20 });
        Assert.Equal(10, s.Sent);
        Assert.Equal(8, s.Received);
        Assert.Equal(20, s.LossPct, 6);
        Assert.Equal(15, s.AvgMs!.Value, 6);
        var dead = NetDiag.Compute("y", new double?[] { null, null });
        Assert.Null(dead.AvgMs);
        Assert.Equal(100, dead.LossPct);
        Assert.Equal(100, NetDiag.Compute("z", Array.Empty<double?>()).LossPct);
    }

    private static PingStats Ps(double avg, double loss, double jitter = 1) => new("t", 10, (int)Math.Round(10 - loss / 10), avg, jitter, loss);

    [Fact]
    public void NetDiag_Verdicts()
    {
        var good = new[] { Ps(20, 0), Ps(22, 0) };
        Assert.Equal(NetVerdict.AllGood, NetDiag.Verdict(Ps(2, 0), good, true));
        Assert.Equal(NetVerdict.LocalProblem, NetDiag.Verdict(Ps(2, 20), good, true));
        Assert.Equal(NetVerdict.LocalProblem, NetDiag.Verdict(Ps(60, 0), good, true));
        Assert.Equal(NetVerdict.ProviderProblem, NetDiag.Verdict(Ps(2, 0), new[] { Ps(30, 10), Ps(30, 0) }, true));
        Assert.Equal(NetVerdict.ProviderProblem, NetDiag.Verdict(Ps(2, 0), new[] { Ps(300, 0), Ps(300, 0) }, true));
        Assert.Equal(NetVerdict.DnsProblem, NetDiag.Verdict(Ps(2, 0), good, false));
        var none = new PingStats("n", 10, 0, null, null, 100);
        Assert.Equal(NetVerdict.NoConnection, NetDiag.Verdict(none, new[] { none, none }, false));
        Assert.Equal(NetVerdict.ProviderProblem, NetDiag.Verdict(Ps(2, 0), new[] { none, none }, false));
        Assert.Equal(NetVerdict.Inconclusive, NetDiag.Verdict(null, new[] { none, none }, null));
        Assert.Equal(NetVerdict.Inconclusive, NetDiag.Verdict(null, Array.Empty<PingStats>(), null));
        foreach (var v in Enum.GetValues<NetVerdict>()) Assert.False(string.IsNullOrWhiteSpace(NetDiag.VerdictText(v)));
        Assert.Contains("1.1.1.1", NetDiag.Describe(new PingStats("1.1.1.1", 10, 10, 12.5, 2, 0)));
    }

    [Fact]
    public void NetDiag_RouterThatIgnoresPing_IsNotBlamed()
    {
        var mute = new PingStats("gw", 10, 0, null, null, 100);
        var good = new[] { Ps(15, 0, 2), Ps(18, 0, 3) };
        Assert.Equal(NetVerdict.RouterIgnoresPing, NetDiag.Verdict(mute, good, true));
        Assert.Equal(NetVerdict.DnsProblem, NetDiag.Verdict(mute, good, false));
        Assert.Equal(NetVerdict.LocalProblem, NetDiag.Verdict(mute, new[] { Ps(200, 20, 80), Ps(200, 20, 80) }, true)); // tudo ruim junto
        Assert.Equal(NetVerdict.NoConnection, NetDiag.Verdict(mute, new[] { mute, mute }, false));
        Assert.Equal(NetVerdict.LocalProblem, NetDiag.Verdict(Ps(5, 0, 45), good, true)); // roteador responde com jitter alto
        Assert.Equal(NetVerdict.AllGood, NetDiag.Verdict(null, good, true));
        Assert.DoesNotContain("Wi-Fi", NetDiag.VerdictText(NetVerdict.RouterIgnoresPing));
    }

    [Fact]
    public void NetDiag_PickRoute_UsesRoutedAdapter_NotTheVpnListedFirst()
    {
        static IPAddress Ip(string s) => IPAddress.Parse(s);
        var vpn = new NetDiag.AdapterRoute("Radmin VPN", true, false, false, new[] { Ip("26.251.154.230") }, Ip("26.0.0.1"), new[] { Ip("fec0:0:0:ffff::1"), Ip("fec0:0:0:ffff::2") });
        var eth = new NetDiag.AdapterRoute("Ethernet", true, false, false, new[] { Ip("192.168.1.2") }, Ip("192.168.1.1"), new[] { Ip("fec0:0:0:ffff::1"), Ip("192.168.1.1"), Ip("8.8.8.8") });
        var list = new[] { vpn, eth };
        var r = NetDiag.PickRoute(list, Ip("192.168.1.2"));
        Assert.Equal("Ethernet", r.AdapterName);
        Assert.Equal(Ip("192.168.1.1"), r.Gateway);
        Assert.Equal(new[] { Ip("192.168.1.1"), Ip("8.8.8.8") }, r.Dns); // placeholder descartado
        // sem IP de rota conhecido: cai no primeiro com gateway
        Assert.Equal("Radmin VPN", NetDiag.PickRoute(list, null).AdapterName);
        // IP de rota desconhecido do conjunto: idem
        Assert.Equal("Radmin VPN", NetDiag.PickRoute(list, Ip("10.9.9.9")).AdapterName);
        // loopback, túnel e desconectados são ignorados
        var tunnel = new NetDiag.AdapterRoute("T", true, false, true, new[] { Ip("10.0.0.2") }, Ip("10.0.0.1"), Array.Empty<IPAddress>());
        var down = new NetDiag.AdapterRoute("D", false, false, false, new[] { Ip("172.16.0.2") }, Ip("172.16.0.1"), Array.Empty<IPAddress>());
        var loop = new NetDiag.AdapterRoute("L", true, true, false, new[] { Ip("127.0.0.1") }, null, Array.Empty<IPAddress>());
        var none = NetDiag.PickRoute(new[] { tunnel, down, loop }, Ip("10.0.0.2"));
        Assert.Null(none.AdapterName);
        Assert.Null(none.Gateway);
        Assert.Empty(none.Dns);
        Assert.True(NetDiag.IsPlaceholderDns(Ip("fec0:0:0:ffff::3")));
        Assert.False(NetDiag.IsPlaceholderDns(Ip("2606:4700:4700::1111")));
        Assert.False(NetDiag.IsPlaceholderDns(Ip("1.1.1.1")));
        _ = NetDiag.ReadRoute(); // não pode lançar
    }

    [Fact]
    public void Proc_ResolveOem_UsesGivenCodePageAndFallsBack()
    {
        Assert.Equal(850, WinDiag.Core.Util.Proc.ResolveOem(850).CodePage);
        Assert.Equal(437, WinDiag.Core.Util.Proc.ResolveOem(437).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, WinDiag.Core.Util.Proc.ResolveOem(0).CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, WinDiag.Core.Util.Proc.ResolveOem(99999).CodePage);
        Assert.True(WinDiag.Core.Util.Proc.OemEncoding.CodePage > 0);
        // a página não depende da cultura do app
        var before = WinDiag.Core.Util.Proc.OemEncoding.CodePage;
        try { Loc.Set("en"); Assert.Equal(before, WinDiag.Core.Util.Proc.OemEncoding.CodePage); }
        finally { Loc.Set("pt-BR"); }
        Assert.Equal("operação", WinDiag.Core.Util.Proc.ResolveOem(850).GetString(WinDiag.Core.Util.Proc.ResolveOem(850).GetBytes("operação")));
    }

    [Fact]
    public void GetOptions_ExplainsMissingSizes()
    {
        var runner = new ToolRunner(new SnapshotStore(Tmp("opt")), dryRun: true);
        var opts = runner.GetOptions(ToolCatalog.Get("DISK_CLEANUP")!);
        Assert.Equal(DiskCleanup.AllOptionIds.Length, opts.Count);
        foreach (var o in opts)
        {
            if (o.Bytes is null) Assert.False(string.IsNullOrWhiteSpace(o.Note), o.Id);
            else Assert.Null(o.Note);
        }
        if (!ToolRunner_IsAdmin())
            foreach (var id in new[] { "win_temp", "wu_cache", "do_cache" })
            {
                var o = opts.Single(x => x.Id == id);
                Assert.Null(o.Bytes);
                Assert.Contains("administrador", o.Note);
            }
        Assert.Empty(runner.GetOptions(ToolCatalog.Get("NET_TEST")!));
        Assert.Null(new ToolOption("a", "b", 1, true).Note);
    }

    private static bool ToolRunner_IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    [Fact]
    public void NetDiag_StaticIpDetection()
    {
        var list = new[]
        {
            new AdapterInfo("Ethernet", true, false, false, false, true, "Intel(R) Ethernet", new[] { "192.168.1.5" }),   // IP fixo
            new AdapterInfo("Wi-Fi", true, false, false, true, true),        // DHCP
            new AdapterInfo("Loopback", true, true, false, false, true),
            new AdapterInfo("VPN", true, false, true, false, true),
            new AdapterInfo("Desligado", false, false, false, false, true, "Realtek PCIe", new[] { "10.0.0.9" }), // desconectado, mas com IP fixo
            new AdapterInfo("SemIP", true, false, false, false, false),
            new AdapterInfo("vEthernet (Default Switch)", true, false, false, false, true, "Hyper-V Virtual Ethernet Adapter"),
            new AdapterInfo("Radmin VPN", true, false, false, false, true, "Famatech Radmin VPN Ethernet Adapter"),
            new AdapterInfo("Ethernet 2", true, false, false, false, true, "TAP-Windows Adapter V9"),
            new AdapterInfo("VMware", true, false, false, false, true, "VMware Virtual Ethernet Adapter for VMnet8"),
        };
        Assert.Equal(new[] { "Ethernet", "Desligado" }, NetDiag.StaticIpAdapters(list));
        Assert.Equal(new[] { "Ethernet (192.168.1.5)", "Desligado (10.0.0.9)" }, NetDiag.StaticIpDetails(list));
        Assert.Empty(NetDiag.StaticIpAdapters(list.Skip(1).Take(2)));
        Assert.True(NetDiag.IsVirtual(list[6]) && NetDiag.IsVirtual(list[7]) && NetDiag.IsVirtual(list[8]) && NetDiag.IsVirtual(list[9]));
        Assert.False(NetDiag.IsVirtual(list[0]));
        Assert.NotNull(NetDiag.ReadAdapters());
    }

    // ---------------------------------------------------------------- limpeza de disco

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void DiskCleanup_DeletesInsideTarget_NeverFollowsJunctions_SkipsLockedFiles()
    {
        var root = Tmp("clean");
        var outside = Tmp("outside");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(root, "a.tmp"), new string('x', 1000));
            File.WriteAllText(Path.Combine(root, "sub", "b.tmp"), new string('x', 500));
            File.WriteAllText(Path.Combine(root, "sub", "deep", "c.tmp"), "c");
            var ro = Path.Combine(root, "ro.tmp"); File.WriteAllText(ro, "r"); File.SetAttributes(ro, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(outside, "precioso.txt"), "nao apagar");
            Junction(Path.Combine(root, "link"), outside);
            var locked = Path.Combine(root, "locked.tmp");
            var target = new CleanTarget(root, "*", true);

            Assert.Equal(1000 + 500 + 1 + 1, DiskCleanup.Measure(target)); // não soma o conteúdo da junction

            using (var fs = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(1);
                var res = DiskCleanup.Clean(target);
                Assert.Equal(4, res.Deleted);
                Assert.Equal(1000 + 500 + 1 + 1, res.FreedBytes);
                Assert.True(res.Skipped >= 2); // arquivo em uso + junction
            }
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(Path.Combine(outside, "precioso.txt")));
            Assert.True(Directory.Exists(root));
            Assert.False(File.Exists(Path.Combine(root, "a.tmp")));
            Assert.False(Directory.Exists(Path.Combine(root, "sub")));
            // a própria pasta alvo sendo uma junction: nada é tocado
            var via = Tmp("via");
            Junction(via, outside);
            try
            {
                var r2 = DiskCleanup.Clean(new CleanTarget(via, "*", true));
                Assert.Equal(0, r2.Deleted);
                Assert.True(File.Exists(Path.Combine(outside, "precioso.txt")));
            }
            finally { try { Directory.Delete(via); } catch (IOException) { } }
        }
        finally
        {
            try { Directory.Delete(Path.Combine(root, "link")); } catch (Exception) { }
            try { Directory.Delete(root, true); } catch (Exception) { }
            try { Directory.Delete(outside, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void DiskCleanup_PatternOnlyMatchesAndDoesNotRecurse()
    {
        var root = Tmp("thumb");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "thumbcache_32.db"), "1234");
            File.WriteAllText(Path.Combine(root, "iconcache.db"), "keep");
            File.WriteAllText(Path.Combine(root, "sub", "thumbcache_x.db"), "keep");
            var res = DiskCleanup.Clean(new CleanTarget(root, "thumbcache_*.db", false));
            Assert.Equal(1, res.Deleted);
            Assert.Equal(4, res.FreedBytes);
            Assert.True(File.Exists(Path.Combine(root, "iconcache.db")));
            Assert.True(File.Exists(Path.Combine(root, "sub", "thumbcache_x.db")));
            Assert.Equal(0, DiskCleanup.Measure(new CleanTarget(Path.Combine(root, "nao_existe"), "*", true)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DiskCleanup_OptionsAreDefinedAndRecycleIsOffByDefault()
    {
        foreach (var id in DiskCleanup.AllOptionIds)
        {
            Assert.False(string.IsNullOrWhiteSpace(DiskCleanup.Label(id)));
            if (id != DiskCleanup.Recycle) Assert.NotEmpty(DiskCleanup.Targets(id));
        }
        Assert.False(DiskCleanup.DefaultOn(DiskCleanup.Recycle));
        Assert.True(DiskCleanup.DefaultOn(DiskCleanup.UserTemp));
        Assert.Contains(DiskCleanup.ShaderTargets(), t => t.Dir.EndsWith("D3DSCache", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1 KB", DiskCleanup.FormatBytes(1024));
    }

    // ---------------------------------------------------------------- Proc.RunStreaming

    [Fact]
    public void RunStreaming_ReadsLinesAndNeverThrows()
    {
        var lines = new List<string>();
        var r = WinDiag.Core.Util.Proc.RunStreaming(WinDiag.Core.Util.Proc.Sys32("cmd.exe"), "/c echo um& echo dois", lines.Add, CancellationToken.None, 10000);
        Assert.True(r.Ok);
        Assert.Equal(new[] { "um", "dois" }, lines);
        var bad = WinDiag.Core.Util.Proc.RunStreaming(@"C:\nao\existe.exe", "", _ => { }, CancellationToken.None, 1000);
        Assert.False(bad.Ok);
        using var cts = new CancellationTokenSource(300);
        var sw = Stopwatch.StartNew();
        var c = WinDiag.Core.Util.Proc.RunStreaming(WinDiag.Core.Util.Proc.Sys32("ping.exe"), "-n 30 127.0.0.1", _ => { }, cts.Token, 60000);
        Assert.False(c.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    // ---------------------------------------------------------------- atualização

    [Fact]
    public void UpdateCheck_IsNewer()
    {
        Assert.True(UpdateCheck.IsNewer("1.2.0", "1.3.0"));
        Assert.True(UpdateCheck.IsNewer("1.2.0", "v1.2.1"));
        Assert.True(UpdateCheck.IsNewer("1.9.0", "1.10.0"));
        Assert.False(UpdateCheck.IsNewer("1.3.0", "1.3.0"));
        Assert.False(UpdateCheck.IsNewer("1.3.0", "1.2.9"));
        Assert.False(UpdateCheck.IsNewer("1.2", "1.2.0"));
        Assert.True(UpdateCheck.IsNewer("1.2.0", "1.3.0-beta"));
        Assert.False(UpdateCheck.IsNewer("1.2.0", "lixo"));
        Assert.False(UpdateCheck.IsNewer("", "1.0.0"));
    }

    [Fact]
    public void UpdateCheck_IsDue_AllFrequenciesAndEdges()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Manual, null, now));
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Manual, now.AddYears(-1), now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.OnStartup, now, now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.OnStartup, null, now));
        foreach (var f in new[] { UpdateFrequency.Daily, UpdateFrequency.Weekly, UpdateFrequency.Monthly })
            Assert.True(UpdateCheck.IsDue(f, null, now));
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Daily, now.AddHours(-23).AddMinutes(-59), now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.Daily, now.AddDays(-1), now));
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Weekly, now.AddDays(-6), now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.Weekly, now.AddDays(-7), now));
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Monthly, now.AddDays(-29), now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.Monthly, now.AddDays(-30), now));
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.Daily, now.AddHours(1), now)); // data no futuro: não confia, verifica
        Assert.True(UpdateCheck.IsDue(UpdateFrequency.Monthly, now.AddYears(1), now));
        Assert.False(UpdateCheck.IsDue(UpdateFrequency.Manual, now.AddHours(1), now));
    }

    private const string ReleaseJson = """
    {
      "tag_name": "v1.3.0", "html_url": "https://github.com/dougllascda/WinDiag/releases/tag/v1.3.0",
      "draft": false, "prerelease": false, "body": "Novidades da 1.3",
      "assets": [
        { "name": "WinDiag-Setup-1.3.0.exe", "browser_download_url": "https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/WinDiag-Setup-1.3.0.exe", "size": 123456 },
        { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/SHA256SUMS.txt", "size": 300 },
        { "name": "sem-url" }
      ]
    }
    """;

    [Fact]
    public void UpdateCheck_ParsesReleaseJson()
    {
        var info = UpdateCheck.Parse(ReleaseJson, "1.2.0")!;
        Assert.Equal("1.3.0", info.Version);
        Assert.True(info.IsNewer);
        Assert.Equal("Novidades da 1.3", info.Notes);
        Assert.EndsWith("v1.3.0", info.Url);
        Assert.Equal(2, info.Assets.Count);
        Assert.Equal(new UpdateAsset("WinDiag-Setup-1.3.0.exe", "https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/WinDiag-Setup-1.3.0.exe", 123456), info.Assets[0]);
        Assert.False(UpdateCheck.Parse(ReleaseJson, "1.3.0")!.IsNewer);
        Assert.Null(UpdateCheck.Parse(ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true"), "1.2.0"));
        Assert.Null(UpdateCheck.Parse(ReleaseJson.Replace("\"draft\": false", "\"draft\": true"), "1.2.0"));
        Assert.Null(UpdateCheck.Parse("{ \"html_url\": \"x\" }", "1.2.0"));
        Assert.Null(UpdateCheck.Parse("não é json", "1.2.0"));
        Assert.Null(UpdateCheck.Parse("[]", "1.2.0"));
        var minimal = UpdateCheck.Parse("{ \"tag_name\": \"1.4.0\" }", "1.2.0")!;
        Assert.Empty(minimal.Assets);
        Assert.Equal("", minimal.Notes);
    }

    [Fact]
    public void UpdateInstaller_ParseSums()
    {
        var h1 = new string('a', 64); var h2 = new string('B', 64);
        var d = UpdateInstaller.ParseSums($"{h1}  WinDiag-Setup-1.3.0.exe\r\n{h2} *WinDiag-Portable-1.3.0.exe\n\nlixo sem hash\n{new string('c', 63)}  curto.exe\n{h1}  ./dir/outro.exe\n");
        Assert.Equal(3, d.Count);
        Assert.Equal(h1, d["WinDiag-Setup-1.3.0.exe"]);
        Assert.Equal(h2.ToLowerInvariant(), d["winDiag-portable-1.3.0.exe"]);
        Assert.Equal(h1, d["outro.exe"]);
        Assert.Empty(UpdateInstaller.ParseSums(null));
        Assert.Empty(UpdateInstaller.ParseSums(""));
    }

    [Theory]
    [InlineData("https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/WinDiag-Setup-1.3.0.exe", true)]
    [InlineData("http://github.com/dougllascda/WinDiag/releases/download/v1.3.0/a.exe", false)]
    [InlineData("https://evil.com/dougllascda/WinDiag/releases/download/v1.3.0/a.exe", false)]
    [InlineData("https://github.com/outro/WinDiag/releases/download/v1.3.0/a.exe", false)]
    [InlineData("https://github.com/dougllascda/OutroRepo/releases/download/v1.3.0/a.exe", false)]
    [InlineData("https://github.com/dougllascda/WinDiag/releases/download/../../../evil/a.exe", false)]
    [InlineData("https://github.com/dougllascda/WinDiag/releases/download/%2e%2e/evil/a.exe", false)]
    [InlineData("https://github.com/dougllascda/WinDiag/releases/download/v1\\a.exe", false)]
    [InlineData("https://github.com.evil.com/dougllascda/WinDiag/releases/download/v1/a.exe", false)]
    [InlineData("https://github.com/dougllascda/WinDiag/releases/download/", false)]
    [InlineData("HTTPS://github.com/dougllascda/WinDiag/releases/download/v1/a.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UpdateInstaller_ValidatesDownloadUrl(string? url, bool ok) => Assert.Equal(ok, UpdateInstaller.IsAllowedDownloadUrl(url));

    [Fact]
    public void UpdateInstaller_AssetNames_AndDetectKind()
    {
        Assert.Equal("WinDiag-Setup-1.3.0.exe", UpdateInstaller.AssetNameFor(InstallKind.Installer, "1.3.0"));
        Assert.Equal("WinDiag-Portable-1.3.0.exe", UpdateInstaller.AssetNameFor(InstallKind.Portable, "1.3.0"));
        Assert.Equal("WinDiag-1.3.0-fx.exe", UpdateInstaller.AssetNameFor(InstallKind.FrameworkDependent, "1.3.0"));

        var dir = Tmp("kind");
        Directory.CreateDirectory(dir);
        try
        {
            const long mb = 1024 * 1024;
            Assert.Equal(InstallKind.Portable, UpdateInstaller.DetectKind(dir, 31 * mb));
            Assert.Equal(InstallKind.FrameworkDependent, UpdateInstaller.DetectKind(dir, 30 * mb));
            Assert.Equal(InstallKind.FrameworkDependent, UpdateInstaller.DetectKind(dir, 2 * mb));
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "x");
            Assert.Equal(InstallKind.Installer, UpdateInstaller.DetectKind(dir, 100 * mb));
            Assert.Equal(InstallKind.Installer, UpdateInstaller.DetectKind(dir, 1));
        }
        finally { Directory.Delete(dir, true); }
        _ = UpdateInstaller.DetectKind(); // no ambiente de teste não pode lançar
    }

    [Fact]
    public void UpdateInstaller_SwapExecutable_RenamesAndRollsBack()
    {
        var dir = Tmp("swap");
        Directory.CreateDirectory(dir);
        try
        {
            var cur = Path.Combine(dir, "WinDiag.exe"); var nw = Path.Combine(dir, "novo.exe");
            File.WriteAllText(cur, "velho"); File.WriteAllText(nw, "novo");
            File.WriteAllText(cur + ".old", "mais antigo");
            var ok = UpdateInstaller.SwapExecutable(cur, nw, launch: false);
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal("novo", File.ReadAllText(cur));
            Assert.Equal("velho", File.ReadAllText(cur + ".old"));
            Assert.False(File.Exists(nw));

            // falha ao colocar o novo (arquivo inexistente): o executável antigo volta ao lugar
            var cur2 = Path.Combine(dir, "Outro.exe"); File.WriteAllText(cur2, "velho2");
            var bad = UpdateInstaller.SwapExecutable(cur2, Path.Combine(dir, "nao_existe.exe"), launch: false);
            Assert.False(bad.Ok);
            Assert.False(string.IsNullOrWhiteSpace(bad.Error));
            Assert.Equal("velho2", File.ReadAllText(cur2));
            Assert.False(File.Exists(cur2 + ".old"));

            Assert.False(UpdateInstaller.Apply(new DownloadedUpdate(Path.Combine(dir, "nada.exe"), "1.3.0", InstallKind.Installer)).Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UpdateInstaller_CleanupLeftovers_RemovesOldAndInstalledVersions()
    {
        var dir = Tmp("left"); var root = Path.Combine(dir, "update");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var v in new[] { "1.0.0", "1.2.0", "9.9.9", "lixo" }) { Directory.CreateDirectory(Path.Combine(root, v)); File.WriteAllText(Path.Combine(root, v, "f.exe"), "x"); }
            var exe = Path.Combine(dir, "WinDiag.exe"); File.WriteAllText(exe + ".old", "x");
            UpdateInstaller.CleanupLeftovers(exe, root, "1.2.0");
            Assert.False(File.Exists(exe + ".old"));
            Assert.False(Directory.Exists(Path.Combine(root, "1.0.0")));
            Assert.False(Directory.Exists(Path.Combine(root, "1.2.0")));
            Assert.True(Directory.Exists(Path.Combine(root, "lixo"))); // só pastas com nome de versão são apagadas
            Assert.True(Directory.Exists(Path.Combine(root, "9.9.9")));
            UpdateInstaller.CleanupLeftovers("", Path.Combine(dir, "nao_existe"), "1.2.0"); // não lança
            UpdateInstaller.CleanupLeftovers(); // não lança
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Manipulador HTTP de mentira: responde pelo endereço, sem rede.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Map { get; } = new();
        public List<string> Requested { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(Map.TryGetValue(url, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Rel = "https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/";

    private static (UpdateInfo Info, FakeHandler H, byte[] Data) Scenario(string sumsFor, long? size = null, string asset = "WinDiag-Portable-1.3.0.exe")
    {
        var data = Encoding.UTF8.GetBytes("conteudo do executavel de teste");
        var h = new FakeHandler();
        h.Map[Rel + asset] = data;
        var hash = sumsFor == "ok" ? Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : new string('0', 64);
        h.Map[Rel + "SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{hash}  {asset}\n");
        var info = new UpdateInfo("1.3.0", "https://github.com/dougllascda/WinDiag/releases/tag/v1.3.0", true, "",
            new[] { new UpdateAsset(asset, Rel + asset, size ?? data.Length), new UpdateAsset("SHA256SUMS.txt", Rel + "SHA256SUMS.txt", 0) });
        return (info, h, data);
    }

    [Fact]
    public async Task Download_VerifiesSha256_AndRejectsEverythingElse()
    {
        var root = Tmp("dl2");
        try
        {
            var (info, h, data) = Scenario("ok");
            Directory.CreateDirectory(Path.Combine(root, "0.9.0"));
            var d = await UpdateInstaller.DownloadAsync(info, InstallKind.Portable, null, h, root, CancellationToken.None);
            Assert.Equal(Path.Combine(root, "1.3.0", "WinDiag-Portable-1.3.0.exe"), d.Path);
            Assert.Equal(data, File.ReadAllBytes(d.Path));
            // o handle verificado fica aberto: ninguém grava nem apaga o arquivo enquanto isso
            Assert.NotNull(d.Handle);
            Assert.ThrowsAny<IOException>(() => new FileStream(d.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.ThrowsAny<IOException>(() => File.Delete(d.Path));
            d.Dispose(); d.Dispose();
            File.Delete(d.Path); File.WriteAllBytes(d.Path, data);
            Assert.Equal(InstallKind.Portable, d.Kind);
            Assert.False(Directory.Exists(Path.Combine(root, "0.9.0")));

            // hash diferente: apaga o arquivo e lança
            var (bad, h2, _) = Scenario("bad");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(bad, InstallKind.Portable, null, h2, root, CancellationToken.None));
            Assert.Contains("SHA-256", ex.Message);
            Assert.False(File.Exists(Path.Combine(root, "1.3.0", "WinDiag-Portable-1.3.0.exe")));

            // tamanho informado diferente
            var (sz, h3, _) = Scenario("ok", size: 5);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(sz, InstallKind.Portable, null, h3, root, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "1.3.0", "WinDiag-Portable-1.3.0.exe")));

            // acima de 400 MB informado: recusa antes de baixar
            var (big, h4, _) = Scenario("ok", size: UpdateInstaller.MaxBytes + 1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(big, InstallKind.Portable, null, h4, root, CancellationToken.None));
            Assert.Empty(h4.Requested);

            // sem SHA256SUMS.txt
            var noSums = info with { Assets = info.Assets.Where(a => a.Name != "SHA256SUMS.txt").ToList() };
            var h5 = new FakeHandler();
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(noSums, InstallKind.Portable, null, h5, root, CancellationToken.None));
            Assert.Empty(h5.Requested);

            // o arquivo não consta no SUMS
            var (other, h6, _) = Scenario("ok");
            h6.Map[Rel + "SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(new string('a', 64) + "  outro.exe\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(other, InstallKind.Portable, null, h6, root, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "1.3.0", "WinDiag-Portable-1.3.0.exe")));

            // tipo sem asset correspondente
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(info, InstallKind.Installer, null, h, root, CancellationToken.None));

            // endereço fora do repositório, SUMS de outro release e versão com caminho
            var evil = info with { Assets = new[] { new UpdateAsset("WinDiag-Portable-1.3.0.exe", "https://evil.example/WinDiag-Portable-1.3.0.exe", 1), info.Assets[1] } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(evil, InstallKind.Portable, null, h, root, CancellationToken.None));
            var otherRelease = info with { Assets = new[] { info.Assets[0], new UpdateAsset("SHA256SUMS.txt", "https://github.com/dougllascda/WinDiag/releases/download/v9.9.9/SHA256SUMS.txt", 0) } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(otherRelease, InstallKind.Portable, null, h, root, CancellationToken.None));
            var traversal = info with { Version = "..\\..\\x" };
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(traversal, InstallKind.Portable, null, h, root, CancellationToken.None));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void UserPrefs_UpdateFields_DefaultsAndOldFiles()
    {
        Assert.Equal("Weekly", new UserPrefs().UpdateFrequency);
        Assert.Equal(UpdateFrequency.Weekly, UserPrefs.UpdateFrequencyOf(new UserPrefs()));
        Assert.Equal(UpdateFrequency.Weekly, UserPrefs.UpdateFrequencyOf(""));
        Assert.Equal(UpdateFrequency.Weekly, UserPrefs.UpdateFrequencyOf("xyz"));
        Assert.Equal(UpdateFrequency.Weekly, UserPrefs.UpdateFrequencyOf("42"));
        Assert.Equal(UpdateFrequency.Weekly, UserPrefs.UpdateFrequencyOf((string?)null));
        Assert.Equal(UpdateFrequency.Manual, UserPrefs.UpdateFrequencyOf("manual"));
        Assert.Equal(UpdateFrequency.OnStartup, UserPrefs.UpdateFrequencyOf(new UserPrefs { UpdateFrequency = "OnStartup" }));
        Assert.Equal(UpdateFrequency.Monthly, UserPrefs.UpdateFrequencyOf(" Monthly "));

        var dir = Tmp("prefs"); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"Theme\": \"Dark\" }");
            var old = UserPrefs.Load(path);
            Assert.Equal("Weekly", old.UpdateFrequency);
            Assert.Null(old.LastUpdateCheckUtc);
            Assert.Null(old.SkippedVersion);
            old.UpdateFrequency = "Daily"; old.LastUpdateCheckUtc = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc); old.SkippedVersion = "1.3.0";
            Assert.True(old.Save(path));
            var back = UserPrefs.Load(path);
            Assert.Equal("Daily", back.UpdateFrequency);
            Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), back.LastUpdateCheckUtc!.Value.ToUniversalTime());
            Assert.Equal("1.3.0", back.SkippedVersion);
            File.WriteAllText(path, "{ \"UpdateFrequency\": \"\" }");
            Assert.Equal("Weekly", UserPrefs.Load(path).UpdateFrequency);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- ajustes novos

    [Fact]
    public void NewFixes_AreReversibleRegistryOrPlanItems()
    {
        foreach (var id in new[] { "MOUSE_ACCEL_OFF", "STICKY_KEYS_OFF", "PRINTER_DEFAULT_LOCK", "POWER_PLAN_GAMING" })
        {
            var f = FixCatalog.Get(id)!;
            Assert.True(f.Reversible);
            Assert.False(string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.ShortName) || string.IsNullOrWhiteSpace(f.Explain), id);
        }
        Assert.Equal(Scope.Gaming, FixCatalog.Get("MOUSE_ACCEL_OFF")!.Scope);
        Assert.Equal(Scope.Gaming, FixCatalog.Get("STICKY_KEYS_OFF")!.Scope);
        Assert.Equal(Scope.Gaming, FixCatalog.Get("POWER_PLAN_GAMING")!.Scope);
        Assert.Equal(Scope.Work, FixCatalog.Get("PRINTER_DEFAULT_LOCK")!.Scope);
        var mouse = FixCatalog.Get("MOUSE_ACCEL_OFF")!;
        Assert.Equal(new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }, mouse.Reg.Select(r => r.Name));
        Assert.All(mouse.Reg, r => { Assert.Equal("0", r.Value); Assert.Equal(Microsoft.Win32.RegistryValueKind.String, r.Kind); });
        Assert.True(mouse.MouseApply);
        var sticky = FixCatalog.Get("STICKY_KEYS_OFF")!;
        Assert.Equal(new object[] { "506", "122", "58" }, sticky.Reg.Select(r => r.Value));
        var lockFix = FixCatalog.Get("PRINTER_DEFAULT_LOCK")!;
        Assert.Equal("LegacyDefaultPrinterMode", lockFix.Reg.Single().Name);
        Assert.Equal(1, lockFix.Reg.Single().Value);
        Assert.Equal(Microsoft.Win32.RegistryValueKind.DWord, lockFix.Reg.Single().Kind);
        // aplicabilidade e estado só leem; não podem lançar
        var facts = new SystemFacts { Printers = { new PrinterInfo { Name = "A" }, new PrinterInfo { Name = "B" } } };
        foreach (var f in new[] { mouse, sticky, lockFix }) { _ = f.Applicable(facts); _ = f.IsActive(facts); }
        Assert.False(lockFix.Applicable(new SystemFacts { Printers = { new PrinterInfo { Name = "A" } } }));
    }

    [Fact]
    public void PowerPlanGaming_ApplicableAndActive()
    {
        var fix = FixCatalog.Get("POWER_PLAN_GAMING")!;
        var all = new[] { FixCatalog.BalancedPlanGuid, FixCatalog.HighPerformancePlanGuid, FixCatalog.PowerSaverPlanGuid };
        var desk = new SystemFacts { IsLaptop = false, ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid, PowerPlanGuids = all.ToList() };
        Assert.True(fix.Applicable(desk));
        Assert.False(fix.IsActive(desk));
        desk.ActivePowerPlanGuid = FixCatalog.PowerSaverPlanGuid.ToUpperInvariant();
        Assert.True(fix.Applicable(desk));
        desk.PowerPlanGuids = new List<string> { FixCatalog.BalancedPlanGuid }; // Alto desempenho não existe
        Assert.False(fix.Applicable(desk));
        desk.PowerPlanGuids = all.ToList();
        desk.ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid;
        Assert.False(fix.Applicable(desk));
        Assert.True(fix.IsActive(desk));
        desk.ActivePowerPlanGuid = FixCatalog.UltimatePlanGuid;
        Assert.True(fix.IsActive(desk));
        Assert.False(fix.Applicable(desk));
        var lap = new SystemFacts { IsLaptop = true, ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid, PowerPlanGuids = all.ToList() };
        Assert.False(fix.Applicable(lap));
        lap.ActivePowerPlanGuid = FixCatalog.HighPerformancePlanGuid;
        Assert.False(fix.IsActive(lap));
        Assert.False(fix.Applicable(new SystemFacts { IsAtlas = true, ActivePowerPlanGuid = FixCatalog.BalancedPlanGuid, PowerPlanGuids = all.ToList() }));
        Assert.False(fix.Applicable(new SystemFacts()));

        var dir = Tmp("pg");
        var r = new FixEngine(new SnapshotStore(dir), dryRun: true).Enable(fix, new SystemFacts { IsLaptop = false });
        Assert.True(r.Ok);
        Assert.Contains(FixCatalog.HighPerformancePlanGuid, string.Join(" ", r.Messages));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MouseFix_DryRun_WritesNothingAndMentionsSessionApply()
    {
        var dir = Tmp("mouse");
        var eng = new FixEngine(new SnapshotStore(dir), dryRun: true);
        var r = eng.Enable(FixCatalog.Get("MOUSE_ACCEL_OFF")!, new SystemFacts());
        Assert.True(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("MouseSpeed"));
        Assert.Contains(r.Messages, m => m.Contains("mouse", StringComparison.OrdinalIgnoreCase) && m.Contains("sessão"));
        Assert.False(Directory.Exists(dir));
        var state = new FixState { Reg = { new WinDiag.Core.Util.RegState { Path = WinDiag.Core.Util.MouseApi.MousePath, Name = "MouseSpeed", Exists = true, Value = "1", Kind = "String" } } };
        Assert.Contains(eng.Restore(state), m => m.Contains("mouse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemovedActionItems_AreNoLongerInTheCatalog()
    {
        Assert.DoesNotContain(FixCatalog.All, f => f.Id is "TEMP" or "SPOOLER_RESTART" or "PRINT_QUEUE_CLEAR");
        Assert.NotNull(ToolCatalog.Get("SPOOLER_RESTART"));
    }

    // ---------------------------------------------------------------- nota parcial e ToolId

    [Fact]
    public void GameScore_PartialCap_AppearsAsPartialReadingsPart()
    {
        var none = GameScore.Compute(Array.Empty<Finding>(), UsageProfile.Work, partialReadings: true);
        Assert.Equal(95, none.Score);
        var part = Assert.Single(none.Parts);
        Assert.Equal("Leituras parciais", part.Area);
        Assert.Equal(5, part.Lost);
        Assert.Equal(100 - none.Score, none.Parts.Sum(p => p.Lost));

        var full = GameScore.Compute(Array.Empty<Finding>(), UsageProfile.Work, partialReadings: false);
        Assert.Equal(100, full.Score);
        Assert.Empty(full.Parts);

        var f = new Finding(Severity.Aviso, "T1", "e", "x", "a1", "", 50) { Section = "trabalho", Id = "trabalho.x", Scope = Scope.Work };
        var withLoss = GameScore.Compute(new[] { f }, UsageProfile.Work, partialReadings: true);
        Assert.Equal(94, withLoss.Score); // 100 - 6 = 94, abaixo do teto: sem entrada extra
        Assert.DoesNotContain(withLoss.Parts, p => p.Area == "Leituras parciais");
        Assert.Equal(100 - withLoss.Score, withLoss.Parts.Sum(p => p.Lost));

        var f2 = new Finding(Severity.Aviso, "T2", "e", "x", "a2", "", 20) { Section = "trabalho", Id = "trabalho.y", Scope = Scope.Work };
        var small = GameScore.Compute(new[] { f2 }, UsageProfile.Work, partialReadings: true); // 100 - 2 = 98 -> teto 95
        Assert.Equal(95, small.Score);
        Assert.Equal(2, small.Parts.Count);
        Assert.Equal(100 - small.Score, small.Parts.Sum(p => p.Lost));
        Assert.Contains(small.Parts, p => p.Area == "Leituras parciais" && p.Lost == 3);
        Assert.Equal(small.Parts.OrderByDescending(p => p.Lost).Select(p => p.Area), small.Parts.Select(p => p.Area).Where(_ => true));

        // jogos nunca recebe o teto
        Assert.Equal(100, GameScore.Compute(Array.Empty<Finding>(), UsageProfile.Gaming, partialReadings: true).Score);
        try
        {
            Loc.Set("en");
            Assert.Equal("Partial readings", GameScore.Compute(Array.Empty<Finding>(), UsageProfile.Work, true).Parts.Single().Area);
        }
        finally { Loc.Set("pt-BR"); }
    }

    [Fact]
    public void Findings_CarryToolId_AndPlanStepKeepsIt()
    {
        var now = new DateTime(2026, 11, 8);
        var f = new SystemFacts
        {
            SpoolerRunning = true, HasBattery = true, IsLaptop = true, BatteryWearPct = 65, BatteryDesignMWh = 50000, BatteryFullMWh = 17500,
            RebootPending = true, LastUpdateInstalled = now.AddDays(-90),
            Printers = { new PrinterInfo { Name = "P", JobCount = 1, StuckJobs = 1 } },
        };
        var res = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(res, f, new WorkChecks(Now: now));
        string? Tool(string id) => res.Findings.Single(x => x.Id == id).ToolId;
        Assert.Equal("PRINT_UNSTICK", Tool("trabalho.print_stuck"));
        Assert.Equal("BATTERY_REPORT", Tool("trabalho.battery_wear"));
        Assert.Equal("OPEN_WINUPDATE", Tool("trabalho.reboot_pending"));
        Assert.Equal("OPEN_WINUPDATE", Tool("trabalho.update_old"));
        Assert.All(res.Findings.Where(x => x.ToolId is not null), x => Assert.NotNull(ToolCatalog.Get(x.ToolId!)));

        var down = new SectionResult { Key = "trabalho", Title = "t" };
        WorkCollector.AddFindings(down, new SystemFacts { SpoolerRunning = false });
        Assert.Equal("SPOOLER_RESTART", down.Findings.Single().ToolId);

        var plan = ActionPlan.Build(res.Findings, UsageProfile.Work);
        Assert.Equal("PRINT_UNSTICK", plan.Single(s => s.FindingId == "trabalho.print_stuck").ToolId);
        Assert.Equal("BATTERY_REPORT", plan.Single(s => s.FindingId == "trabalho.battery_wear").ToolId);

        var plain = new SectionResult { Key = "cpu", Title = "t" };
        plain.Find(Severity.Aviso, "Sem ferramenta", "e", "x", "faça algo", impact: 10, id: "cpu.x");
        Assert.Null(plain.Findings[0].ToolId);
        plain.Find(Severity.Aviso, "Com ferramenta", "e", "x", "faça outra coisa", impact: 10, id: "cpu.y", toolId: "DISK_CLEANUP");
        Assert.Equal("DISK_CLEANUP", plain.Findings[1].ToolId);
        Assert.Null(ActionPlan.Build(plain.Findings.Take(1)).Single().ToolId);
        Assert.Equal("DISK_CLEANUP", ActionPlan.Build(plain.Findings.Skip(1)).Single().ToolId);
    }

    [Fact]
    public void Glossary_HasNewEntriesInBothLanguages()
    {
        foreach (var key in new[] { "cache_dns", "pilha_rede", "sfc", "dism", "cache_shaders", "ponto_restauracao", "jitter" })
        {
            var e = WinDiag.Core.Knowledge.Glossary.All.Single(x => x.Key == key);
            Assert.False(string.IsNullOrWhiteSpace(e.TitlePt) || string.IsNullOrWhiteSpace(e.TextPt));
            Assert.NotEqual(e.TextPt, e.TextEn);
            Assert.NotEqual(e.TitlePt, e.TextEn);
        }
    }
}
