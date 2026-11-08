using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Log de eventos do Windows (System e Application): erros críticos desde o boot e eventos ligados a engasgos
/// (TDR de vídeo, I/O de disco, WHEA, Kernel-Power 41) nos últimos 7 dias. Fonte: EventLogReader.
/// Parte dos logs pode exigir administrador; falhas de leitura viram notas.
/// </summary>
public sealed class EventsCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "eventos";
    /// <inheritdoc/>
    public string Title => Loc.T("Eventos do Windows", "Windows events");

    private sealed record Ev(string Log, string Provider, int Id, int Level, DateTime Time, Func<string> Message);

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        try { Boot(res); } catch (Exception ex) { res.Note(Loc.T("Eventos desde o boot: ", "Events since boot: ") + ex.Message.Trim()); }
        try { Stability(res); } catch (Exception ex) { res.Note(Loc.T("Eventos de estabilidade: ", "Stability events: ") + ex.Message.Trim()); }
        return res;
    }

    /// <summary>Classifica um evento pelo provedor/ID e devolve (categoria, significado em pt-BR). Categoria "OTHER" e texto vazio quando não reconhecido.</summary>
    /// <param name="provider">Nome do provedor do evento.</param>
    /// <param name="id">ID do evento.</param>
    /// <param name="msg">Mensagem formatada (pode ser vazia).</param>
    public static (string Kind, string Text) Explain(string provider, int id, string msg)
    {
        const RegexOptions I = RegexOptions.IgnoreCase;
        if (provider == "Service Control Manager" && Regex.IsMatch(msg, @"VMSP|Virtualiza|hipervisor|hypervisor|Hyper-V|Nested|vmcompute|WSL|M.quina Virtual", I))
            return ("VIRT", Loc.T("Falha ao iniciar serviço ligado à virtualização (Hyper-V/VM/WSL). Em geral não afeta jogos; costuma vir de ordem de inicialização ou recursos de virtualização incompletos.", "Failure starting a virtualization-related service (Hyper-V/VM/WSL). It usually does not affect games; it often comes from startup order or incomplete virtualization features."));
        if (provider == "Service Control Manager")
            return ("SVC", Loc.T("Um serviço falhou ao iniciar ou foi encerrado. Veja o nome do serviço na mensagem para avaliar a importância.", "A service failed to start or was terminated. Check the service name in the message to judge its importance."));
        if (Regex.IsMatch(provider, "Display|nvlddmkm", I))
            return ("GPU", Loc.T("O driver de vídeo parou de responder e foi reiniciado (TDR). Aponta instabilidade de driver, overclock ou energia.", "The video driver stopped responding and was reset (TDR). Points to driver instability, overclocking or power issues."));
        if (Regex.IsMatch(provider, "^(disk|storahci|stornvme|volmgr)$", I))
            return ("DISK", Loc.T("Problema de I/O em disco (repetição, reset ou erro de leitura/gravação). Pode ser cabo SATA, disco falhando ou driver.", "Disk I/O problem (retry, reset or read/write error). May be a SATA cable, a failing disk or a driver."));
        if (Regex.IsMatch(provider, "WHEA", I))
            return ("HW", Loc.T("Erro de hardware corrigido ou fatal (CPU, memória ou barramento PCIe). Se for frequente, indica instabilidade.", "Corrected or fatal hardware error (CPU, memory or PCIe bus). If frequent, it indicates instability."));
        if (Regex.IsMatch(provider, "Kernel-Power", I) && id == 41)
            return ("POWER", Loc.T("O sistema reiniciou sem desligar corretamente (queda de energia, travamento ou reset).", "The system restarted without shutting down cleanly (power loss, freeze or reset)."));
        if (provider == "DistributedCOM")
            return ("COM", Loc.T("Erro de permissão DCOM. Muito comum e geralmente inofensivo.", "DCOM permission error. Very common and usually harmless."));
        return ("OTHER", "");
    }

    private const int BootCap = 300;
    private const int StabilityCap = 400;

    /// <summary>Texto de contagem: o número exato, ou "N+" quando a leitura atingiu o teto (o total real pode ser maior).</summary>
    /// <param name="count">Quantidade lida.</param>
    /// <param name="capped">true se a leitura parou no teto.</param>
    /// <param name="cap">Teto de leitura.</param>
    internal static string CountText(int count, bool capped, int cap) =>
        capped ? cap.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string XTime(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static List<Ev> Read(string log, string xpath, int max, SectionResult res)
    {
        var list = new List<Ev>();
        try
        {
            var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            EventRecord? rec;
            while (list.Count < max && (rec = reader.ReadEvent()) != null)
            {
                using (rec)
                {
                    var r = rec;
                    string? msg = null;
                    try { msg = r.FormatDescription(); } catch (Exception) { /* provedor sem texto registrado */ }
                    list.Add(new Ev(log, r.ProviderName ?? "", r.Id, r.Level ?? 4, r.TimeCreated ?? DateTime.MinValue, () => msg ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            res.Note(Loc.T($"Log {log}: leitura falhou ({ex.Message.Trim()})", $"Log {log}: read failed ({ex.Message.Trim()})"));
        }
        return list;
    }

    private static string Clean(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > max ? s[..max] + "..." : s;
    }

    private static void Boot(SectionResult res)
    {
        DateTime boot;
        var os = Wmi.First("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        var lb = Fmt.ParseWmiDate(os?.P("LastBootUpTime"));
        if (lb != null) boot = lb.Value;
        else
        {
            boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            res.Note(Loc.T("Hora do boot (Win32_OperatingSystem) indisponível; usado o tempo ligado do processo.", "Boot time (Win32_OperatingSystem) unavailable; process uptime used instead."));
        }

        var xp = $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{XTime(boot)}']]]";
        var all = new List<Ev>();
        bool capped = false;
        foreach (var log in new[] { "System", "Application" })
        {
            var part = Read(log, xp, BootCap, res);
            if (part.Count >= BootCap) capped = true;
            all.AddRange(part);
        }
        if (all.Count > BootCap) capped = true;
        var events = all.OrderByDescending(e => e.Time).Take(BootCap).ToList();
        string total = CountText(events.Count, capped, BootCap);

        var g = res.Group(Loc.T("Erros desde o boot", "Errors since boot"));
        g.Add(Loc.T("Último boot", "Last boot"), Fmt.DateTime(boot));
        if (events.Count == 0)
        {
            g.Add(Loc.T("Erros (nível 1 ou 2)", "Errors (level 1 or 2)"), Loc.T("nenhum desde o último boot", "none since the last boot"));
            res.Find(Severity.Ok, Loc.T("Erros desde o boot", "Errors since boot"), Loc.T("0 erros críticos", "0 critical errors"), id: "eventos.boot_errors");
            return;
        }

        g.Add(Loc.T("Erros (nível 1 ou 2)", "Errors (level 1 or 2)"), total);
        if (capped) g.Notes.Add(Loc.T($"A leitura para em {BootCap} eventos por log; o total real pode ser maior.", $"Reading stops at {BootCap} events per log; the real total may be higher."));
        var tb = g.Table(Loc.T("Erros agrupados (12 mais frequentes)", "Grouped errors (12 most frequent)"), Loc.T("Qtd", "Count"), Loc.T("Registro", "Log"), Loc.T("Fonte", "Source"), "ID", Loc.T("Último", "Last"), Loc.T("Mensagem", "Message"), Loc.T("Significado", "Meaning"));
        int virt = 0, other = 0;
        var groups = events.GroupBy(e => e.Provider + "|" + e.Id).OrderByDescending(x => x.Count()).Take(12);
        foreach (var grp in groups)
        {
            var e = grp.First();
            var msg = Clean(e.Message(), 150);
            var x = Explain(e.Provider, e.Id, msg);
            tb.AddRow(grp.Count(), e.Log, e.Provider, e.Id, e.Time.ToString("HH:mm:ss"), msg, x.Text);
            if (x.Kind == "VIRT") virt += grp.Count(); else other += grp.Count();
        }
        if (other == 0)
            res.Find(Severity.Info, Loc.T("Erros desde o boot", "Errors since boot"), Loc.T($"{virt} erro(s) de serviços de virtualização", $"{virt} virtualization service error(s)"),
                Loc.T("São falhas de inicialização de serviços da pilha de virtualização. Não costumam afetar jogos.", "These are startup failures of virtualization stack services. They do not usually affect games."), id: "eventos.boot_errors");
        else
            res.Find(Severity.Aviso, Loc.T("Erros desde o boot", "Errors since boot"), Loc.T($"{total} evento(s) de erro; veja a lista acima", $"{total} error event(s); see the list above"),
                Loc.T("Erros registrados desde que o PC ligou. A fonte e o ID acima indicam o componente.", "Errors logged since the PC was turned on. The source and ID above indicate the component."),
                Loc.T("Pesquise o ID do evento junto com a fonte para detalhes.", "Search for the event ID together with the source for details."), id: "eventos.boot_errors",
                simple: Loc.T("O Windows registrou erros desde que o PC ligou. Nem todo erro é grave; a lista mostra de onde cada um veio.", "Windows logged errors since the PC was turned on. Not every error is serious; the list shows where each one came from."));
    }

    private static void Stability(SectionResult res)
    {
        var since = DateTime.Now.AddDays(-7);
        string[] provs = { "Display", "nvlddmkm", "disk", "storahci", "stornvme", "volmgr", "Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-Power" };
        var pc = string.Join(" or ", provs.Select(p => $"Provider[@Name='{p}']"));
        var xp = $"*[System[({pc}) and TimeCreated[@SystemTime>='{XTime(since)}']]]";
        var raw = Read("System", xp, StabilityCap, res);
        bool capped = raw.Count >= StabilityCap;

        var ev = raw.Where(e =>
            (Regex.IsMatch(e.Provider, "Display|nvlddmkm", RegexOptions.IgnoreCase) && e.Id == 4101) ||
            (Regex.IsMatch(e.Provider, "^(disk|storahci|stornvme|volmgr)$", RegexOptions.IgnoreCase) && e.Level <= 3) ||
            Regex.IsMatch(e.Provider, "WHEA", RegexOptions.IgnoreCase) ||
            (Regex.IsMatch(e.Provider, "Kernel-Power", RegexOptions.IgnoreCase) && e.Id == 41)).ToList();

        var g = res.Group(Loc.T("Eventos ligados a engasgos (7 dias)", "Events related to stutters (7 days)"));
        if (ev.Count == 0)
        {
            g.Add(Loc.T("Resultado", "Result"), Loc.T("nenhum evento de TDR de vídeo, I/O de disco, WHEA ou desligamento inesperado", "no video TDR, disk I/O, WHEA or unexpected shutdown events"));
            res.Find(Severity.Ok, Loc.T("Eventos de estabilidade (7 dias)", "Stability events (7 days)"), Loc.T("Sem TDR, erros de disco, WHEA ou Kernel-Power 41", "No TDR, disk errors, WHEA or Kernel-Power 41"), id: "eventos.stability");
            return;
        }

        var tb = g.Table(Loc.T("Eventos agrupados", "Grouped events"), Loc.T("Qtd", "Count"), Loc.T("Fonte", "Source"), "ID", Loc.T("Último", "Last"), Loc.T("Significado", "Meaning"));
        foreach (var grp in ev.GroupBy(e => e.Provider + "|" + e.Id).OrderByDescending(x => x.Count()))
        {
            var e = grp.First();
            tb.AddRow(grp.Count(), e.Provider, e.Id, e.Time.ToString(Loc.IsPt ? "dd/MM HH:mm" : "MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), Explain(e.Provider, e.Id, "").Text);
        }
        var cnt = capped ? $"{StabilityCap}+" : ev.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        res.Find(Severity.Aviso, Loc.T("Eventos de estabilidade (7 dias)", "Stability events (7 days)"), Loc.T($"{cnt} evento(s) relacionados a driver de vídeo, disco ou hardware", $"{cnt} event(s) related to the video driver, disk or hardware"),
            Loc.T("Esses eventos estão ligados a travadas e instabilidade. Veja o significado de cada grupo acima.", "These events are linked to freezes and instability. See the meaning of each group above."),
            Loc.T("Se houver TDR de vídeo, atualize/reinstale o driver e verifique energia e temperatura. Se houver erro de disco, verifique cabo SATA e S.M.A.R.T.", "If there is a video TDR, update/reinstall the driver and check power and temperature. If there is a disk error, check the SATA cable and S.M.A.R.T."), id: "eventos.stability",
            simple: Loc.T("O Windows registrou travas do driver de vídeo, erros de disco ou desligamentos inesperados na última semana. Vale conferir driver, cabos e temperatura.", "Windows logged video driver hangs, disk errors or unexpected shutdowns in the last week. It is worth checking the driver, cables and temperature."));
    }
}
