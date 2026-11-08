using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace WinDiag.Core.Sensors;

/// <summary>Resultado da interpretação da memória compartilhada do HWiNFO.</summary>
internal enum HwInfoParseState
{
    /// <summary>Memória válida e ativa.</summary>
    Ok,
    /// <summary>Assinatura "DEAD": o HWiNFO está fechando.</summary>
    Inactive,
    /// <summary>Dados curtos demais, assinatura desconhecida ou cabeçalho incoerente.</summary>
    Invalid,
    /// <summary>Memória válida, mas o <c>poll_time</c> do cabeçalho não muda há mais de 5 s: o HWiNFO está aberto e parado.</summary>
    Stale
}

/// <summary>Saída do parser do HWiNFO.</summary>
/// <param name="State">Situação do bloco lido.</param>
/// <param name="Readings">Leituras (vazio se não for <see cref="HwInfoParseState.Ok"/>).</param>
/// <param name="PollTime">Campo <c>poll_time</c> (int64, offset 12 do cabeçalho); 0 se o bloco não era válido.</param>
internal sealed record HwInfoParseResult(HwInfoParseState State, IReadOnlyList<SensorReading> Readings, long PollTime = 0);

/// <summary>
/// Detecta HWiNFO parado: guarda o último <c>poll_time</c> visto e o instante em que ele mudou.
/// Se não mudar por mais que o limite, as leituras são tratadas como inativas. Não é thread-safe sozinho (o chamador usa lock).
/// Limitação: se o usuário configurou um intervalo de atualização do HWiNFO maior que o limite, será tratado como parado.
/// </summary>
internal sealed class HwInfoFreshness
{
    private long _poll;
    private DateTime _changedUtc;
    private bool _seen;

    /// <summary>Tempo sem mudança que caracteriza o HWiNFO como parado.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>Registra uma leitura do <c>poll_time</c> e diz se está parado. <c>poll_time</c> 0 (desconhecido) nunca é considerado parado.</summary>
    /// <param name="pollTime">Valor do cabeçalho.</param>
    /// <param name="nowUtc">Instante da leitura.</param>
    internal bool IsStale(long pollTime, DateTime nowUtc)
    {
        if (pollTime == 0) { _seen = false; return false; }
        if (!_seen || pollTime != _poll)
        {
            _seen = true;
            _poll = pollTime;
            _changedUtc = nowUtc;
            return false;
        }
        return nowUtc - _changedUtc > Limit;
    }
}

/// <summary>
/// Leitor da memória compartilhada do HWiNFO ("Global\HWiNFO_SENS_SM2", formato documentado no SDK do HWiNFO).
/// Somente leitura: abre o mapeamento existente com direito de leitura e nunca cria nada. O HWiNFO precisa estar
/// aberto com "Shared Memory Support" ligado (a opção gratuita tem limite de tempo de uso do recurso, conforme o HWiNFO).
/// </summary>
internal static class HwInfoSource
{
    /// <summary>Nome da fonte nas leituras.</summary>
    internal const string SourceName = "HWiNFO";
    private const uint SignatureLive = 0x53695748; // 'HWiS'
    private const uint SignatureDead = 0x44414544; // 'DEAD'
    private const int HeaderSize = 44;
    private const int SensorMin = 264;   // id + inst + 2 x char[128]
    private const int ReadingMin = 316;  // tipo + sensor + id + 2 x char[128] + unidade[16] + 4 doubles
    private const int MaxElements = 20000;
    private static readonly string[] Names = { @"Global\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2" };
    private static readonly HwInfoFreshness Fresh = new();
    private static readonly object FreshLock = new();

    /// <summary>
    /// Lê o bloco do HWiNFO e interpreta. Nunca lança exceção; null = HWiNFO ausente (mapeamento não existe).
    /// Se o <c>poll_time</c> ficar parado por mais de 5 s, devolve <see cref="HwInfoParseState.Stale"/> sem leituras.
    /// </summary>
    internal static HwInfoParseResult? Read()
    {
        var r = ReadRaw();
        if (r is { State: HwInfoParseState.Ok })
        {
            bool stale;
            lock (FreshLock) stale = Fresh.IsStale(r.PollTime, DateTime.UtcNow);
            if (stale) return new HwInfoParseResult(HwInfoParseState.Stale, [], r.PollTime);
        }
        return r;
    }

    private static HwInfoParseResult? ReadRaw()
    {
        foreach (var name in Names)
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                long cap = view.Capacity;
                if (cap < HeaderSize) return new HwInfoParseResult(HwInfoParseState.Invalid, []);
                var head = new byte[HeaderSize];
                view.ReadArray(0, head, 0, HeaderSize);
                long need = RequiredLength(head);
                if (need <= 0) return Parse(head);
                int len = (int)Math.Min(Math.Min(need, cap), 64L * 1024 * 1024);
                var buf = new byte[len];
                view.ReadArray(0, buf, 0, len);
                return Parse(buf);
            }
            catch (FileNotFoundException) { /* tenta o outro nome */ }
            catch (Exception) { /* sem permissão ou mapeamento inválido: tenta o outro nome */ }
        }
        return null;
    }

    /// <summary>Tamanho total do bloco, pelo cabeçalho; 0 se o cabeçalho não for utilizável.</summary>
    private static long RequiredLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(h) != SignatureLive) return 0;
        long s = (long)U(h, 20) + (long)U(h, 24) * U(h, 28);
        long r = (long)U(h, 32) + (long)U(h, 36) * U(h, 40);
        return Math.Max(s, r);
    }

    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));

    /// <summary>
    /// Interpreta um bloco de memória no formato do HWiNFO. Usa os tamanhos de elemento informados no cabeçalho para pular
    /// campos que versões novas acrescentam. Nunca lança exceção; elementos fora dos limites são ignorados.
    /// </summary>
    /// <param name="data">Bytes do mapeamento, desde o início.</param>
    internal static HwInfoParseResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < HeaderSize) return new HwInfoParseResult(HwInfoParseState.Invalid, []);
            uint sig = U(data, 0);
            if (sig == SignatureDead) return new HwInfoParseResult(HwInfoParseState.Inactive, []);
            if (sig != SignatureLive) return new HwInfoParseResult(HwInfoParseState.Invalid, []);

            uint offS = U(data, 20), sizeS = U(data, 24), numS = U(data, 28);
            uint offR = U(data, 32), sizeR = U(data, 36), numR = U(data, 40);
            if (sizeS < SensorMin || sizeR < ReadingMin || numS > MaxElements || numR > MaxElements)
                return new HwInfoParseResult(HwInfoParseState.Invalid, []);

            // Sensores: nome (UTF-8 do usuário, se existir; senão o do usuário/original em ANSI).
            var sensors = new List<string?>((int)numS);
            for (uint i = 0; i < numS; i++)
            {
                long o = offS + (long)i * sizeS;
                if (o + sizeS > data.Length) { sensors.Add(null); continue; }
                var el = data.Slice((int)o, (int)sizeS);
                string name = "";
                if (sizeS >= SensorMin + 128) name = Utf8(el.Slice(SensorMin, 128));
                if (name.Length == 0) name = Ansi(el.Slice(136, 128));
                if (name.Length == 0) name = Ansi(el.Slice(8, 128));
                sensors.Add(name);
            }

            var list = new List<SensorReading>((int)numR);
            for (uint i = 0; i < numR; i++)
            {
                long o = offR + (long)i * sizeR;
                if (o + sizeR > data.Length) continue;
                var el = data.Slice((int)o, (int)sizeR);
                uint type = U(el, 0);
                uint sIdx = U(el, 4);
                string label = "";
                if (sizeR >= ReadingMin + 128) label = Utf8(el.Slice(ReadingMin, 128));
                if (label.Length == 0) label = Ansi(el.Slice(140, 128));
                if (label.Length == 0) label = Ansi(el.Slice(12, 128));
                string unit = "";
                if (sizeR >= ReadingMin + 128 + 16) unit = Utf8(el.Slice(ReadingMin + 128, 16));
                if (unit.Length == 0) unit = Ansi(el.Slice(268, 16));
                double value = BinaryPrimitives.ReadDoubleLittleEndian(el.Slice(284, 8));
                if (double.IsNaN(value) || double.IsInfinity(value) || type == 0) continue;
                if (sIdx >= sensors.Count || sensors[(int)sIdx] is not { } hw) continue;
                if (label.Length == 0) continue;

                var kind = type switch
                {
                    1 => SensorKind.Temperature,
                    2 => SensorKind.Voltage,
                    3 => SensorKind.Fan,
                    4 => SensorKind.Current,
                    5 => SensorKind.Power,
                    6 => SensorKind.Clock,
                    7 => SensorKind.Load,
                    _ => SensorKind.Other
                };
                if (kind == SensorKind.Other && (unit.Equals("GB", StringComparison.OrdinalIgnoreCase) || unit.Equals("MB", StringComparison.OrdinalIgnoreCase)))
                    kind = SensorKind.Data;
                list.Add(new SensorReading(hw, HardwareClassifier.Classify(hw), label, kind, value, unit, SourceName));
            }
            return new HwInfoParseResult(HwInfoParseState.Ok, list, BinaryPrimitives.ReadInt64LittleEndian(data.Slice(12, 8)));
        }
        catch (Exception)
        {
            return new HwInfoParseResult(HwInfoParseState.Invalid, []);
        }
    }

    private static string Utf8(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        if (n < 0) n = b.Length;
        return Encoding.UTF8.GetString(b[..n]).Trim();
    }

    // O HWiNFO grava ANSI da página de código local; Latin1 cobre "°" (0xB0) e os acentos comuns do Ocidente.
    private static string Ansi(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        if (n < 0) n = b.Length;
        return Encoding.Latin1.GetString(b[..n]).Trim();
    }
}
