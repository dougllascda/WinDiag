using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinDiag.Core.Util;

namespace WinDiag.Core.Tools;

/// <summary>Resultado dos pings a um destino.</summary>
/// <param name="Target">Nome exibido do destino (ex.: "1.1.1.1").</param>
/// <param name="Sent">Pings enviados.</param>
/// <param name="Received">Respostas recebidas.</param>
/// <param name="AvgMs">Tempo médio de resposta em ms; null se nenhuma resposta chegou.</param>
/// <param name="JitterMs">Oscilação: média da diferença absoluta entre respostas consecutivas, em ms; null com menos de 2 respostas.</param>
/// <param name="LossPct">Perda em %.</param>
public sealed record PingStats(string Target, int Sent, int Received, double? AvgMs, double? JitterMs, double LossPct);

/// <summary>Conclusão do teste de conexão.</summary>
public enum NetVerdict
{
    /// <summary>Sem problema nas medidas.</summary>
    AllGood,
    /// <summary>O trecho até o roteador está ruim: Wi-Fi, cabo ou o próprio roteador.</summary>
    LocalProblem,
    /// <summary>O roteador responde bem, mas a saída para a internet está ruim: provedor ou rota.</summary>
    ProviderProblem,
    /// <summary>Os pings passam, mas o nome não foi resolvido: DNS.</summary>
    DnsProblem,
    /// <summary>A internet está boa, mas o roteador não responde a ping (muitos bloqueiam): não é defeito.</summary>
    RouterIgnoresPing,
    /// <summary>Nenhuma resposta nem do roteador: sem conexão local.</summary>
    NoConnection,
    /// <summary>Dados insuficientes para concluir.</summary>
    Inconclusive,
}

/// <summary>Resumo de um adaptador de rede, usado para detectar IP fixo sem depender da leitura do sistema.</summary>
/// <param name="Name">Nome do adaptador.</param>
/// <param name="Up">true se está conectado.</param>
/// <param name="Loopback">true se é o adaptador de loopback.</param>
/// <param name="Tunnel">true se é um túnel.</param>
/// <param name="DhcpEnabled">true se o IPv4 vem por DHCP.</param>
/// <param name="HasIpv4">true se tem endereço IPv4 (da API ou configurado no registro, mesmo desconectado).</param>
/// <param name="Description">Descrição do adaptador (usada para reconhecer adaptadores virtuais).</param>
/// <param name="Ips">Endereços IPv4 conhecidos, para exibir ao usuário.</param>
public sealed record AdapterInfo(string Name, bool Up, bool Loopback, bool Tunnel, bool DhcpEnabled, bool HasIpv4, string Description = "", IReadOnlyList<string>? Ips = null);

/// <summary>Cálculos e leituras do teste de conexão. Os cálculos são funções puras (testáveis sem rede); a medição usa só a API de ping do .NET.</summary>
public static class NetDiag
{
    /// <summary>Número de pings por destino.</summary>
    public const int PingCount = 10;

    /// <summary>Oscilação (jitter): média da diferença absoluta entre amostras consecutivas. Null com menos de 2 amostras.</summary>
    /// <param name="samples">Tempos de resposta em ms, na ordem de chegada (sem as perdas).</param>
    public static double? Jitter(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2) return null;
        double sum = 0;
        for (int i = 1; i < samples.Count; i++) sum += Math.Abs(samples[i] - samples[i - 1]);
        return sum / (samples.Count - 1);
    }

    /// <summary>Calcula média, jitter e perda de um destino.</summary>
    /// <param name="target">Nome do destino.</param>
    /// <param name="rttMs">Um item por ping enviado: tempo em ms ou null se foi perdido.</param>
    public static PingStats Compute(string target, IReadOnlyList<double?> rttMs)
    {
        var ok = rttMs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        int sent = rttMs.Count;
        double loss = sent == 0 ? 100 : (sent - ok.Count) * 100d / sent;
        return new PingStats(target, sent, ok.Count, ok.Count > 0 ? ok.Average() : null, Jitter(ok), loss);
    }

    /// <summary>true se a medida do trecho até o roteador está ruim: perda de 10% ou mais, média acima de 30 ms ou jitter acima de 30 ms (regra prática do WinDiag, não norma).</summary>
    /// <param name="s">Medidas do gateway.</param>
    public static bool GatewayBad(PingStats s) => s.LossPct >= 10 || s.AvgMs is > 30 || s.JitterMs is > 30;

    /// <summary>true se a medida de um destino da internet está ruim: perda de 5% ou mais, média acima de 150 ms ou jitter acima de 50 ms (regra prática do WinDiag, não norma).</summary>
    /// <param name="s">Medidas do destino.</param>
    public static bool InternetBad(PingStats s) => s.LossPct >= 5 || s.AvgMs is > 150 || s.JitterMs is > 50;

    /// <summary>
    /// Conclui o teste em linguagem simples a partir das medidas. O trecho local só é culpado quando o roteador responde com perda ou oscilação altas,
    /// ou quando roteador e destinos externos estão ruins juntos. Roteador mudo com internet boa não é defeito (muitos roteadores bloqueiam ping).
    /// </summary>
    /// <param name="gateway">Medidas do roteador; null se o gateway não foi encontrado.</param>
    /// <param name="internet">Medidas dos destinos da internet (1.1.1.1 e 8.8.8.8).</param>
    /// <param name="dnsOk">true se o nome foi resolvido; null se o teste de DNS não rodou.</param>
    public static NetVerdict Verdict(PingStats? gateway, IReadOnlyList<PingStats> internet, bool? dnsOk)
    {
        if (internet.Count == 0) return NetVerdict.Inconclusive;
        bool internetDown = internet.All(s => s.Received == 0);
        bool internetBad = internet.Any(InternetBad);
        if (gateway is null)
        {
            if (internetBad) return NetVerdict.Inconclusive;
            return dnsOk == false ? NetVerdict.DnsProblem : NetVerdict.AllGood;
        }
        bool gatewayMute = gateway.Received == 0;
        if (gatewayMute && internetDown) return NetVerdict.NoConnection;
        if (gatewayMute)
        {
            if (internetBad) return NetVerdict.LocalProblem; // roteador e internet ruins juntos
            return dnsOk == false ? NetVerdict.DnsProblem : NetVerdict.RouterIgnoresPing;
        }
        if (GatewayBad(gateway)) return NetVerdict.LocalProblem;
        if (internetBad) return NetVerdict.ProviderProblem;
        return dnsOk == false ? NetVerdict.DnsProblem : NetVerdict.AllGood;
    }
    /// <summary>Texto do veredito no idioma atual, em linguagem simples e sem afirmar causa além do que foi medido.</summary>
    /// <param name="v">Veredito.</param>
    public static string VerdictText(NetVerdict v) => v switch
    {
        NetVerdict.AllGood => Loc.T("Tudo certo: o roteador e a internet responderam bem neste teste.", "All good: the router and the internet answered well in this test."),
        NetVerdict.LocalProblem => Loc.T("O trecho até o roteador está ruim (perda ou oscilação). Isso aponta para o Wi-Fi, o cabo ou o roteador, não para o provedor. Tente chegar mais perto do roteador, usar cabo ou reiniciar o roteador.",
            "The stretch to the router is poor (loss or jitter). That points to Wi-Fi, the cable or the router, not the provider. Try moving closer to the router, using a cable or restarting the router."),
        NetVerdict.ProviderProblem => Loc.T("O roteador responde bem, mas a saída para a internet está ruim. Isso aponta para o provedor ou para a rota até ele. Se persistir, vale falar com o provedor.",
            "The router answers well, but the way out to the internet is poor. That points to the provider or the route to it. If it persists, it is worth contacting the provider."),
        NetVerdict.DnsProblem => Loc.T("Os pings passam, mas o nome do site não foi resolvido: o problema parece estar no DNS. Limpar o cache de DNS ou trocar o servidor de DNS pode ajudar.",
            "Pings go through, but the site name was not resolved: the problem looks like DNS. Flushing the DNS cache or changing the DNS server may help."),
        NetVerdict.RouterIgnoresPing => Loc.T("A conexão está boa. O roteador não responde a ping (muitos bloqueiam isso de propósito), o que não é defeito.", "The connection is good. The router does not answer ping (many block it on purpose), which is not a fault."),
        NetVerdict.NoConnection => Loc.T("Nem o roteador respondeu: o PC está sem conexão local (Wi-Fi desconectado, cabo solto ou roteador desligado).",
            "Not even the router answered: the PC has no local connection (Wi-Fi disconnected, loose cable or router off)."),
        _ => Loc.T("Não foi possível concluir: faltaram medidas (por exemplo, o roteador não foi encontrado).", "Could not conclude: measurements were missing (for example, the router was not found)."),
    };

    /// <summary>Trechos de nome ou descrição que identificam adaptadores virtuais (Hyper-V, VMware, VirtualBox, VPN, TAP, Wintun, Radmin...).</summary>
    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "vmware", "virtualbox", "virtual adapter", "tap-windows", "tap-", "wintun", "radmin", "wireguard", "zerotier", "tailscale",
        "npcap", "loopback", "pseudo", "vpn", "bluetooth", "wi-fi direct", "teredo", "isatap",
    };

    /// <summary>true se o adaptador é virtual (loopback, túnel, VM, VPN, TAP...), pelo tipo ou pelo nome e descrição.</summary>
    /// <param name="a">Adaptador.</param>
    public static bool IsVirtual(AdapterInfo a)
    {
        if (a.Loopback || a.Tunnel) return true;
        var text = (a.Name + " " + a.Description).ToLowerInvariant();
        return VirtualMarkers.Any(text.Contains);
    }

    /// <summary>Adaptadores físicos (ou ao menos não virtuais) com IPv4 fixo (DHCP desligado), conectados ou não. Virtuais, loopback e túneis são ignorados. Usado para avisar que a redefinição da pilha de rede apagaria o IP fixo.</summary>
    /// <param name="adapters">Adaptadores lidos.</param>
    public static List<AdapterInfo> StaticIpAdapterInfos(IEnumerable<AdapterInfo> adapters) =>
        adapters.Where(a => !IsVirtual(a) && a.HasIpv4 && !a.DhcpEnabled).ToList();

    /// <summary>Nomes dos adaptadores com IP fixo (veja <see cref="StaticIpAdapterInfos"/>).</summary>
    /// <param name="adapters">Adaptadores lidos.</param>
    public static List<string> StaticIpAdapters(IEnumerable<AdapterInfo> adapters) => StaticIpAdapterInfos(adapters).Select(a => a.Name).ToList();

    /// <summary>Linhas "nome (IP, IP)" dos adaptadores com IP fixo, para o aviso de confirmação.</summary>
    /// <param name="adapters">Adaptadores lidos.</param>
    public static List<string> StaticIpDetails(IEnumerable<AdapterInfo> adapters) =>
        StaticIpAdapterInfos(adapters).Select(a => a.Ips is { Count: > 0 } ? $"{a.Name} ({string.Join(", ", a.Ips)})" : a.Name).ToList();

    /// <summary>Lê os adaptadores do sistema (somente leitura). Devolve vazio se a lista não puder ser lida; nunca lança exceção.</summary>
    public static List<AdapterInfo> ReadAdapters() => ReadAdaptersOrNull() ?? new List<AdapterInfo>();

    /// <summary>Lê os adaptadores do sistema, inclusive os desconectados (o IP fixo é lido também do registro, pois a API não o mostra sem mídia). Null se a lista não puder ser lida.</summary>
    public static List<AdapterInfo>? ReadAdaptersOrNull()
    {
        try
        {
            var list = new List<AdapterInfo>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var props = ni.GetIPProperties();
                    var ips = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
                    bool dhcp;
                    try { dhcp = props.GetIPv4Properties().IsDhcpEnabled; } catch (Exception) { dhcp = true; /* sem dado: não afirma IP fixo */ }
                    var key = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + ni.Id;
                    if (Reg.GetInt(key, "EnableDHCP") is { } en)
                    {
                        dhcp = en != 0;
                        if (!dhcp && Reg.Get(key, "IPAddress") is string[] cfg)
                            foreach (var ip in cfg.Where(x => !string.IsNullOrWhiteSpace(x) && x != "0.0.0.0" && !ips.Contains(x))) ips.Add(ip);
                    }
                    list.Add(new AdapterInfo(ni.Name, ni.OperationalStatus == OperationalStatus.Up, ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel, dhcp, ips.Count > 0, ni.Description, ips));
                }
                catch (Exception) { /* adaptador ilegível */ }
            }
            return list;
        }
        catch (Exception) { return null; }
    }
    /// <summary>Adaptador de rede resumido para a escolha da rota de saída.</summary>
    /// <param name="Name">Nome do adaptador.</param>
    /// <param name="Operational">true se está conectado.</param>
    /// <param name="Loopback">true se é loopback.</param>
    /// <param name="Tunnel">true se é túnel.</param>
    /// <param name="LocalIps">Endereços IPv4 do adaptador.</param>
    /// <param name="Gateway">Gateway IPv4 (null se não tem).</param>
    /// <param name="Dns">Servidores DNS configurados.</param>
    public sealed record AdapterRoute(string Name, bool Operational, bool Loopback, bool Tunnel, IReadOnlyList<IPAddress> LocalIps, IPAddress? Gateway, IReadOnlyList<IPAddress> Dns);

    /// <summary>Adaptador escolhido para o teste.</summary>
    /// <param name="AdapterName">Nome do adaptador; null se nenhum foi encontrado.</param>
    /// <param name="Gateway">Gateway IPv4; null se não há.</param>
    /// <param name="Dns">Servidores DNS válidos do adaptador.</param>
    public sealed record NetRoute(string? AdapterName, IPAddress? Gateway, IReadOnlyList<IPAddress> Dns);

    /// <summary>true se o endereço é um DNS de preenchimento do Windows (fec0:0:0:ffff::1 a ::3, faixa fec0:0:0:ffff::/64), que aparece em adaptadores sem DNS.</summary>
    /// <param name="a">Endereço.</param>
    public static bool IsPlaceholderDns(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var b = a.GetAddressBytes();
        return b[0] == 0xfe && b[1] == 0xc0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0xff && b[7] == 0xff;
    }

    /// <summary>Escolhe o adaptador que realmente sai para a internet. Ignora loopback, túnel e desconectados. Se o IP local escolhido pela tabela de rotas é conhecido, vale o adaptador dono dele (mesmo que um adaptador de VPN venha antes na lista); sem isso, o primeiro com gateway IPv4. Os DNS de preenchimento são descartados.</summary>
    /// <param name="adapters">Adaptadores lidos.</param>
    /// <param name="routedLocalIp">IP local que a tabela de rotas usa para sair; null se desconhecido.</param>
    public static NetRoute PickRoute(IEnumerable<AdapterRoute> adapters, IPAddress? routedLocalIp)
    {
        var usable = adapters.Where(a => a.Operational && !a.Loopback && !a.Tunnel).ToList();
        AdapterRoute? pick = null;
        if (routedLocalIp is not null) pick = usable.FirstOrDefault(a => a.LocalIps.Any(ip => ip.Equals(routedLocalIp)));
        pick ??= usable.FirstOrDefault(a => a.Gateway is not null);
        if (pick is null) return new NetRoute(null, null, Array.Empty<IPAddress>());
        return new NetRoute(pick.Name, pick.Gateway, pick.Dns.Where(d => !IsPlaceholderDns(d)).ToList());
    }

    /// <summary>IP local que a tabela de rotas escolhe para chegar a 1.1.1.1:53. Usa um socket UDP "conectado" só para ler o endereço local; nada é enviado. Null em falha.</summary>
    public static IPAddress? RoutedLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse("1.1.1.1"), 53);
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Lê o adaptador de saída para a internet, seu gateway IPv4 e seus DNS. Nunca lança exceção.</summary>
    public static NetRoute ReadRoute()
    {
        var list = new List<AdapterRoute>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var props = ni.GetIPProperties();
                    var ips = props.UnicastAddresses.Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                    var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                    list.Add(new AdapterRoute(ni.Name, ni.OperationalStatus == OperationalStatus.Up, ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel, ips, gw, props.DnsAddresses.ToList()));
                }
                catch (Exception) { /* adaptador ilegível */ }
            }
        }
        catch (Exception) { /* sem acesso à lista */ }
        return PickRoute(list, RoutedLocalIp());
    }
    /// <summary>Envia <see cref="PingCount"/> rodadas de ping aos destinos ao mesmo tempo (um por segundo) e devolve as medidas de cada um. Cancelável; nunca lança exceção.</summary>
    /// <param name="targets">Destinos (nome exibido e endereço).</param>
    /// <param name="progress">Recebe uma linha por rodada concluída.</param>
    /// <param name="ct">Cancelamento.</param>
    public static async Task<List<PingStats>> PingAsync(IReadOnlyList<(string Name, IPAddress Address)> targets, Action<string>? progress, CancellationToken ct)
    {
        var samples = targets.Select(_ => new List<double?>()).ToList();
        for (int round = 0; round < PingCount; round++)
        {
            ct.ThrowIfCancellationRequested();
            var tasks = targets.Select(t => PingOnceAsync(t.Address)).ToList();
            var res = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < res.Length; i++) samples[i].Add(res[i]);
            progress?.Invoke(Loc.T($"Rodada {round + 1}/{PingCount} concluída", $"Round {round + 1}/{PingCount} done"));
            if (round < PingCount - 1) await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return targets.Select((t, i) => Compute(t.Name, samples[i])).ToList();
    }

    /// <summary>Um ping com limite de 2 s; devolve o tempo em ms ou null se perdido ou com falha.</summary>
    /// <param name="address">Destino.</param>
    private static async Task<double?> PingOnceAsync(IPAddress address)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(address, 2000).ConfigureAwait(false);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Texto de uma linha com as medidas de um destino no idioma atual.</summary>
    /// <param name="s">Medidas.</param>
    public static string Describe(PingStats s) => s.Received == 0
        ? Loc.T($"{s.Target}: sem resposta (perda de 100%)", $"{s.Target}: no reply (100% loss)")
        : Loc.T($"{s.Target}: média {Fmt.Num(s.AvgMs ?? 0, 1)} ms, jitter {(s.JitterMs is { } j ? Fmt.Num(j, 1) + " ms" : Fmt.NA)}, perda {Fmt.Num(s.LossPct, 0)}%",
            $"{s.Target}: average {Fmt.Num(s.AvgMs ?? 0, 1)} ms, jitter {(s.JitterMs is { } j2 ? Fmt.Num(j2, 1) + " ms" : Fmt.NA)}, loss {Fmt.Num(s.LossPct, 0)}%");
}
