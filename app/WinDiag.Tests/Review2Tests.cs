#pragma warning disable CA1861 // arrays constantes em asserts de teste
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using WinDiag.Core;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Tools;
using WinDiag.Core.Update;
using WinDiag.Core.Util;

namespace WinDiag.Tests;

/// <summary>Testes da segunda revisão do Core: validação de alvos da limpeza, guardas de links, atualização segura, conta elevada diferente e avisos. Nenhum acessa a rede nem altera o sistema real.</summary>
[Collection("Loc")]
public class Review2Tests
{
    private static string Tmp(string tag) => Path.Combine(Path.GetTempPath(), "wdr2_" + tag + "_" + Guid.NewGuid().ToString("N"));

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    // ---------------------------------------------------------------- A3: alvos perigosos

    [Fact]
    public void DiskCleanup_RejectsDangerousTargets()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dangerous = new List<string?>
        {
            null, "", "   ", "relativo\\pasta", @"C:\", @"C:", Path.GetPathRoot(Environment.SystemDirectory),
            profile, Path.GetDirectoryName(profile.TrimEnd('\\')),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var d in dangerous)
        {
            Assert.False(DiskCleanup.IsSafeTarget(d), "deveria recusar: " + d);
            var t = new CleanTarget(d ?? "", "*", true);
            var r = DiskCleanup.Clean(t);
            Assert.Equal(0, r.Deleted);
            Assert.Equal(1, r.Skipped);
            Assert.Null(DiskCleanup.Measure(t));
        }
        Assert.True(DiskCleanup.IsSafeTarget(Path.Combine(Path.GetTempPath(), "wdr2_ok")));
        Assert.True(DiskCleanup.IsSameOrAncestor(@"C:\Users", @"C:\Users\x\AppData"));
        Assert.False(DiskCleanup.IsSameOrAncestor(@"C:\Users", @"C:\UsersX\a"));
        // nenhum alvo oficial devolvido é perigoso
        foreach (var id in DiskCleanup.AllOptionIds)
            Assert.All(DiskCleanup.Targets(id), t => Assert.True(DiskCleanup.IsSafeTarget(t.Dir)));
        Assert.All(DiskCleanup.ShaderTargets(), t => Assert.True(DiskCleanup.IsSafeTarget(t.Dir)));
    }

    // ---------------------------------------------------------------- M5: links em componentes

    [Fact]
    public void DiskCleanup_RejectsTargetBehindJunctionComponent()
    {
        var root = Tmp("comp"); var outside = Tmp("compout");
        try
        {
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(outside, "child"));
            File.WriteAllText(Path.Combine(outside, "child", "x.tmp"), "nao apagar");
            Junction(Path.Combine(root, "link"), outside);
            var target = new CleanTarget(Path.Combine(root, "link", "child"), "*", true);
            var r = DiskCleanup.Clean(target);
            Assert.Equal(0, r.Deleted);
            Assert.True(File.Exists(Path.Combine(outside, "child", "x.tmp")));
            Assert.Equal(0, DiskCleanup.Measure(target));
            Assert.True(UpdateInstaller.HasReparseInChain(Path.Combine(root, "link", "child")));
            Assert.False(UpdateInstaller.HasReparseInChain(root));
            // DeleteTreeNoFollow não segue o link
            Directory.CreateDirectory(Path.Combine(root, "tree"));
            Junction(Path.Combine(root, "tree", "lnk"), outside);
            DiskCleanup.DeleteTreeNoFollow(Path.Combine(root, "tree"));
            Assert.True(File.Exists(Path.Combine(outside, "child", "x.tmp")));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(root, "link")); } catch (Exception) { }
            try { Directory.Delete(Path.Combine(root, "tree", "lnk")); } catch (Exception) { }
            try { Directory.Delete(root, true); } catch (Exception) { }
            try { Directory.Delete(outside, true); } catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- M8: idade e .net

    [Fact]
    public void DiskCleanup_TempRule_OnlyOldFiles_AndSkipsDotNet()
    {
        var root = Tmp("age");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".net", "x")); Directory.CreateDirectory(Path.Combine(root, "sub"));
            string Make(string rel, int ageHours)
            {
                var f = Path.Combine(root, rel); File.WriteAllText(f, "12345"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-ageHours)); return f;
            }
            var oldA = Make("old.tmp", 48); var oldB = Make(Path.Combine("sub", "old2.tmp"), 30);
            var fresh = Make("fresh.tmp", 1); var net = Make(Path.Combine(".net", "x", "lib.dll"), 500);
            var t = new CleanTarget(root, "*", true, DiskCleanup.TempMinAgeHours, ".net");
            Assert.Equal(10, DiskCleanup.Measure(t));
            var r = DiskCleanup.Clean(t);
            Assert.Equal(2, r.Deleted);
            Assert.Equal(10, r.FreedBytes);
            Assert.False(File.Exists(oldA)); Assert.False(File.Exists(oldB));
            Assert.True(File.Exists(fresh)); Assert.True(File.Exists(net));
            Assert.All(DiskCleanup.Targets(DiskCleanup.WinTemp), x => Assert.Equal(DiskCleanup.TempMinAgeHours, x.MinAgeHours));
            Assert.All(DiskCleanup.Targets(DiskCleanup.UserTemp), x => Assert.Equal(".net", x.ExcludeTopDir));
            Assert.Contains("24", DiskCleanup.Label(DiskCleanup.UserTemp));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    // ---------------------------------------------------------------- M6: outra conta elevada

    [Fact]
    public void SessionUser_IsOtherUser_Rules()
    {
        Assert.True(SessionUser.IsOtherUser("S-1-5-21-1", "S-1-5-21-2"));
        Assert.False(SessionUser.IsOtherUser("S-1-5-21-1", "s-1-5-21-1"));
        Assert.False(SessionUser.IsOtherUser(null, "S-1-5-21-2"));
        Assert.False(SessionUser.IsOtherUser("S-1-5-21-1", ""));
        _ = SessionUser.ElevatedAsOtherUser; _ = SessionUser.SessionUserName; // não lançam
    }

    [Fact]
    public void ElevatedAsOtherUser_MakesPerUserFixesNotApplicable()
    {
        var store = new SnapshotStore(Tmp("ou"));
        var facts = new SystemFacts { ElevatedAsOtherUser = true };
        var st = new FixEngine(store, dryRun: true).GetStatuses(facts);
        foreach (var id in new[] { "MOUSE_ACCEL_OFF", "STICKY_KEYS_OFF", "PRINTER_DEFAULT_LOCK", "GAMEDVR", "WINDOWED_OPT", "GAMEMODE_ON", "STARTUP_APPS" })
        {
            var s = st.Single(x => x.Fix.Id == id);
            Assert.Equal(FixStatusKind.NaoAplicavel, s.Kind);
            Assert.Contains("outra conta", s.StatusText);
            Assert.False(s.CanToggle);
        }
        Assert.NotEqual(FixStatusKind.NaoAplicavel, new FixEngine(store, true).GetStatuses(new SystemFacts()).Single(x => x.Fix.Id == "GAMEDVR").Kind);
        Assert.False(new FixEngine(store, true).Enable(FixCatalog.Get("MOUSE_ACCEL_OFF")!, facts).Ok);
        Assert.True(FixCatalog.Get("MOUSE_ACCEL_OFF")!.PerUser);
        Assert.False(FixCatalog.Get("HAGS")!.PerUser);
        Assert.False(FixCatalog.Get("SYSMAIN")!.PerUser);
    }

    [Fact]
    public async Task ElevatedAsOtherUser_BlocksPerUserCleanupAndShaders()
    {
        var runner = new ToolRunner(new SnapshotStore(Tmp("ou2")), dryRun: true) { ElevatedAsOtherUser = true };
        var opts = runner.GetOptions(ToolCatalog.Get("DISK_CLEANUP")!);
        foreach (var id in new[] { "user_temp", "thumbnails", "recycle" })
        {
            var o = opts.Single(x => x.Id == id);
            Assert.Null(o.Bytes);
            Assert.Contains("outra conta", o.Note);
        }
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("SHADER_CACHE_CLEAR")!, null));
        Assert.NotNull(runner.BlockReason(ToolCatalog.Get("SHADER_CACHE_CLEAR")!, new SystemFacts { ElevatedAsOtherUser = true }));
        Assert.Empty(DiskCleanup.Targets("user_temp", includePerUser: false));
        Assert.Empty(DiskCleanup.Targets("thumbnails", includePerUser: false));
        Assert.All(DiskCleanup.Targets("wer", includePerUser: false), t => Assert.DoesNotContain("Local\\Microsoft", t.Dir.Replace("ProgramData", "")));
        var normal = new ToolRunner(new SnapshotStore(Tmp("ou3")), dryRun: true) { ElevatedAsOtherUser = false };
        Assert.Null(normal.BlockReason(ToolCatalog.Get("SHADER_CACHE_CLEAR")!, new SystemFacts()));
        Assert.True((await normal.RunAsync(ToolCatalog.Get("SHADER_CACHE_CLEAR")!, null, CancellationToken.None)).Ok);
    }

    // ---------------------------------------------------------------- M7: aviso de IP fixo

    [Fact]
    public void ConfirmWarning_OnlyForStackReset_AndNoLongerBlocks()
    {
        var runner = new ToolRunner(new SnapshotStore(Tmp("cw")), dryRun: true);
        foreach (var t in ToolCatalog.All.Where(x => x.Id != "NET_STACK_RESET"))
            Assert.Null(runner.ConfirmWarning(t, null));
        var stack = ToolCatalog.Get("NET_STACK_RESET")!;
        Assert.Null(runner.BlockReason(stack, new SystemFacts { IsAdmin = true })); // IP fixo não bloqueia mais
        var w = runner.ConfirmWarning(stack, null);
        Assert.True(w is null || w.Length > 10);
        var details = NetDiag.StaticIpDetails(new[] { new AdapterInfo("Ethernet", true, false, false, false, true, "Intel", new[] { "192.168.0.10", "192.168.0.11" }) });
        Assert.Equal("Ethernet (192.168.0.10, 192.168.0.11)", Assert.Single(details));
        Assert.Null(NetDiag.ReadAdaptersOrNull() is null ? null : (string?)null);
    }

    // ---------------------------------------------------------------- M9: ponto de restauração

    [Fact]
    public void RestorePointCheck_RequiresMatchingRecentPoint()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0);
        var list = new List<(string, DateTime?)> { ("WinDiag 13/11/2026", now.AddMinutes(-1)), ("Outro", now) };
        Assert.True(RestorePointCheck.HasRecent(list, "WinDiag 13/11/2026", now));
        Assert.False(RestorePointCheck.HasRecent(list, "WinDiag 12/11/2026", now));
        Assert.False(RestorePointCheck.HasRecent(new List<(string, DateTime?)> { ("WinDiag 13/11/2026", now.AddHours(-3)) }, "WinDiag 13/11/2026", now));
        Assert.False(RestorePointCheck.HasRecent(new List<(string, DateTime?)> { ("WinDiag 13/11/2026", null) }, "WinDiag 13/11/2026", now));
        Assert.False(RestorePointCheck.HasRecent(new List<(string, DateTime?)>(), "x", now));
        Assert.Single(ToolCatalog.Get("RESTORE_POINT")!.CommandLines);
        Assert.Contains("root\\default", ToolCatalog.Get("RESTORE_POINT")!.Commands[0]);
    }

    // ---------------------------------------------------------------- M10: teclas de aderência

    [Fact]
    public void StickyKeys_AppliesToSessionInDryRun()
    {
        var fix = FixCatalog.Get("STICKY_KEYS_OFF")!;
        Assert.True(fix.AccessibilityApply);
        var eng = new FixEngine(new SnapshotStore(Tmp("sk")), dryRun: true);
        Assert.Contains(eng.Enable(fix, new SystemFacts()).Messages, m => m.Contains("acessibilidade"));
        var state = new FixState { Reg = { new RegState { Path = AccessibilityApi.StickyPath, Name = "Flags", Exists = true, Value = "510", Kind = "String" } } };
        Assert.Contains(eng.Restore(state), m => m.Contains("acessibilidade"));
    }

    // ---------------------------------------------------------------- B13/B15

    [Fact]
    public void UpdateCheck_PrereleaseAndOfficialUrl()
    {
        Assert.True(UpdateCheck.IsNewer("1.3.0-rc1", "1.3.0"));
        Assert.False(UpdateCheck.IsNewer("1.3.0", "1.3.0-rc1"));
        Assert.False(UpdateCheck.IsNewer("1.3.0-rc1", "1.3.0-rc1"));
        Assert.True(UpdateCheck.IsNewer("1.2.0", "1.3.0-rc1")); // números maiores
        Assert.True(UpdateCheck.IsPrerelease("1.3.0-rc1"));
        Assert.False(UpdateCheck.IsPrerelease("1.3.0+abc"));
        const string json = "{ \"tag_name\": \"{TAG}\", \"html_url\": \"x\", \"assets\": [] }";
        Assert.True(UpdateCheck.Parse(json.Replace("{TAG}", "v1.3.0"), "1.2.0")!.IsNewer);
        Assert.False(UpdateCheck.Parse(json.Replace("{TAG}", "nightly"), "1.2.0")!.IsNewer);
        Assert.False(UpdateCheck.Parse(json.Replace("{TAG}", "v1.3.0-rc1"), "1.2.0")!.IsNewer);
        Assert.False(UpdateCheck.Parse(json.Replace("{TAG}", "v9"), "1.2.0")!.IsNewer);
        Assert.True(UpdateCheck.IsOfficialUrl(AppInfo.RepoUrl + "/releases/tag/v1.3.0"));
        Assert.False(UpdateCheck.IsOfficialUrl("http://github.com/dougllascda/WinDiag/releases"));
        Assert.False(UpdateCheck.IsOfficialUrl("https://github.com/dougllascda/WinDiagX/releases"));
        Assert.False(UpdateCheck.IsOfficialUrl(AppInfo.RepoUrl + "/../x"));
        Assert.False(UpdateCheck.IsOfficialUrl(null));
    }

    [Fact]
    public void Shell_IsOpenable_Rules()
    {
        Assert.True(Shell.IsOpenable("https://github.com/dougllascda/WinDiag"));
        Assert.True(Shell.IsOpenable("ms-settings:printers"));
        Assert.True(Shell.IsOpenable(Environment.ProcessPath));
        Assert.False(Shell.IsOpenable("https://x.com/a\" & calc"));
        Assert.False(Shell.IsOpenable("calc.exe"));
        Assert.False(Shell.IsOpenable(@"C:\nao\existe.html"));
        Assert.False(Shell.IsOpenable(""));
        Assert.False(Shell.IsOpenable(null));
        Assert.Throws<ArgumentException>(() => Shell.OpenUnelevated("calc.exe"));
    }

    // ---------------------------------------------------------------- A2/B16/B17: atualização

    [Fact]
    public void UpdateInstaller_CleanRoot_OnlyVersionDirs_NeverFollowsLinks()
    {
        var dir = Tmp("cr"); var outside = Tmp("crout"); var root = Path.Combine(dir, "update");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "1.0.0")); Directory.CreateDirectory(Path.Combine(root, "naoversao")); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
            Junction(Path.Combine(root, "1.1.0"), outside); // pasta com nome de versão que é link
            UpdateInstaller.CleanRoot(root, _ => true);
            Assert.False(Directory.Exists(Path.Combine(root, "1.0.0")));
            Assert.True(Directory.Exists(Path.Combine(root, "naoversao")));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
            // raiz que passa por link: nada é apagado
            var viaRoot = Path.Combine(dir, "viaroot");
            Directory.CreateDirectory(Path.Combine(outside, "2.0.0"));
            Junction(viaRoot, outside);
            UpdateInstaller.CleanRoot(viaRoot, _ => true);
            Assert.True(Directory.Exists(Path.Combine(outside, "2.0.0")));
            Assert.Throws<InvalidOperationException>(() => UpdateInstaller.PrepareRoot(viaRoot, secure: false));
        }
        finally
        {
            foreach (var l in new[] { Path.Combine(dir, "update", "1.1.0"), Path.Combine(dir, "viaroot") }) { try { Directory.Delete(l); } catch (Exception) { } }
            try { Directory.Delete(dir, true); } catch (Exception) { }
            try { Directory.Delete(outside, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void UpdateInstaller_Cleanup_RestoresOldWhenExeMissing_AndSwapUsesReplace()
    {
        var dir = Tmp("rest"); Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "WinDiag.exe");
            File.WriteAllText(exe + ".old", "antigo");
            UpdateInstaller.CleanupLeftovers(exe, Path.Combine(dir, "u"), "1.0.0");
            Assert.Equal("antigo", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".old"));

            File.WriteAllText(exe + ".new", "sobra");
            File.WriteAllText(exe + ".old", "sobra2");
            UpdateInstaller.CleanupLeftovers(exe, Path.Combine(dir, "u"), "1.0.0");
            Assert.True(File.Exists(exe));
            Assert.False(File.Exists(exe + ".new")); Assert.False(File.Exists(exe + ".old"));

            // troca copiando do handle verificado: a origem fica, o executável muda e o antigo vira .old
            var src = Path.Combine(dir, "baixado.exe"); File.WriteAllText(src, "NOVO");
            using (var h = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var r = UpdateInstaller.SwapExecutable(exe, src, launch: false, verified: h);
                Assert.True(r.Ok, r.Error);
            }
            Assert.Equal("NOVO", File.ReadAllText(exe));
            Assert.Equal("antigo", File.ReadAllText(exe + ".old"));
            Assert.False(File.Exists(exe + ".new"));
            Assert.True(File.Exists(src));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Fluxo que nunca entrega dados: simula um download travado.</summary>
    private sealed class HangStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(Timeout.Infinite); return 0; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 64) + "  WinDiag-Portable-1.3.0.exe\n") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangStream()) });
        }
    }

    [Fact]
    public async Task Download_StalledStream_IsCancelledWithClearMessage()
    {
        var root = Tmp("stall"); var old = UpdateInstaller.StallTimeout;
        try
        {
            UpdateInstaller.StallTimeout = TimeSpan.FromMilliseconds(400);
            var rel = "https://github.com/dougllascda/WinDiag/releases/download/v1.3.0/";
            var info = new UpdateInfo("1.3.0", "x", true, "", new[] { new UpdateAsset("WinDiag-Portable-1.3.0.exe", rel + "WinDiag-Portable-1.3.0.exe", 0), new UpdateAsset("SHA256SUMS.txt", rel + "SHA256SUMS.txt", 0) });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.DownloadAsync(info, InstallKind.Portable, null, new HangHandler(), root, CancellationToken.None));
            Assert.Contains("travou", ex.Message);
            Assert.False(File.Exists(Path.Combine(root, "1.3.0", "WinDiag-Portable-1.3.0.exe")));
            // cancelamento do usuário continua sendo cancelamento
            using var cts = new CancellationTokenSource(200);
            UpdateInstaller.StallTimeout = TimeSpan.FromSeconds(30);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateInstaller.DownloadAsync(info, InstallKind.Portable, null, new HangHandler(), root, cts.Token));
        }
        finally { UpdateInstaller.StallTimeout = old; try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    [Fact]
    public void UpdateInstaller_Roots_AreDistinctAndProtectedWhenElevated()
    {
        Assert.EndsWith(Path.Combine("WinDiagMaint", "update"), UpdateInstaller.SecureRoot);
        Assert.EndsWith(Path.Combine("WinDiag", "update"), UpdateInstaller.LegacyRoot);
        Assert.Contains(UpdateInstaller.UpdateRoot, new[] { UpdateInstaller.SecureRoot, UpdateInstaller.LegacyRoot });
        UpdateInstaller.CleanupLeftovers(); // não lança
    }

    // ---------------------------------------------------------------- B19/B20/B21

    [Fact]
    public async Task History_CorruptFile_IsCopiedToBad_AndWrittenAtomically()
    {
        var dir = Tmp("hbad"); var runner = new ToolRunner(new SnapshotStore(dir));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(runner.HistoryPath, "{ quebrado");
            Assert.Empty(runner.History());
            Assert.True(File.Exists(runner.HistoryPath + ".bad"));
            await runner.RunAsync(new ToolDefinition { Id = "TEST_UNKNOWN", NameText = ("a", "a"), ExplainText = ("b", "b") }, null, CancellationToken.None);
            await runner.RunAsync(new ToolDefinition { Id = "TEST_UNKNOWN", NameText = ("a", "a"), ExplainText = ("b", "b") }, null, CancellationToken.None);
            Assert.Equal(2, runner.History().Count);
            Assert.False(File.Exists(runner.HistoryPath + ".tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void FormatBytes_IsPublicAndUsesUnits()
    {
        Assert.Equal("1 KB", DiskCleanup.FormatBytes(1024));
        Assert.EndsWith("MB", DiskCleanup.FormatBytes(5L * 1024 * 1024));
        Assert.EndsWith("GB", DiskCleanup.FormatBytes(3L * 1024 * 1024 * 1024));
    }
}
