namespace WinDiag.Core.Sensors;

/// <summary>Tipo de grandeza medida por um sensor.</summary>
public enum SensorKind
{
    /// <summary>Temperatura, em °C.</summary>
    Temperature,
    /// <summary>Velocidade de ventoinha, em RPM.</summary>
    Fan,
    /// <summary>Tensão, em V.</summary>
    Voltage,
    /// <summary>Corrente, em A.</summary>
    Current,
    /// <summary>Potência, em W.</summary>
    Power,
    /// <summary>Frequência de clock, em MHz.</summary>
    Clock,
    /// <summary>Carga ou uso, em %.</summary>
    Load,
    /// <summary>Quantidade de dados (GB, MB).</summary>
    Data,
    /// <summary>Qualquer outra grandeza.</summary>
    Other
}

/// <summary>Uma leitura de sensor, já normalizada.</summary>
/// <param name="Hardware">Nome da peça (ex.: "Intel Core i3-10100F").</param>
/// <param name="HardwareKind">Categoria curta da peça em pt-BR: "CPU", "Placa-mãe", "Placa de vídeo", "Memória", "Armazenamento", "Fonte", "Controlador" ou "Outro".</param>
/// <param name="Name">Nome do sensor (ex.: "CPU Package").</param>
/// <param name="Kind">Tipo de grandeza.</param>
/// <param name="Value">Valor na unidade de <paramref name="Unit"/>.</param>
/// <param name="Unit">Unidade ("°C", "RPM", "V", "A", "W", "MHz", "%", "GB"...).</param>
/// <param name="Source">Origem da leitura ("HWiNFO", "LibreHardwareMonitor" ou "nvidia-smi").</param>
public sealed record SensorReading(string Hardware, string HardwareKind, string Name, SensorKind Kind, double Value, string Unit, string Source);

/// <summary>Conjunto de leituras feitas de uma vez.</summary>
public sealed class SensorSnapshot
{
    /// <summary>Instante da leitura (hora local).</summary>
    public DateTime Time { get; init; }
    /// <summary>Leituras já sem duplicatas óbvias.</summary>
    public IReadOnlyList<SensorReading> Readings { get; init; } = [];
    /// <summary>Fontes que realmente trouxeram leituras nesta passada.</summary>
    public IReadOnlyList<string> SourcesUsed { get; init; } = [];
}

/// <summary>Situação das fontes de sensores no momento.</summary>
public sealed class SensorStatus
{
    /// <summary>true se a memória compartilhada do HWiNFO está ativa.</summary>
    public bool HwInfoAvailable { get; init; }
    /// <summary>true se a biblioteca LibreHardwareMonitor abriu e está lendo.</summary>
    public bool LhmAvailable { get; init; }
    /// <summary>true se há um driver de kernel de leitura de hardware instalado (PawnIO).</summary>
    public bool KernelDriverAvailable { get; init; }
    /// <summary>Nome do driver detectado ("PawnIO") ou vazio.</summary>
    public string DriverName { get; init; } = "";
    /// <summary>Versão da LibreHardwareMonitorLib embutida; null se não carregou.</summary>
    public string? LhmVersion { get; init; }
    /// <summary>Observações sobre o que ficou indisponível e por quê.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Rótulos de exibição das categorias de peça (<see cref="SensorReading.HardwareKind"/>), cujos valores internos ficam estáveis em português.</summary>
public static class HardwareKinds
{
    /// <summary>Rótulo da categoria no idioma atual. Valores desconhecidos são devolvidos como vieram.</summary>
    /// <param name="kind">Valor de <see cref="SensorReading.HardwareKind"/> ("CPU", "Placa-mãe", "Placa de vídeo", "Memória", "Armazenamento", "Fonte", "Controlador" ou "Outro").</param>
    public static string HardwareKindLabel(string kind) => kind switch
    {
        "CPU" => "CPU",
        "Placa-mãe" => Loc.T("Placa-mãe", "Motherboard"),
        "Placa de vídeo" => Loc.T("Placa de vídeo", "Graphics card"),
        "Memória" => Loc.T("Memória", "Memory"),
        "Armazenamento" => Loc.T("Armazenamento", "Storage"),
        "Fonte" => Loc.T("Fonte", "Power supply"),
        "Controlador" => Loc.T("Controlador", "Controller"),
        "Outro" => Loc.T("Outro", "Other"),
        _ => kind,
    };
}
