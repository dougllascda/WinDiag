using LibreHardwareMonitor.Hardware;

namespace WinDiag.Core.Sensors;

/// <summary>
/// Leitura de sensores pela biblioteca LibreHardwareMonitorLib (usada sem modificação, como dependência binária).
/// A versão 0.9.6 usa o driver PawnIO (não o WinRing0) para CPU, placa-mãe e memória; o WinDiag NÃO instala esse driver.
/// Sem ele, só são ativadas as peças que não exigem acesso de kernel (placa de vídeo, armazenamento, fontes digitais e controladores USB).
/// A abertura pode levar alguns segundos; leituras seguintes são rápidas. Não é thread-safe.
/// </summary>
internal sealed class LhmSource : IDisposable
{
    /// <summary>Nome da fonte nas leituras.</summary>
    internal const string SourceName = "LibreHardwareMonitor";
    private Computer? _pc;

    /// <summary>true se a biblioteca abriu e está lendo.</summary>
    internal bool Available { get; private set; }
    /// <summary>Versão da biblioteca (ex.: "0.9.6"); null se não carregou.</summary>
    internal string? Version { get; private set; }
    /// <summary>Observações da abertura (o que ficou desligado e por quê).</summary>
    internal List<string> Notes { get; } = new();

    /// <summary>Abre a biblioteca. Nunca lança exceção; falhas viram nota e <see cref="Available"/> = false.</summary>
    /// <param name="kernelDriver">true se o PawnIO está instalado: habilita CPU, placa-mãe e memória.</param>
    internal void Open(bool kernelDriver)
    {
        try
        {
            try
            {
                var v = typeof(Computer).Assembly.GetName().Version;
                Version = v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch (Exception) { Version = null; }

            var pc = new Computer
            {
                IsGpuEnabled = true,
                IsStorageEnabled = true,
                IsPsuEnabled = true,
                IsControllerEnabled = true,
                IsCpuEnabled = kernelDriver,
                IsMotherboardEnabled = kernelDriver,
                IsMemoryEnabled = kernelDriver,
                IsNetworkEnabled = false,
                IsBatteryEnabled = false,
            };
            pc.Open();
            _pc = pc;
            Available = true;
            if (!kernelDriver)
                Notes.Add(Loc.T("Driver PawnIO não instalado: temperatura da CPU, sensores da placa-mãe e da memória ficam indisponíveis pelo LibreHardwareMonitor.", "PawnIO driver not installed: CPU temperature and motherboard and memory sensors are unavailable through LibreHardwareMonitor."));
        }
        catch (Exception ex)
        {
            Available = false;
            Notes.Add(Loc.T("LibreHardwareMonitor não pôde ser aberto (", "LibreHardwareMonitor could not be opened (") + ex.GetType().Name + ": " + ex.Message + ").");
            try { _pc?.Close(); } catch (Exception) { }
            _pc = null;
        }
    }

    /// <summary>Atualiza e devolve as leituras. Nunca lança exceção; peças que falham são ignoradas.</summary>
    internal List<SensorReading> Read()
    {
        var list = new List<SensorReading>();
        var pc = _pc;
        if (!Available || pc is null) return list;
        try
        {
            foreach (var hw in pc.Hardware.ToArray())
            {
                try { Visit(hw, hw.Name, ParentKind(hw), list); }
                catch (Exception) { /* uma peça com defeito não derruba as outras */ }
            }
        }
        catch (Exception) { /* lista alterada durante a leitura */ }
        return list;
    }

    private static void Visit(IHardware hw, string rootName, string rootKind, List<SensorReading> list)
    {
        try { hw.Update(); } catch (Exception) { return; }
        string kind = KindOf(hw, rootKind);
        foreach (var s in hw.Sensors)
        {
            var conv = Convert(s);
            if (conv is null) continue;
            list.Add(new SensorReading(rootName, kind, s.Name, conv.Value.Kind, conv.Value.Value, conv.Value.Unit, SourceName));
        }
        foreach (var sub in hw.SubHardware)
        {
            try { Visit(sub, rootName, rootKind, list); } catch (Exception) { }
        }
    }

    private static string ParentKind(IHardware hw) => KindOf(hw, HardwareClassifier.Other);

    /// <summary>Categoria em pt-BR pelo tipo da peça informado pela biblioteca.</summary>
    internal static string KindOf(IHardware hw, string fallback)
    {
        switch (hw.HardwareType.ToString())
        {
            case "Cpu": return HardwareClassifier.Cpu;
            case "GpuNvidia":
            case "GpuAmd":
            case "GpuIntel": return HardwareClassifier.Gpu;
            case "Memory": return HardwareClassifier.Memory;
            case "Storage": return HardwareClassifier.Storage;
            case "Motherboard":
            case "SuperIO":
            case "EmbeddedController": return HardwareClassifier.Board;
            case "Psu": return HardwareClassifier.Psu;
            case "Cooler":
            case "Controller": return HardwareClassifier.Controller;
            default:
                var byName = HardwareClassifier.Classify(hw.Name);
                return byName == HardwareClassifier.Other ? fallback : byName;
        }
    }

    private static (SensorKind Kind, double Value, string Unit)? Convert(ISensor s)
    {
        if (s.Value is not { } f) return null;
        double v = f;
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        switch (s.SensorType.ToString())
        {
            case "Temperature": return (SensorKind.Temperature, v, "°C");
            case "Fan": return (SensorKind.Fan, v, "RPM");
            case "Voltage": return (SensorKind.Voltage, v, "V");
            case "Current": return (SensorKind.Current, v, "A");
            case "Power": return (SensorKind.Power, v, "W");
            case "Clock": return (SensorKind.Clock, v, "MHz");
            case "Load": return (SensorKind.Load, v, "%");
            case "Data": return (SensorKind.Data, v, "GB");
            case "SmallData": return (SensorKind.Data, v, "MB");
            case "Control": return (SensorKind.Other, v, "%");
            case "Level": return (SensorKind.Other, v, "%");
            case "Throughput": return (SensorKind.Other, v / 1_000_000d, "MB/s");
            case "Frequency": return (SensorKind.Other, v, "Hz");
            case "Flow": return (SensorKind.Other, v, "L/h");
            case "Energy": return (SensorKind.Other, v, "mWh");
            case "Factor": return (SensorKind.Other, v, "");
            case "Noise": return (SensorKind.Other, v, "dBA");
            default: return null;
        }
    }

    /// <summary>Fecha a biblioteca.</summary>
    public void Dispose()
    {
        try { _pc?.Close(); } catch (Exception) { }
        _pc = null;
        Available = false;
    }
}
