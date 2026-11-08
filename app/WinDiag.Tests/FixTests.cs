using Microsoft.Win32;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;

namespace WinDiag.Tests;

public class FixTests
{
    [Fact]
    public void MergeDx_AddsWhenMissing() =>
        Assert.Equal("SwapEffectUpgradeEnable=1;", FixEngine.MergeDxSetting("", "SwapEffectUpgradeEnable", "1"));

    [Fact]
    public void MergeDx_ReplacesAndPreservesOthers() =>
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;",
            FixEngine.MergeDxSetting("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1"));

    [Fact]
    public void StartupBytes_DisabledHasOddFirstByteAndTimestamp()
    {
        var b = FixEngine.StartupApprovedBytes(false, new DateTime(2026, 11, 8));
        Assert.Equal(12, b.Length);
        Assert.Equal(3, b[0]);
        Assert.NotEqual(0, BitConverter.ToInt64(b, 4));
        Assert.Equal(2, FixEngine.StartupApprovedBytes(true, DateTime.Now)[0]);
    }

    // Snapshot real gravado pela v7 (PowerShell 5.1, UTF-8 com BOM, sem campo Action).
    private const string V7Json = """
    {
        "Fixes":  [
                      { "Name":  "Ativar otimizacoes para jogos em janela", "Id":  "WINDOWED_OPT", "Reversible":  true,
                        "State":  { "Reg":  [ { "Kind":  "DWord", "Path":  "HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences",
                                                "Name":  "DirectXUserGlobalSettings", "Value":  null, "Exists":  false } ],
                                    "Bcd":  [ ], "Hib":  null, "Mmc":  null, "Power":  [ ], "Svc":  [ ] } },
                      { "Name":  "Desativar o hypervisor", "Id":  "HYPERVISOR_OFF", "Reversible":  true,
                        "State":  { "Reg":  [ ], "Bcd":  [ { "Old":  "Auto", "Name":  "hypervisorlaunchtype" } ],
                                    "Hib":  null, "Mmc":  null, "Power":  [ ], "Svc":  [ ] } }
                  ],
        "Created":  "2026-11-08T07:26:30",
        "Version":  "7.0"
    }
    """;

    private static SnapshotStore StoreWith(params (string name, string json)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wdtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "snapshots"));
        foreach (var (name, json) in files)
            File.WriteAllText(Path.Combine(dir, "snapshots", name), json, new System.Text.UTF8Encoding(true));
        return new SnapshotStore(dir);
    }

    [Fact]
    public void ReadsV7SnapshotWithBom()
    {
        var store = StoreWith(("snapshot_20261108_072630.json", V7Json));
        var list = store.List();
        Assert.Single(list);
        Assert.Equal(2, list[0].Data.Fixes.Count);
        Assert.True(list[0].Data.Fixes[0].IsApply);
        Assert.Equal("Auto", list[0].Data.Fixes[1].State.Bcd[0].Old);
    }

    [Fact]
    public void ActiveItemAppliedByWinDiag_IsToggleable()
    {
        var store = StoreWith(("snapshot_20261108_072630.json", V7Json));
        var facts = new SystemFacts { WindowedOpt = "1" };
        var st = new FixEngine(store, dryRun: true).GetStatuses(facts).Single(s => s.Fix.Id == "WINDOWED_OPT");
        Assert.Equal(FixStatusKind.AtivoWinDiag, st.Kind);
        Assert.True(st.CanToggle);
        Assert.NotNull(st.Source);
    }

    [Fact]
    public void ActiveItemWithoutSnapshot_IsExternalAndLocked()
    {
        var store = StoreWith();
        var facts = new SystemFacts { GameDVR = 0 };
        var st = new FixEngine(store, dryRun: true).GetStatuses(facts).Single(s => s.Fix.Id == "GAMEDVR");
        Assert.Equal(FixStatusKind.AtivoExterno, st.Kind);
        Assert.False(st.CanToggle);
    }

    [Fact]
    public void AfterUndoEntry_ItemIsNoLongerOwnedByWinDiag()
    {
        var undo = """
        { "Created": "2026-11-08T09:00:00", "Version": "8.0",
          "Fixes": [ { "Id": "WINDOWED_OPT", "Name": "Desfazer", "Reversible": true, "Action": "Desfazer",
                       "State": { "Reg": [], "Bcd": [], "Power": [], "Svc": [] } } ] }
        """;
        var store = StoreWith(("snapshot_20261108_072630.json", V7Json), ("snapshot_20261108_090000.json", undo));
        var facts = new SystemFacts { WindowedOpt = "1" }; // reativado por fora depois do desfazer
        var st = new FixEngine(store, dryRun: true).GetStatuses(facts).Single(s => s.Fix.Id == "WINDOWED_OPT");
        Assert.Equal(FixStatusKind.AtivoExterno, st.Kind);
    }

    [Fact]
    public void DryRunDisable_RestoresFromSourceWithoutWriting()
    {
        var store = StoreWith(("snapshot_20261108_072630.json", V7Json));
        var eng = new FixEngine(store, dryRun: true);
        var facts = new SystemFacts { Bcd = new(StringComparer.OrdinalIgnoreCase) { ["hypervisorlaunchtype"] = "Off" } };
        var st = eng.GetStatuses(facts).Single(s => s.Fix.Id == "HYPERVISOR_OFF");
        Assert.Equal(FixStatusKind.AtivoWinDiag, st.Kind);
        var r = eng.Disable(st);
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Messages, m => m.Contains("hypervisorlaunchtype restaurado para Auto"));
        Assert.Single(store.List()); // nenhum snapshot novo em simulação
    }

    [Fact]
    public void ToRegistryValue_ConvertsJsonBinaryAndDword()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "b": [3,0,0,0], "d": 5 }""");
        var bin = (byte[])FixEngine.ToRegistryValue(doc.RootElement.GetProperty("b").Clone(), RegistryValueKind.Binary);
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, bin);
        Assert.Equal(5, FixEngine.ToRegistryValue(doc.RootElement.GetProperty("d").Clone(), RegistryValueKind.DWord));
    }
}
