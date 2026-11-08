using System.Text;
using WinDiag.Core;
using WinDiag.Core.Collectors;
using WinDiag.Core.Live;
using WinDiag.Core.Model;
using WinDiag.Core.Rules;
using WinDiag.Core.Sensors;

namespace WinDiag.Tests;

/// <summary>Sensores: parser do HWiNFO (memória sintética), classificação, deduplicação do hub, alertas e fonte digital.</summary>
public class SensorTests
{
    private static readonly string[] SensorCols = { "Sensor", "Valor", "Origem" };

    // ---------------------------------------------------------------- memória sintética do HWiNFO

    private static void PutStr(byte[] buf, int off, string s, Encoding enc)
    {
        var b = enc.GetBytes(s);
        Array.Copy(b, 0, buf, off, b.Length);
    }

    private static byte[] BuildHwInfo(uint signature, int sizeS, int sizeR, bool utf8, out int sensorsOff, out int readingsOff)
    {
        // 2 sensores e 4 leituras; tamanhos maiores que o mínimo (campos extras ignorados/UTF-8).
        int offS = 44, numS = 2, numR = 4;
        int offR = offS + sizeS * numS + 16; // folga entre as seções
        sensorsOff = offS; readingsOff = offR;
        var buf = new byte[offR + sizeR * numR];
        BitConverter.GetBytes(signature).CopyTo(buf, 0);
        BitConverter.GetBytes(2u).CopyTo(buf, 4);
        BitConverter.GetBytes(1u).CopyTo(buf, 8);
        BitConverter.GetBytes(123456789L).CopyTo(buf, 12);
        BitConverter.GetBytes((uint)offS).CopyTo(buf, 20);
        BitConverter.GetBytes((uint)sizeS).CopyTo(buf, 24);
        BitConverter.GetBytes((uint)numS).CopyTo(buf, 28);
        BitConverter.GetBytes((uint)offR).CopyTo(buf, 32);
        BitConverter.GetBytes((uint)sizeR).CopyTo(buf, 36);
        BitConverter.GetBytes((uint)numR).CopyTo(buf, 40);

        void Sensor(int i, string orig, string user)
        {
            int o = offS + i * sizeS;
            BitConverter.GetBytes((uint)(0x100 + i)).CopyTo(buf, o);
            PutStr(buf, o + 8, orig, Encoding.Latin1);
            PutStr(buf, o + 136, user, Encoding.Latin1);
            if (utf8 && sizeS >= 264 + 128) PutStr(buf, o + 264, user, Encoding.UTF8);
        }
        void Reading(int i, uint type, int sensor, string label, string unit, double value)
        {
            int o = offR + i * sizeR;
            BitConverter.GetBytes(type).CopyTo(buf, o);
            BitConverter.GetBytes((uint)sensor).CopyTo(buf, o + 4);
            BitConverter.GetBytes((uint)(0x200 + i)).CopyTo(buf, o + 8);
            PutStr(buf, o + 12, label, Encoding.Latin1);
            PutStr(buf, o + 268, unit, Encoding.Latin1);
            BitConverter.GetBytes(value).CopyTo(buf, o + 284);
            BitConverter.GetBytes(value + 10).CopyTo(buf, o + 292);
        }
        Sensor(0, "CPU [#0]: Intel Core i3-10100F", "");
        Sensor(1, "S.M.A.R.T.: Samsung SSD 980 PRO 1TB", "S.M.A.R.T.: Samsung SSD 980 PRO 1TB");
        Reading(0, 1, 0, "CPU Package", "°C", 61.5);
        Reading(1, 3, 0, "CPU Fan", "RPM", 1180);
        Reading(2, 1, 1, "Drive Temperature", "°C", 43);
        Reading(3, 8, 1, "Total Host Writes", "GB", 5120);
        if (utf8 && sizeR >= 316 + 128 + 16)
        {
            int o = offR + 0 * sizeR;
            PutStr(buf, o + 316, "Pacote da CPU", Encoding.UTF8);
            PutStr(buf, o + 444, "°C", Encoding.UTF8);
        }
        return buf;
    }

    [Theory]
    [InlineData(264, 316)]    // mínimo
    [InlineData(300, 400)]    // maiores que o mínimo, sem campos UTF-8 completos
    [InlineData(392, 460)]    // com campos UTF-8
    public void HwInfo_Parse_TwoSensors_FourReadings(int sizeS, int sizeR)
    {
        var buf = BuildHwInfo(0x53695748, sizeS, sizeR, utf8: sizeS >= 392, out _, out _);
        var r = HwInfoSource.Parse(buf);
        Assert.Equal(HwInfoParseState.Ok, r.State);
        Assert.Equal(4, r.Readings.Count);

        var pkg = r.Readings[0];
        Assert.Equal("CPU [#0]: Intel Core i3-10100F", pkg.Hardware);
        Assert.Equal("CPU", pkg.HardwareKind);
        Assert.Equal(SensorKind.Temperature, pkg.Kind);
        Assert.Equal(61.5, pkg.Value);
        Assert.Equal("°C", pkg.Unit);
        Assert.Equal("HWiNFO", pkg.Source);
        Assert.Equal(sizeR >= 460 ? "Pacote da CPU" : "CPU Package", pkg.Name);

        Assert.Equal(SensorKind.Fan, r.Readings[1].Kind);
        Assert.Equal("RPM", r.Readings[1].Unit);
        Assert.Equal("Armazenamento", r.Readings[2].HardwareKind);
        Assert.Equal(SensorKind.Data, r.Readings[3].Kind); // tipo "outro" em GB vira Data
        Assert.Equal(5120, r.Readings[3].Value);
    }

    [Fact]
    public void HwInfo_Parse_DeadSignature_IsInactive_AndGarbageIsInvalid()
    {
        var dead = BuildHwInfo(0x44414544, 264, 316, false, out _, out _);
        var r = HwInfoSource.Parse(dead);
        Assert.Equal(HwInfoParseState.Inactive, r.State);
        Assert.Empty(r.Readings);

        Assert.Equal(HwInfoParseState.Invalid, HwInfoSource.Parse(new byte[10]).State);
        Assert.Equal(HwInfoParseState.Invalid, HwInfoSource.Parse(new byte[200]).State);
    }

    [Fact]
    public void HwInfo_Parse_Truncated_NeverThrows_AndSkipsMissingElements()
    {
        var buf = BuildHwInfo(0x53695748, 264, 316, false, out _, out int offR);
        var cut = buf.AsSpan(0, offR + 316 * 2 + 100).ToArray(); // só 2 leituras completas
        var r = HwInfoSource.Parse(cut);
        Assert.Equal(HwInfoParseState.Ok, r.State);
        Assert.Equal(2, r.Readings.Count);

        // contagem absurda no cabeçalho é recusada
        var bad = BuildHwInfo(0x53695748, 264, 316, false, out _, out _);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bad, 40);
        Assert.Equal(HwInfoParseState.Invalid, HwInfoSource.Parse(bad).State);
    }

    [Fact]
    public void HwInfo_Parse_IgnoresNaN_AndMissingSensorIndex()
    {
        var buf = BuildHwInfo(0x53695748, 264, 316, false, out _, out int offR);
        BitConverter.GetBytes(double.NaN).CopyTo(buf, offR + 284);          // leitura 0 com NaN
        BitConverter.GetBytes(9u).CopyTo(buf, offR + 316 + 4);               // leitura 1 aponta para sensor inexistente
        var r = HwInfoSource.Parse(buf);
        Assert.Equal(2, r.Readings.Count);
    }

    // ---------------------------------------------------------------- classificação

    [Theory]
    [InlineData("CPU [#0]: Intel Core i3-10100F", "CPU")]
    [InlineData("Intel Core i3-10100F", "CPU")]
    [InlineData("AMD Ryzen 5 5600", "CPU")]
    [InlineData("GPU [#0]: NVIDIA GeForce RTX 5060", "Placa de vídeo")]
    [InlineData("NVIDIA GeForce RTX 4060", "Placa de vídeo")]
    [InlineData("AMD Radeon RX 7800 XT", "Placa de vídeo")]
    [InlineData("DIMM Temperature Sensor", "Memória")]
    [InlineData("Memory Timings", "Memória")]
    [InlineData("Generic Memory", "Memória")]
    [InlineData("S.M.A.R.T.: Samsung SSD 980 PRO 1TB (S6B...)", "Armazenamento")]
    [InlineData("Drive: WDC WD10EZEX", "Armazenamento")]
    [InlineData("Samsung SSD 970 EVO", "Armazenamento")]
    [InlineData("Gigabyte B550 AORUS ELITE (Nuvoton NCT6798D)", "Placa-mãe")]
    [InlineData("ITE IT8688E", "Placa-mãe")]
    [InlineData("Nuvoton NCT6797D", "Placa-mãe")]
    [InlineData("Motherboard", "Placa-mãe")]
    [InlineData("Corsair HX850i", "Fonte")]
    [InlineData("Corsair Commander Core", "Controlador")]
    [InlineData("NZXT Kraken X63", "Controlador")]
    [InlineData("Windows Hot Fixes", "Outro")]
    [InlineData("", "Outro")]
    public void Classifier_ByName(string name, string expected)
        => Assert.Equal(expected, HardwareClassifier.Classify(name));

    // ---------------------------------------------------------------- deduplicação do hub

    private static SensorReading R(string hw, string kind, string name, SensorKind k, double v, string src, string unit = "°C")
        => new(hw, kind, name, k, v, unit, src);

    [Fact]
    public void Hub_Merge_KeepsHighestPrioritySource_ForSamePieceNameAndKind()
    {
        var hwinfo = new List<SensorReading>
        {
            R("CPU [#0]", "CPU", "CPU Package", SensorKind.Temperature, 60, "HWiNFO"),
            R("CPU [#0]", "CPU", "Core 0", SensorKind.Temperature, 58, "HWiNFO"),
        };
        var lhm = new List<SensorReading>
        {
            R("Intel Core i3", "CPU", "CPU Package", SensorKind.Temperature, 61, "LibreHardwareMonitor"),   // duplicata: sai
            R("Intel Core i3", "CPU", "CPU Package", SensorKind.Power, 30, "LibreHardwareMonitor", "W"),    // tipo diferente: fica
            R("NVIDIA GPU", "Placa de vídeo", "GPU Core", SensorKind.Temperature, 50, "LibreHardwareMonitor"),
        };
        var smi = new List<SensorReading>
        {
            R("GPU NVIDIA", "Placa de vídeo", "GPU Core", SensorKind.Temperature, 51, "nvidia-smi"),         // duplicata de LHM: sai
            R("GPU NVIDIA", "Placa de vídeo", "GPU Fan", SensorKind.Other, 30, "nvidia-smi", "%"),
        };
        var m = SensorHub.Merge(new IReadOnlyList<SensorReading>[] { hwinfo, lhm, smi });
        Assert.Equal(5, m.Count);
        Assert.Single(m, r => r.Name == "CPU Package" && r.Kind == SensorKind.Temperature);
        Assert.Equal("HWiNFO", m.Single(r => r.Name == "CPU Package" && r.Kind == SensorKind.Temperature).Source);
        Assert.Contains(m, r => r.Kind == SensorKind.Power && r.Source == "LibreHardwareMonitor");
        Assert.Equal("LibreHardwareMonitor", m.Single(r => r.Name == "GPU Core").Source);
        Assert.Contains(m, r => r.Name == "GPU Fan" && r.Source == "nvidia-smi");
    }

    [Fact]
    public void Hub_Merge_KeepsAllReadingsOfSameSource_ForRepeatedPieces()
    {
        var hw = new List<SensorReading>
        {
            R("SSD A", "Armazenamento", "Temperature", SensorKind.Temperature, 40, "LibreHardwareMonitor"),
            R("SSD B", "Armazenamento", "Temperature", SensorKind.Temperature, 42, "LibreHardwareMonitor"),
        };
        Assert.Equal(2, SensorHub.Merge(new IReadOnlyList<SensorReading>[] { hw }).Count);
    }

    // ---------------------------------------------------------------- alertas

    private static SectionResult Apply(IReadOnlyList<SensorReading> readings, SystemFacts? f = null, IReadOnlyCollection<SensorsCollector.DiskInfo>? disks = null)
    {
        var res = new SectionResult { Key = "sensores", Title = "Sensores" };
        SensorsCollector.ApplyRules(readings, f ?? new SystemFacts(), res, disks);
        return res;
    }

    private static SensorReading Cpu(double t, string name = "CPU Package") => R("CPU", "CPU", name, SensorKind.Temperature, t, "HWiNFO");
    private static SensorReading Gpu(double t, string name = "GPU Temperature") => R("GPU", "Placa de vídeo", name, SensorKind.Temperature, t, "LibreHardwareMonitor");

    [Theory]
    [InlineData(95, Severity.Atencao, "sensors.cpu_hot")]
    [InlineData(90, Severity.Atencao, "sensors.cpu_hot")]
    [InlineData(85, Severity.Aviso, "sensors.cpu_warm")]
    [InlineData(80, Severity.Aviso, "sensors.cpu_warm")]
    public void Rules_CpuHot(double t, Severity sev, string id)
    {
        var f = new SystemFacts();
        var r = Apply(new[] { Cpu(t) }, f);
        var fi = Assert.Single(r.Findings);
        Assert.Equal(sev, fi.Sev);
        Assert.Equal(id, fi.Id);
        Assert.False(string.IsNullOrWhiteSpace(fi.Simple));
        Assert.Equal(t, f.CpuTempC);
    }

    [Fact]
    public void Rules_CpuNormal_NoFinding_AndThresholdSensorsIgnored()
    {
        var f = new SystemFacts();
        var r = Apply(new[] { Cpu(55), Cpu(105, "CPU Package Max"), Cpu(100, "Core Distance to TjMax"), Cpu(99, "TjMax") }, f);
        Assert.Empty(r.Findings);
        Assert.Equal(55, f.CpuTempC);
    }

    [Fact]
    public void Rules_NoCpuSensor_GivesInfo_WithHwInfoAndPawnIoGuidance()
    {
        var r = Apply(new[] { Gpu(40) });
        var fi = Assert.Single(r.Findings);
        Assert.Equal("sensors.cpu_unavailable", fi.Id);
        Assert.Equal(Severity.Info, fi.Sev);
        Assert.Equal("Temperatura da CPU indisponível", fi.Title);
        Assert.Contains("HWiNFO", fi.Simple);
        Assert.Contains("Shared Memory Support", fi.Simple);
        Assert.Contains("PawnIO", fi.Simple);
        Assert.Contains("não instala", fi.Simple);
    }

    [Theory]
    [InlineData(86, Severity.Atencao, "sensors.gpu_hot")]
    [InlineData(85, Severity.Atencao, "sensors.gpu_hot")]
    [InlineData(82, Severity.Aviso, "sensors.gpu_warm")]
    public void Rules_GpuHot(double t, Severity sev, string id)
    {
        var f = new SystemFacts();
        var r = Apply(new[] { Cpu(40), Gpu(t) }, f);
        var fi = Assert.Single(r.Findings);
        Assert.Equal(sev, fi.Sev);
        Assert.Equal(id, fi.Id);
        Assert.Equal(t, f.GpuTempC);
    }

    [Fact]
    public void Rules_GpuHotSpot_UsesPlusTenThresholds_AndDoesNotCountAsCore()
    {
        var f = new SystemFacts();
        // Hot spot em 88 não alerta (limiar 90); núcleo em 70 não alerta.
        Assert.Empty(Apply(new[] { Cpu(40), Gpu(70), Gpu(88, "GPU Hot Spot Temperature") }, f).Findings);
        Assert.Equal(70, f.GpuTempC);
        var warn = Assert.Single(Apply(new[] { Cpu(40), Gpu(70), Gpu(91, "GPU Hot Spot Temperature") }).Findings);
        Assert.Equal(Severity.Aviso, warn.Sev);
        var hot = Assert.Single(Apply(new[] { Cpu(40), Gpu(70), Gpu(96, "GPU Hot Spot Temperature") }).Findings);
        Assert.Equal(Severity.Atencao, hot.Sev);
    }

    [Fact]
    public void Rules_FanStopped_OnlyWithHotPiece()
    {
        var fanGpu = R("GPU", "Placa de vídeo", "GPU Fan", SensorKind.Fan, 0, "LibreHardwareMonitor", "RPM");
        // núcleo a 75 (>= 70): alerta
        var hot = Apply(new[] { Cpu(40), Gpu(75), fanGpu });
        var fi = Assert.Single(hot.Findings);
        Assert.Equal(Severity.Aviso, fi.Sev);
        Assert.StartsWith("sensors.fan_stopped_gpu", fi.Id);
        // 62 °C: zero-RPM é normal, sem alerta
        Assert.Empty(Apply(new[] { Cpu(40), Gpu(62), fanGpu }).Findings);
        // hot spot alto não conta (só o núcleo)
        Assert.Empty(Apply(new[] { Cpu(40), Gpu(60), Gpu(80, "GPU Hot Spot Temperature"), fanGpu }).Findings);
        // ventoinha girando: sem alerta
        var spinning = R("GPU", "Placa de vídeo", "GPU Fan", SensorKind.Fan, 900, "LibreHardwareMonitor", "RPM");
        Assert.Empty(Apply(new[] { Cpu(40), Gpu(75), spinning }).Findings);
        // ventoinha de chassi parada (placa-mãe) não está ligada a uma peça quente
        var sysFan = R("Placa", "Placa-mãe", "System Fan #2", SensorKind.Fan, 0, "HWiNFO", "RPM");
        Assert.Empty(Apply(new[] { Cpu(75), sysFan }).Findings);
        // cabeçalho opcional / bomba parados não contam
        Assert.Empty(Apply(new[] { Cpu(75), R("Placa", "Placa-mãe", "OPT_FAN1", SensorKind.Fan, 0, "HWiNFO", "RPM"), R("Placa", "Placa-mãe", "AIO_PUMP", SensorKind.Fan, 0, "HWiNFO", "RPM") }).Findings);
        // ventoinha da CPU na placa-mãe: só com CPU >= 70
        var cpuFan = R("Placa", "Placa-mãe", "CPU Fan", SensorKind.Fan, 0, "HWiNFO", "RPM");
        Assert.Contains(Apply(new[] { Cpu(75), cpuFan }).Findings, x => x.Id.StartsWith("sensors.fan_stopped_cpu"));
        Assert.Empty(Apply(new[] { Cpu(65), cpuFan }).Findings);
    }

    [Fact]
    public void PickMaxTemps_PrefersPackage_AndIgnoresLimitsAndHotSpot()
    {
        var (cpu, gpu) = SensorsCollector.PickMaxTemps(new[]
        {
            Cpu(50, "Core #0"), Cpu(70, "Core #1"), Cpu(55, "CPU Package"), Cpu(100, "CPU Package Max"),
            Gpu(60), Gpu(80, "GPU Hot Spot Temperature"), Gpu(90, "GPU Memory Junction Temperature"),
        });
        Assert.Equal(55, cpu);
        Assert.Equal(60, gpu);
        Assert.Equal((null, null), SensorsCollector.PickMaxTemps(Array.Empty<SensorReading>()));
    }

    [Fact]
    public void SharedHub_Null_FallsBackToOwnHubPath()
    {
        Assert.Null(SensorsCollector.SharedHub);
        Assert.Null(SensorsCollector.SharedHubLock);
    }

    [Fact]
    public void HwInfo_Freshness_FrozenPollTime_IsStaleAfterFiveSeconds()
    {
        var buf = BuildHwInfo(0x53695748, 264, 316, false, out _, out _);
        var r1 = HwInfoSource.Parse(buf);
        var r2 = HwInfoSource.Parse(buf); // memória idêntica: poll_time congelado
        Assert.Equal(123456789L, r1.PollTime);
        var fr = new HwInfoFreshness();
        var t0 = DateTime.UtcNow;
        Assert.False(fr.IsStale(r1.PollTime, t0));
        Assert.False(fr.IsStale(r2.PollTime, t0.AddSeconds(4)));
        Assert.True(fr.IsStale(r2.PollTime, t0.AddSeconds(6)));
        // poll_time voltou a andar: não está mais parado
        BitConverter.GetBytes(123456790L).CopyTo(buf, 12);
        Assert.False(fr.IsStale(HwInfoSource.Parse(buf).PollTime, t0.AddSeconds(7)));
        // campo ausente (0) nunca é tratado como parado
        Assert.False(new HwInfoFreshness().IsStale(0, t0.AddHours(1)));
    }

    [Fact]
    public void Disks_SataSsd60_NoAlert_Hdd60_Alerts_NvmeLimit75()
    {
        SensorReading D(string hw, double t) => R(hw, "Armazenamento", "Temperature", SensorKind.Temperature, t, "LibreHardwareMonitor");
        var disks = new[]
        {
            new SensorsCollector.DiskInfo("Crucial MX500", false, false),
            new SensorsCollector.DiskInfo("WDC WD10EZEX", false, true),
            new SensorsCollector.DiskInfo("Samsung SSD 980 PRO 1TB", true, false),
        };
        Assert.Empty(Apply(new[] { Cpu(40), D("S.M.A.R.T.: Crucial MX500 (1234ABC)", 60) }, disks: disks).Findings);
        var hdd = Assert.Single(Apply(new[] { Cpu(40), D("WDC WD10EZEX", 60) }, disks: disks).Findings);
        Assert.StartsWith("sensors.disk_hot.", hdd.Id);
        Assert.Empty(Apply(new[] { Cpu(40), D("Samsung SSD 980 PRO 1TB", 74) }, disks: disks).Findings);
        Assert.Single(Apply(new[] { Cpu(40), D("Samsung SSD 980 PRO 1TB", 76) }, disks: disks).Findings);
        // sem casamento: 70
        Assert.Empty(Apply(new[] { Cpu(40), D("Disco Qualquer 9000", 69) }, disks: disks).Findings);
        Assert.Single(Apply(new[] { Cpu(40), D("Disco Qualquer 9000", 71) }, disks: disks).Findings);
        // dois discos quentes têm Ids diferentes
        var two = Apply(new[] { Cpu(40), D("WDC WD10EZEX", 60), D("Seagate Barracuda", 80) }, disks: disks);
        Assert.Equal(2, two.Findings.Select(x => x.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("Corsair RM850x", 850)]
    [InlineData("Corsair AX760", 760)]
    [InlineData("Corsair H150i", null)]
    [InlineData("Corsair H100i RM750", 750)]
    public void Psu_Watts_PrefersPsuSuffix_AndSkipsCoolers(string model, int? watts)
        => Assert.Equal(watts, SensorsCollector.ParsePsuWatts(model));

    [Fact]
    public void Rules_Disks_ThresholdSensorsIgnored_AndUnknownBusUses70()
    {
        SensorReading D(string hw, double t, string name = "Temperature") => R(hw, "Armazenamento", name, SensorKind.Temperature, t, "LibreHardwareMonitor");
        var disks = new[] { new SensorsCollector.DiskInfo("Samsung SSD 980 PRO 1TB", true, false) };
        // barramento desconhecido: limiar 70
        Assert.Empty(Apply(new[] { Cpu(40), D("Crucial MX500", 60) }, disks: null).Findings);
        Assert.Single(Apply(new[] { Cpu(40), D("Crucial MX500", 72) }, disks: null).Findings);
        // limiares gravados pelo disco (warning/critical) não contam como temperatura
        Assert.Empty(Apply(new[] { Cpu(40), D("Samsung SSD 980 PRO 1TB", 60), D("Samsung SSD 980 PRO 1TB", 84, "Warning Temperature"), D("Samsung SSD 980 PRO 1TB", 85, "Critical Temperature") }, disks: disks).Findings);
    }

    [Fact]
    public void Rules_Vrm_Above100()
    {
        var vrm = R("Placa", "Placa-mãe", "VRM MOS", SensorKind.Temperature, 101, "HWiNFO");
        var fi = Assert.Single(Apply(new[] { Cpu(40), vrm }).Findings);
        Assert.Equal("sensors.vrm_hot", fi.Id);
        Assert.Empty(Apply(new[] { Cpu(40), vrm with { Value = 95 } }).Findings);
    }

    // ---------------------------------------------------------------- fonte digital

    [Theory]
    [InlineData("Corsair HX850i", 850)]
    [InlineData("Corsair RM1000x", 1000)]
    [InlineData("NZXT E850", 850)]
    [InlineData("Corsair Commander", null)]
    public void Psu_WattsParsedFromModel(string model, int? watts) => Assert.Equal(watts, SensorsCollector.ParsePsuWatts(model));

    [Fact]
    public void Collector_DigitalPsu_FillsFacts_AndInsightsUsesIt()
    {
        var readings = new List<SensorReading>
        {
            Cpu(50),
            R("Corsair HX850i", "Fonte", "Total Watts", SensorKind.Power, 120, "LibreHardwareMonitor", "W"),
        };
        var snap = new SensorSnapshot { Time = DateTime.Now, Readings = readings, SourcesUsed = new[] { "LibreHardwareMonitor" } };
        var status = new SensorStatus { LhmAvailable = true, LhmVersion = "0.9.6" };
        var c = new SensorsCollector(() => (snap, status));
        var f = new SystemFacts
        {
            CpuName = "Intel(R) Core(TM) i3-10100F CPU @ 3.60GHz", CpuCores = 4, CpuThreads = 8,
            GpuName = "NVIDIA GeForce RTX 5060", GpuVramMiB = 8151, GpuPowerLimitW = 145,
        };
        var res = c.Collect(f);

        Assert.Equal("sensores", c.Key);
        Assert.Equal("Sensores", c.Title);
        Assert.Equal("Corsair HX850i", f.PsuDetectedModel);
        Assert.Equal(850, f.PsuDetectedWatts);
        Assert.Equal(50, f.CpuTempC);
        Assert.Contains(res.Groups, g => g.Title == "Fontes de sensores");
        Assert.Contains(res.Groups, g => g.Title == "Corsair HX850i" && g.Tables.Any(t => t.Columns.SequenceEqual(SensorCols)));

        // Sem fonte informada: a Insights usa a detectada.
        var ins = Insights.Analyze(f, new List<SectionResult>());
        var psu = ins.Findings.Single(x => x.Id == "analise.psu");
        Assert.NotEqual("Fonte de alimentação não informada", psu.Title);
        Assert.Contains("detectada", psu.Evidence);
        Assert.Contains("850", psu.Evidence);
        Assert.Contains(ins.Groups.Single(g => g.Title == "Fonte de alimentação").Rows, r => r.Label == "Fonte detectada");

        // Fonte informada pelo usuário tem prioridade.
        f.PsuWatts = 200;
        var user = Insights.Analyze(f, new List<SectionResult>()).Findings.Single(x => x.Id == "analise.psu");
        Assert.Equal(Severity.Atencao, user.Sev);
        Assert.Contains("informada", user.Evidence);
    }

    [Fact]
    public void Collector_WithoutSources_DoesNotThrow_AndNotesIt()
    {
        var c = new SensorsCollector(() => (new SensorSnapshot { Time = DateTime.Now }, new SensorStatus()));
        var res = c.Collect(new SystemFacts());
        Assert.Null(res.Error);
        Assert.Contains(res.Findings, f => f.Id == "sensors.cpu_unavailable");
        Assert.Contains(res.Notes, n => n.Contains("nenhuma fonte"));

        var boom = new SensorsCollector(() => throw new InvalidOperationException("falha simulada"));
        var r2 = boom.Collect(new SystemFacts());
        Assert.Null(r2.Error);
        Assert.NotEmpty(r2.Notes);
    }

    // ---------------------------------------------------------------- captura

    [Fact]
    public void Capture_ThermalThrottling_FromTemperature()
    {
        CaptureSample S(int i, double? cpu, double? gpu) => new() { Second = i, CpuPct = 50, Gpu3DPct = 90, CpuTempC = cpu, GpuTempC = gpu };
        var hot = Enumerable.Range(1, 10).Select(i => S(i, i == 5 ? 96 : 80, 70)).ToList();
        var r = Capture.Summarize(hot);
        Assert.Contains(r.Rows, x => x.Metric == "Temperatura máx. da CPU" && x.Max == 96);
        Assert.Contains(r.Interpretation, t => t.Contains("limitação térmica") && t.Contains("CPU"));

        var gpuHot = Enumerable.Range(1, 10).Select(i => S(i, 70, 88)).ToList();
        Assert.Contains(Capture.Summarize(gpuHot).Interpretation, t => t.Contains("limitação térmica") && t.Contains("GPU"));

        var cool = Enumerable.Range(1, 10).Select(i => S(i, 70, 70)).ToList();
        Assert.DoesNotContain(Capture.Summarize(cool).Interpretation, t => t.Contains("limitação térmica"));

        var none = Enumerable.Range(1, 10).Select(i => new CaptureSample { Second = i, CpuPct = 10 }).ToList();
        Assert.DoesNotContain(Capture.Summarize(none).Rows, x => x.Metric.StartsWith("Temperatura máx."));
    }

    [Fact]
    public void Glossary_CoversSensors()
    {
        Assert.NotNull(WinDiag.Core.Knowledge.Glossary.Lookup("Sensores"));
        Assert.NotNull(WinDiag.Core.Knowledge.Glossary.Lookup("Fontes de sensores"));
        Assert.Equal("sensores", WinDiag.Core.Knowledge.Glossary.Lookup("Intel Core i3-10100F", "sensores")!.Key);
    }

    [Fact]
    public void ScanEngine_IncludesSensorsCollector()
    {
        var all = ScanEngine.CreateCollectors();
        Assert.Contains(all, c => c.Key == "sensores");
        Assert.Equal(all.Count + 1, ScanEngine.SectionCount);
    }
}
