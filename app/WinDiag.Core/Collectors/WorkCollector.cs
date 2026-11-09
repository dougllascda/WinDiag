using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Util;
using WinDiag.Core.Work;

namespace WinDiag.Core.Collectors;

/// <summary>Dados auxiliares das regras do perfil Trabalho que não ficam em <see cref="SystemFacts"/> (lidos pelo coletor e injetáveis em testes).</summary>
/// <param name="InstalledRamGB">RAM instalada (soma dos pentes) em GB; null = usa a RAM visível ao Windows.</param>
/// <param name="SystemDriveIsHdd">true = a unidade do Windows está em HDD; false = não; null = não determinado.</param>
/// <param name="SpoolerDisabled">true se o serviço Spooler está configurado como Desativado (escolha deliberada).</param>
/// <param name="Now">Data de referência para "dias desde a última atualização" (null = agora).</param>
public sealed record WorkChecks(double? InstalledRamGB = null, bool? SystemDriveIsHdd = null, bool SpoolerDisabled = false, DateTime? Now = null);

/// <summary>
/// Perfil Trabalho: impressoras e Spooler, programas de fábrica, Windows Update (reinício pendente e última atualização), bateria, plano de energia e tempo de inicialização.
/// Fontes: WMI (Win32_Printer, Win32_PrintJob, Win32_Service, Win32_Battery, Win32_QuickFixEngineering, Win32_PhysicalMemory), registro (Uninstall, pacotes Appx, RebootRequired),
/// powercfg (/getactivescheme e /batteryreport /xml) e o log Microsoft-Windows-Diagnostics-Performance/Operational (evento 100). Somente leitura;
/// o relatório de bateria é gravado numa pasta temporária e apagado. Leituras que falham viram nota e o campo mostra "n/d".
/// Todos os achados têm escopo <see cref="Scope.Work"/>.
/// </summary>
public sealed class WorkCollector : ICollector
{
    /// <summary>Chave do registro (relativa a HKLM/HKCU) onde os programas instalados registram nome, editor, versão e desinstalador.</summary>
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    /// <summary>Chave (em HKCU) do repositório de pacotes Appx/MSIX; os nomes dos subitens são os PackageFullName dos apps da Loja instalados.</summary>
    private const string AppxRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    /// <summary>Minutos na fila a partir dos quais um trabalho de impressão é considerado preso. Limiar prático do WinDiag.</summary>
    private const int StuckMinutes = 10;

    /// <inheritdoc/>
    public string Key => "trabalho";
    /// <inheritdoc/>
    public string Title => Loc.T("Trabalho e escritório", "Work and office");

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        var extras = new WorkChecks(Now: DateTime.Now);
        bool spoolerDisabled = false;

        try { spoolerDisabled = ReadPrinters(res, facts); } catch (Exception ex) { res.Note(Loc.T("Impressoras: ", "Printers: ") + ex.Message.Trim()); }
        try { ReadBloat(res, facts); } catch (Exception ex) { res.Note(Loc.T("Programas de fábrica: ", "Factory programs: ") + ex.Message.Trim()); }
        try { ReadUpdates(res, facts); } catch (Exception ex) { res.Note(Loc.T("Windows Update: ", "Windows Update: ") + ex.Message.Trim()); }
        try { ReadBattery(res, facts); } catch (Exception ex) { res.Note(Loc.T("Bateria: ", "Battery: ") + ex.Message.Trim()); }
        try { ReadPowerPlan(res, facts); } catch (Exception ex) { res.Note(Loc.T("Plano de energia: ", "Power plan: ") + ex.Message.Trim()); }
        try { ReadBoot(res, facts); } catch (Exception ex) { res.Note(Loc.T("Tempo de inicialização: ", "Boot time: ") + ex.Message.Trim()); }

        double? ram = null; bool? hdd = null;
        try { ram = InstalledRamGB(res); } catch (Exception ex) { res.Note(Loc.T("RAM instalada: ", "Installed RAM: ") + ex.Message.Trim()); }
        try { hdd = SystemDriveIsHdd(res); } catch (Exception ex) { res.Note(Loc.T("Tipo do disco do Windows: ", "Windows disk type: ") + ex.Message.Trim()); }

        try { AddFindings(res, facts, extras with { InstalledRamGB = ram, SystemDriveIsHdd = hdd, SpoolerDisabled = spoolerDisabled }); }
        catch (Exception ex) { res.Note(Loc.T("Verificações de trabalho: ", "Work checks: ") + ex.Message.Trim()); }
        return res;
    }

    // ------------------------------------------------------------------ impressoras

    /// <summary>Lê impressoras, fila e Spooler. Devolve true se o Spooler está configurado como Desativado.</summary>
    private static bool ReadPrinters(SectionResult res, SystemFacts facts)
    {
        void Err(string s) => res.Note(s);
        bool disabled = false;
        var g = res.Group(Loc.T("Impressoras", "Printers"));

        var svc = Wmi.First("SELECT State, StartMode FROM Win32_Service WHERE Name='Spooler'", onError: Err);
        if (svc is not null)
        {
            facts.SpoolerRunning = string.Equals(svc.S("State"), "Running", StringComparison.OrdinalIgnoreCase);
            disabled = string.Equals(svc.S("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase);
            facts.SpoolerDisabled = disabled;
            svc.Dispose();
        }
        g.Add(Loc.T("Serviço Spooler", "Spooler service"), facts.SpoolerRunning is null ? null : facts.SpoolerRunning.Value ? Loc.T("em execução", "running") : disabled ? Loc.T("desativado", "disabled") : Loc.T("parado", "stopped"),
            facts.SpoolerRunning == false && !disabled ? Severity.Atencao : null);

        var printers = new List<PrinterInfo>();
        var pq = Wmi.Query("SELECT Name, Default, Network, PrinterStatus, DetectedErrorState, WorkOffline, ExtendedPrinterStatus FROM Win32_Printer", onError: Err);
        var jobs = Wmi.Query("SELECT Name, TimeSubmitted, JobStatus, StatusMask FROM Win32_PrintJob", onError: Err);
        try
        {
            var now = DateTime.Now;
            var jobsByPrinter = new Dictionary<string, (int Total, int Stuck)>(StringComparer.OrdinalIgnoreCase);
            var parsedJobs = new List<(string Printer, DateTime? When, long? Mask, string? Status)>();
            foreach (var j in jobs)
            {
                var jn = j.S("Name") ?? "";
                int comma = jn.LastIndexOf(',');
                var pn = comma > 0 ? jn[..comma].Trim() : jn;
                parsedJobs.Add((pn, ParseJobTime(j.P("TimeSubmitted"), now), j.L("StatusMask"), j.S("JobStatus")));
            }
            var progressing = new HashSet<string>(parsedJobs.Where(x => JobIsActive(x.Mask, x.Status)).Select(x => x.Printer), StringComparer.OrdinalIgnoreCase);
            foreach (var (pn, when, mask, status) in parsedJobs)
            {
                bool stuck = IsJobStuck(when, now, mask, status, progressing.Contains(pn));
                jobsByPrinter.TryGetValue(pn, out var cur);
                jobsByPrinter[pn] = (cur.Total + 1, cur.Stuck + (stuck ? 1 : 0));
            }            foreach (var p in pq)
            {
                var name = p.S("Name") ?? "";
                if (name.Length == 0) continue;
                long? ps = p.L("PrinterStatus"), de = p.L("DetectedErrorState"), ext = p.L("ExtendedPrinterStatus");
                bool workOffline = p.B("WorkOffline") == true;
                bool offline = workOffline || ps == 7 || de == 9 || ext == 7;
                bool error = ps == 6 || de is 4 or 6 or 7 or 8 or 10 or 11;
                jobsByPrinter.TryGetValue(name, out var jc);
                printers.Add(new PrinterInfo
                {
                    Name = name,
                    IsDefault = p.B("Default") == true,
                    IsNetwork = p.B("Network") == true,
                    Offline = offline,
                    HasError = error,
                    StatusText = PrinterStatusText(ps, de, workOffline),
                    JobCount = jc.Total,
                    StuckJobs = jc.Stuck,
                });
            }
        }
        finally { Wmi.DisposeAll(pq); Wmi.DisposeAll(jobs); }
        facts.Printers = printers;

        if (printers.Count == 0) g.Notes.Add(Loc.T("Nenhuma impressora instalada foi lida.", "No installed printer was read."));
        else
        {
            var t = g.Table(Loc.T("Impressoras instaladas", "Installed printers"), Loc.T("Nome", "Name"), Loc.T("Padrão", "Default"), Loc.T("Rede", "Network"), Loc.T("Estado", "Status"), Loc.T("Trabalhos", "Jobs"), Loc.T("Presos (> 10 min)", "Stuck (> 10 min)"));
            foreach (var p in printers.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                t.AddRow(p.Name, p.IsDefault, p.IsNetwork, p.StatusText, p.JobCount, p.StuckJobs);
        }
        return disabled;
    }

    /// <summary>
    /// Interpreta o TimeSubmitted de um trabalho de impressão (formato WMI <c>yyyyMMddHHmmss.ffffff±zzz</c>) como hora local, com cuidado de fuso:
    /// o deslocamento do texto é aplicado (um <c>+000</c> é lido como UTC) e o resultado é convertido para a hora local. Datas no futuro (tolerância de 5 min)
    /// ou com mais de 7 dias são tratadas como não confiáveis e devolvem null, ou seja, o trabalho não é considerado preso por esse critério.
    /// </summary>
    /// <param name="raw">Valor lido do WMI (texto, ou DateTime já convertido, tratado como hora local).</param>
    /// <param name="now">Hora local de referência.</param>
    public static DateTime? ParseJobTime(object? raw, DateTime now)
    {
        DateTime? local = null;
        if (raw is DateTime dt) local = dt;
        else if (raw?.ToString() is { } s && Regex.Match(s.Trim(), @"^(\d{14})\.(\d{6})([+-]\d{3})$") is { Success: true } m)
        {
            try
            {
                var wall = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
                int offsetMin = int.Parse(m.Groups[3].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                local = DateTime.SpecifyKind(wall.AddMinutes(-offsetMin), DateTimeKind.Utc).ToLocalTime();
            }
            catch (Exception) { return null; }
        }
        if (local is null) return null;
        var age = now - local.Value;
        if (age < TimeSpan.FromMinutes(-5) || age > TimeSpan.FromDays(7)) return null;
        return local;
    }

    /// <summary>true se o trabalho está sendo impresso ou enviado ao spool (bits Spooling 0x8 e Printing 0x10 de StatusMask, ou esses termos em JobStatus), ou seja, a fila anda.</summary>
    /// <param name="mask">StatusMask do Win32_PrintJob.</param>
    /// <param name="status">JobStatus (texto).</param>
    public static bool JobIsActive(long? mask, string? status) =>
        (mask is { } m && (m & 0x18) != 0) || (status is { Length: > 0 } s && Regex.IsMatch(s, @"printing|spooling|imprimindo", RegexOptions.IgnoreCase));

    /// <summary>true se o status do trabalho indica pausa ou erro: bits Paused 0x1, Error 0x2, Offline 0x20, Paperout 0x40, Blocked 0x200 e User_Intervention 0x400 de StatusMask, ou termos equivalentes em JobStatus.</summary>
    /// <param name="mask">StatusMask do Win32_PrintJob.</param>
    /// <param name="status">JobStatus (texto).</param>
    public static bool JobHasProblem(long? mask, string? status) =>
        (mask is { } m && (m & 0x663) != 0) || (status is { Length: > 0 } s && Regex.IsMatch(s, @"paus|error|erro|offline|paper|blocked|intervention", RegexOptions.IgnoreCase));

    /// <summary>
    /// Heurística de trabalho preso: o trabalho foi enviado há mais de <see cref="StuckMinutes"/> min (data confiável, veja <see cref="ParseJobTime"/>)
    /// E (o status indica erro ou pausa OU a fila da impressora não andou, isto é, nenhum trabalho dela está sendo impresso ou em spool).
    /// Um trabalho aguardando atrás de outro que imprime normalmente não conta. Sem data confiável nunca é considerado preso.
    /// </summary>
    /// <param name="submitted">Hora local de envio (null se não confiável).</param>
    /// <param name="now">Hora local de referência.</param>
    /// <param name="mask">StatusMask do trabalho.</param>
    /// <param name="status">JobStatus do trabalho.</param>
    /// <param name="queueProgressing">true se algum trabalho da mesma impressora está imprimindo ou em spool.</param>
    public static bool IsJobStuck(DateTime? submitted, DateTime now, long? mask, string? status, bool queueProgressing) =>
        submitted is { } t && (now - t).TotalMinutes > StuckMinutes && (JobHasProblem(mask, status) || !queueProgressing);
    /// <summary>Texto de estado da impressora a partir de PrinterStatus (3 pronta, 4 imprimindo, 5 aquecendo, 6 parada, 7 offline) e DetectedErrorState (4 sem papel, 6 sem toner, 7 tampa aberta, 8 atolamento etc.), conforme Win32_Printer. WorkOffline tem prioridade.</summary>
    private static string PrinterStatusText(long? status, long? detected, bool workOffline)
    {
        if (workOffline || status == 7 || detected == 9) return Loc.T("offline", "offline");
        string s = status switch
        {
            3 => Loc.T("pronta", "ready"),
            4 => Loc.T("imprimindo", "printing"),
            5 => Loc.T("aquecendo", "warming up"),
            6 => Loc.T("parada", "stopped"),
            1 or 2 => Loc.T("sem informação", "no information"),
            _ => Fmt.NA
        };
        string d = detected switch
        {
            4 => Loc.T("sem papel", "out of paper"),
            6 => Loc.T("sem toner", "out of toner"),
            7 => Loc.T("tampa aberta", "door open"),
            8 => Loc.T("papel atolado", "paper jam"),
            10 => Loc.T("precisa de manutenção", "service requested"),
            11 => Loc.T("bandeja de saída cheia", "output bin full"),
            _ => ""
        };
        return d.Length > 0 ? s + " - " + d : s;
    }

    // ------------------------------------------------------------------ programas de fábrica

    /// <summary>Procura programas de fábrica no registro (Uninstall de 64 bits, 32 bits e do usuário) e em pacotes Appx, classificando-os pela lista do WinDiag (<c>BloatwareDb</c>). Só lista e abre o desinstalador oficial; nada é removido aqui.</summary>
    private static void ReadBloat(SectionResult res, SystemFacts facts)
    {
        var g = res.Group(Loc.T("Programas de fábrica", "Factory programs"));
        var found = new List<BloatItem>();
        int scanned = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string name, string publisher, string version, string uninstall, bool appx, string full, bool userHive = false)
        {
            scanned++;
            if (!seen.Add(name + "|" + appx)) return;
            var c = BloatwareDb.Classify(name, publisher);
            if (c is null) return;
            found.Add(new BloatItem { Name = name, Publisher = publisher, Version = version, Verdict = c.Value.Verdict, Reason = c.Value.Reason, UninstallCommand = uninstall, IsAppx = appx, PackageFullName = full, FromUserHive = userHive });
        }

        foreach (var (root, sub, userHive) in new[] { (Registry.LocalMachine, UninstallKey, false), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false), (Registry.CurrentUser, UninstallKey, true) })
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                foreach (var sk in k.GetSubKeyNames())
                {
                    try
                    {
                        using var e = k.OpenSubKey(sk);
                        if (e is null) continue;
                        var name = (e.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (Convert.ToInt32(e.GetValue("SystemComponent") ?? 0, CultureInfo.InvariantCulture) == 1) continue;
                        if (e.GetValue("ParentKeyName") is string) continue; // atualização de outro produto
                        Consider(name, (e.GetValue("Publisher") as string)?.Trim() ?? "", (e.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                            (e.GetValue("UninstallString") as string)?.Trim() ?? "", false, "", userHive);
                    }
                    catch (Exception) { /* entrada ilegível: ignora */ }
                }
            }
            catch (Exception ex) { res.Note(Loc.T("Programas instalados (registro): ", "Installed programs (registry): ") + ex.Message.Trim()); }
        }

        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AppxRepository);
            if (k is null) res.Note(Loc.T("Pacotes da Loja não lidos", "Store packages not read"));
            else
                foreach (var full in k.GetSubKeyNames())
                {
                    var (display, pub) = AppxNames(full);
                    if (display.Length == 0) continue;
                    // classifica pelo identificador completo e pelo último trecho ("king.com.CandyCrushSaga" -> "CandyCrushSaga").
                    var id = full.Split('_')[0];
                    var tail = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
                    string? use = BloatwareDb.Classify(id, pub) is not null ? id : BloatwareDb.Classify(tail, pub) is not null ? tail : null;
                    if (use is null) { scanned++; continue; }
                    Consider(use, pub, "", "", true, full);
                }
        }
        catch (Exception ex) { res.Note(Loc.T("Pacotes da Loja: ", "Store packages: ") + ex.Message.Trim()); }

        found = found.OrderBy(x => x.Verdict).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        facts.Bloat = found;
        g.Add(Loc.T("Programas analisados", "Programs analysed"), scanned);
        g.Add(Loc.T("Reconhecidos pela lista", "Recognised by the list"), found.Count);
        g.Add(Loc.T("Sugeridos para remover", "Suggested for removal"), found.Count(x => x.Verdict == BloatVerdict.Remove));
        if (found.Count > 0)
        {
            var t = g.Table(Loc.T("Programas classificados", "Classified programs"), Loc.T("Nome", "Name"), Loc.T("Fabricante", "Publisher"), Loc.T("Recomendação", "Recommendation"), Loc.T("Motivo", "Reason"));
            foreach (var b in found) t.AddRow(b.Name, b.Publisher, BloatwareDb.Label(b.Verdict), b.Reason);
        }
        g.Notes.Add(Loc.T("Só aparecem programas que a lista do WinDiag reconhece. O WinDiag não remove nada sozinho: abre o desinstalador oficial.",
            "Only programs that the WinDiag list recognises appear. WinDiag removes nothing by itself: it opens the official uninstaller."));
    }

    /// <summary>Deduz nome e editor aproximados do PackageFullName ("Editor.App_versão_arq__hash"): o nome é o trecho antes do primeiro "_" e o editor o trecho antes do primeiro ponto.</summary>
    private static (string Display, string Publisher) AppxNames(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        var id = parts[0];
        if (id.Length == 0) return ("", "");
        var pub = id.Contains('.') ? id[..id.IndexOf('.')] : "";
        return (id, pub);
    }

    // ------------------------------------------------------------------ Windows Update

    /// <summary>Estado do Windows Update: reinício pendente (chaves RebootRequired do Windows Update e RebootPending do CBS) e data da última correção instalada (Win32_QuickFixEngineering.InstalledOn, confirmada pelo evento 19 do log WindowsUpdateClient/Operational; vale a mais recente das duas).</summary>
    private static void ReadUpdates(SectionResult res, SystemFacts facts)
    {
        var g = res.Group("Windows Update");
        bool wuReboot = Reg.Open(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } a && Dispose(a);
        bool cbsReboot = Reg.Open(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } b && Dispose(b);
        facts.RebootPending = wuReboot || cbsReboot;

        DateTime? last = null;
        var qfe = Wmi.Query("SELECT InstalledOn FROM Win32_QuickFixEngineering", onError: s => res.Note(s));
        try
        {
            foreach (var q in qfe)
                if (ParseInstalledOn(q.S("InstalledOn")) is { } d && (last is null || d > last)) last = d;
        }
        finally { Wmi.DisposeAll(qfe); }
        // Win32_QuickFixEngineering não lista todas as atualizações: confirma pelo histórico do Windows Update (evento 19 = instalação bem-sucedida).
        var hist = ReadLastUpdateEvent(res);
        facts.LastUpdateUnconfirmed = hist is null;
        if (hist is { } h && (last is null || h > last)) last = h;
        facts.LastUpdateInstalled = last;
        if (last is null) res.Note(Loc.T("Data da última atualização não lida (Win32_QuickFixEngineering)", "Last update date not read (Win32_QuickFixEngineering)"));

        g.Add(Loc.T("Última atualização instalada", "Last update installed"), Fmt.Date(last));
        g.Add(Loc.T("Reinício pendente", "Pending restart"), facts.RebootPending is null ? null : facts.RebootPending.Value ? Loc.T("sim", "yes") : Loc.T("não", "no"),
            facts.RebootPending == true ? Severity.Aviso : null);
    }

    /// <summary>Data do evento 19 (instalação bem-sucedida) mais recente do log Microsoft-Windows-WindowsUpdateClient/Operational. Null se o log não pôde ser lido (anota o motivo) ou não há evento.</summary>
    /// <param name="res">Seção que recebe a nota de falha.</param>
    private static DateTime? ReadLastUpdateEvent(SectionResult res)
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-WindowsUpdateClient/Operational", PathType.LogName, "*[System[(EventID=19)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var ev = reader.ReadEvent();
            if (ev?.TimeCreated is { } t) return t;
            res.Note(Loc.T("Histórico do Windows Update: nenhuma instalação bem-sucedida registrada no log", "Windows Update history: no successful installation recorded in the log"));
        }
        catch (UnauthorizedAccessException) { res.Note(Loc.T("Histórico do Windows Update: exige administrador", "Windows Update history: requires administrator")); }
        catch (Exception ex) { res.Note(Loc.T("Histórico do Windows Update: ", "Windows Update history: ") + ex.Message.Trim()); }
        return null;
    }

    /// <summary>Libera a chave aberta e devolve true, para permitir usar a verificação de existência da chave dentro de uma expressão.</summary>
    private static bool Dispose(RegistryKey k) { k.Dispose(); return true; }

    /// <summary>Interpreta o InstalledOn do Win32_QuickFixEngineering: texto M/d/yyyy (ou na cultura local) ou FILETIME hexadecimal ("0x01d..."). Null se não reconhecido.</summary>
    /// <param name="text">Valor lido do WMI.</param>
    public static DateTime? ParseInstalledOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
                return DateTime.FromFileTime(ft);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        // O WMI devolve a data no formato M/d/yyyy (invariante) mesmo no Windows em pt-BR; as demais culturas ficam de reserva.
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("pt-BR"), CultureInfo.CurrentCulture })
            if (DateTime.TryParse(t, culture, DateTimeStyles.None, out var d) && d.Year >= 1995) return d;
        return null;
    }

    // ------------------------------------------------------------------ bateria e energia

    /// <summary>Estado e carga da bateria (Win32_Battery) e, quando disponível, capacidade de projeto, carga completa atual e desgaste, lidos do relatório do powercfg (<c>BatteryReport</c>, XML temporário apagado ao final). Desgaste a partir de 40% é destacado como aviso e a partir de 60% como atenção (limiares práticos do WinDiag, não de fabricante).</summary>
    private static void ReadBattery(SectionResult res, SystemFacts facts)
    {
        var g = res.Group(Loc.T("Saúde da bateria", "Battery health"));
        var bats = Wmi.Query("SELECT BatteryStatus, EstimatedChargeRemaining FROM Win32_Battery", onError: s => res.Note(s));
        facts.HasBattery = bats.Count > 0;
        long? status = bats.Count > 0 ? bats[0].L("BatteryStatus") : null;
        long? charge = bats.Count > 0 ? bats[0].L("EstimatedChargeRemaining") : null;
        Wmi.DisposeAll(bats);
        facts.OnAcPower = facts.HasBattery ? OnAc(status) : null;

        g.Add(Loc.T("Bateria", "Battery"), facts.HasBattery ? Loc.T("detectada", "detected") : Loc.T("nenhuma (desktop ou não detectada)", "none (desktop or not detected)"));
        if (!facts.HasBattery) return;
        g.Add(Loc.T("Carga atual", "Current charge"), charge is null ? null : charge + "%");
        g.Add(Loc.T("Fonte de energia", "Power source"), facts.OnAcPower is null ? null : facts.OnAcPower.Value ? Loc.T("na tomada", "plugged in") : Loc.T("na bateria", "on battery"));

        var cap = BatteryReport.Read(out var err);
        if (cap is null) res.Note(err ?? Loc.T("relatório de bateria indisponível", "battery report unavailable"));
        else
        {
            if (cap.Partial && err is not null) res.Note(err);
            facts.BatteryDesignMWh = cap.DesignMWh;
            facts.BatteryFullMWh = cap.FullChargeMWh;
            facts.BatteryWearPct = cap.WearPct;
        }
        g.Add(Loc.T("Capacidade de projeto", "Design capacity"), facts.BatteryDesignMWh is null ? null : Fmt.Num(facts.BatteryDesignMWh.Value, 0) + " mWh");
        g.Add(Loc.T("Carga completa atual", "Current full charge"), facts.BatteryFullMWh is null ? null : Fmt.Num(facts.BatteryFullMWh.Value, 0) + " mWh");
        g.Add(Loc.T("Desgaste", "Wear"), facts.BatteryWearPct is null ? null : Fmt.Num(facts.BatteryWearPct.Value, 1) + "%",
            facts.BatteryWearPct is >= 60 ? Severity.Atencao : facts.BatteryWearPct is >= 40 ? Severity.Aviso : null);
    }

    /// <summary>true = na tomada; false = descarregando; null = indefinido. Usa o BatteryStatus do Win32_Battery (1 descarregando; 4 e 5 baixa/crítica; 2, 3, 6 a 9 e 11 na tomada).</summary>
    /// <param name="batteryStatus">Valor de BatteryStatus.</param>
    public static bool? OnAc(long? batteryStatus) => batteryStatus switch
    {
        1 or 4 or 5 => false,
        2 or 3 or 6 or 7 or 8 or 9 or 11 => true,
        _ => null
    };

    /// <summary>Plano de energia ativo (powercfg /getactivescheme, nome entre parênteses) e o plano recomendado para trabalho conforme o tipo de máquina.</summary>
    private static void ReadPowerPlan(SectionResult res, SystemFacts facts)
    {
        var r = Proc.Run(Proc.Sys32("powercfg.exe"), "/getactivescheme", 10000);
        facts.ActivePowerPlanGuid = PowerCfg.ParseSchemeGuid(r.StdOut);
        var raw = r.StdOut.Replace("\r", " ").Replace("\n", " ").Trim();
        var m = Regex.Match(raw, @"\((.+)\)");
        facts.ActivePowerPlanName = m.Success ? m.Groups[1].Value : null;
        if (facts.ActivePowerPlanGuid is null) res.Note(Loc.T("powercfg: plano ativo não lido", "powercfg: active plan not read"));
        var list = Proc.Run(Proc.Sys32("powercfg.exe"), "/list", 10000);
        facts.PowerPlanGuids = PowerCfg.ParseSchemeGuids(list.StdOut);
        if (facts.PowerPlanGuids.Count == 0) res.Note(Loc.T("powercfg: lista de planos não lida (nenhuma troca de plano será sugerida)", "powercfg: plan list not read (no plan switch will be suggested)"));

        var g = res.Group(Loc.T("Plano de energia", "Power plan"));
        g.Add(Loc.T("Plano ativo", "Active plan"), facts.ActivePowerPlanName);
        g.Add("GUID", facts.ActivePowerPlanGuid);
        var rec = FixCatalog.WorkPlanGuid(facts);
        g.Add(Loc.T("Recomendado para trabalho", "Recommended for work"), rec == FixCatalog.BalancedPlanGuid ? Loc.T("Equilibrado (notebook)", "Balanced (laptop)") : Loc.T("Alto desempenho (desktop)", "High performance (desktop)"));
    }

    // ------------------------------------------------------------------ inicialização

    /// <summary>Último tempo de boot medido pelo Windows (log Microsoft-Windows-Diagnostics-Performance/Operational, evento 100, campo BootTime em ms). O log pode exigir administrador; acima de 60 s o campo é destacado como aviso (limiar prático do WinDiag).</summary>
    private static void ReadBoot(SectionResult res, SystemFacts facts)
    {
        var g = res.Group(Loc.T("Tempo de inicialização", "Boot time"));
        DateTime? when = null;
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var ev = reader.ReadEvent();
            if (ev is not null)
            {
                facts.BootTimeMs = ParseBootTimeMs(ev.ToXml());
                when = ev.TimeCreated;
            }
            else res.Note(Loc.T("Tempo de inicialização: nenhum evento de boot registrado", "Boot time: no boot event recorded"));
        }
        catch (UnauthorizedAccessException) { res.Note(Loc.T("Tempo de inicialização: exige administrador", "Boot time: requires administrator")); }
        catch (Exception ex) { res.Note(Loc.T("Tempo de inicialização (log de desempenho): ", "Boot time (performance log): ") + ex.Message.Trim()); }
        g.Add(Loc.T("Último boot medido", "Last boot measured"), facts.BootTimeMs is null ? null : Fmt.Num(facts.BootTimeMs.Value / 1000d, 1) + " s",
            facts.BootTimeMs > 60000 ? Severity.Aviso : null);
        g.Add(Loc.T("Medido em", "Measured on"), Fmt.DateTime(when));
    }

    /// <summary>Extrai o valor BootTime (ms) do XML do evento 100 do Diagnostics-Performance. Null se ausente.</summary>
    /// <param name="eventXml">XML do evento.</param>
    public static int? ParseBootTimeMs(string? eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml)) return null;
        try
        {
            var doc = XDocument.Parse(eventXml);
            var d = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == "BootTime");
            return d is not null && int.TryParse(d.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : null;
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ RAM e disco do sistema

    /// <summary>RAM instalada em GB (soma de Win32_PhysicalMemory.Capacity), diferente da RAM visível ao Windows, que desconta a reservada pelo hardware.</summary>
    private static double? InstalledRamGB(SectionResult res)
    {
        var mem = Wmi.Query("SELECT Capacity FROM Win32_PhysicalMemory", onError: s => res.Note(s));
        try
        {
            double total = mem.Sum(m => m.D("Capacity") ?? 0);
            return total > 0 ? Math.Round(total / 1073741824d, 1) : null;
        }
        finally { Wmi.DisposeAll(mem); }
    }

    /// <summary>Descobre se a unidade do Windows está em HDD: letra para número do disco (MSFT_Partition) e depois tipo de mídia (MSFT_PhysicalDisk: 3 = HDD, 4 = SSD, 5 = SCM; sem tipo, rotação válida indica HDD). Null se não for possível determinar.</summary>
    private static bool? SystemDriveIsHdd(SectionResult res)
    {
        const string ns = @"root\Microsoft\Windows\Storage";
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(sysRoot)) return null;
        char letter = char.ToUpperInvariant(sysRoot[0]);
        long? diskNumber = null;
        foreach (var o in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", ns, s => res.Note(s)))
        {
            using (o)
            {
                var dl = o.P("DriveLetter");
                char c = dl is char ch ? ch : dl is not null && dl.ToString() is { Length: > 0 } s ? s[0] : '\0';
                if (char.ToUpperInvariant(c) == letter) diskNumber = o.L("DiskNumber");
            }
        }
        if (diskNumber is null) return null;
        var disks = Wmi.Query($"SELECT MediaType, SpindleSpeed FROM MSFT_PhysicalDisk WHERE DeviceId='{diskNumber.Value}'", ns, s => res.Note(s));
        try
        {
            if (disks.Count == 0) return null;
            long? media = disks[0].L("MediaType"), spindle = disks[0].L("SpindleSpeed");
            if (media == 3) return true;
            if (media == 4 || media == 5) return false;
            return spindle is > 0 and < uint.MaxValue ? true : null;
        }
        finally { Wmi.DisposeAll(disks); }
    }

    // ------------------------------------------------------------------ regras

    /// <summary>
    /// Gera os achados do perfil Trabalho a partir dos fatos (lógica pura, testável com fatos sintéticos). Todos têm escopo <see cref="Scope.Work"/> e Id estável "trabalho.*".
    /// Em notebook nada aqui sugere desligar a hibernação.
    /// </summary>
    /// <param name="res">Seção que recebe os achados.</param>
    /// <param name="f">Fatos coletados.</param>
    /// <param name="x">Dados auxiliares (RAM instalada, tipo do disco do Windows, Spooler desativado, data de referência).</param>
    public static void AddFindings(SectionResult res, SystemFacts f, WorkChecks? x = null)
    {
        x ??= new WorkChecks();
        var now = x.Now ?? DateTime.Now;
        const Scope W = Scope.Work;

        // --- Impressão
        var bad = f.Printers.Where(p => p.Offline || p.HasError).ToList();
        if (bad.Count > 0)
            res.Find(Severity.Aviso, Loc.T("Impressora offline ou com erro", "Printer offline or in error"),
                string.Join("; ", bad.Take(4).Select(p => $"{p.Name}: {p.StatusText}")) + (bad.Count > 4 ? $" (+{bad.Count - 4})" : ""),
                Loc.T("O Windows não consegue falar com a impressora ou ela informa um problema (papel, toner, tampa). Impressora de rede também aparece offline quando o PC está em outra rede ou ela está desligada.",
                    "Windows cannot talk to the printer or it reports a problem (paper, toner, cover). A network printer also shows offline when the PC is on another network or the printer is off."),
                Loc.T("Confira se a impressora está ligada, com papel e na mesma rede ou cabo; depois, em Configurações > Impressoras e scanners, desmarque \"Usar impressora offline\".",
                    "Check that the printer is on, has paper and is on the same network or cable; then, in Settings > Printers & scanners, clear \"Use printer offline\"."),
                impact: 30, id: "trabalho.printer_offline", scope: W,
                simple: Loc.T("O Windows não está conseguindo usar uma das impressoras. Veja se ela está ligada, com papel e conectada à mesma rede do PC.",
                    "Windows cannot use one of the printers. Check that it is on, has paper and is connected to the same network as the PC."));

        int stuck = f.Printers.Sum(p => p.StuckJobs);
        if (stuck > 0)
            res.Find(Severity.Aviso, Loc.T("Trabalhos presos na fila de impressão", "Jobs stuck in the print queue"),
                Loc.T($"{stuck} trabalho(s) na fila há mais de {StuckMinutes} min em: {string.Join(", ", f.Printers.Where(p => p.StuckJobs > 0).Select(p => p.Name).Take(3))}",
                    $"{stuck} job(s) in the queue for more than {StuckMinutes} min on: {string.Join(", ", f.Printers.Where(p => p.StuckJobs > 0).Select(p => p.Name).Take(3))}"),
                Loc.T("Um documento parado costuma bloquear os seguintes. Limpar a fila apaga os documentos pendentes, que precisam ser enviados de novo.",
                    "A stalled document usually blocks the ones after it. Clearing the queue deletes the pending documents, which must be sent again."),
                Loc.T("Limpe a fila de impressão pela correção disponível ou cancele os documentos em Configurações > Impressoras e scanners > Abrir fila de impressão.",
                    "Clear the print queue with the available fix or cancel the documents in Settings > Printers & scanners > Open print queue."),
                "PRINT_QUEUE_CLEAR", 35, id: "trabalho.print_stuck", scope: W,
                simple: Loc.T("Há documentos parados na fila de impressão, e isso costuma impedir os próximos de imprimir. Limpar a fila resolve, mas os documentos precisam ser enviados de novo.",
                    "There are documents stuck in the print queue, which usually stops the next ones from printing. Clearing the queue fixes it, but the documents must be sent again."));

        if (f.SpoolerRunning == false)
        {
            if (x.SpoolerDisabled || f.SpoolerDisabled == true)
                res.Find(Severity.Info, Loc.T("Spooler de impressão desativado", "Print spooler disabled"), Loc.T("Serviço Spooler com início Desativado", "Spooler service set to Disabled"),
                    Loc.T("O serviço está desativado de propósito (por exemplo, para reduzir a superfície de ataque). Sem ele nenhuma impressão funciona.",
                        "The service is disabled on purpose (for example, to reduce the attack surface). Without it, nothing prints."),
                    id: "trabalho.spooler_disabled", scope: W,
                    simple: Loc.T("A impressão está desligada neste PC por configuração. Se precisar imprimir, o serviço Spooler precisa ser reativado.",
                        "Printing is turned off on this PC by configuration. If you need to print, the Spooler service must be re-enabled."));
            else
                res.Find(Severity.Atencao, Loc.T("Spooler de impressão parado", "Print spooler stopped"), Loc.T("Serviço Spooler não está em execução", "Spooler service is not running"),
                    Loc.T("Sem o Spooler nenhum documento é impresso. Ele pode ter travado ou sido parado por uma falha.",
                        "Without the Spooler nothing prints. It may have hung or been stopped by a failure."),
                    Loc.T("Reinicie o serviço de impressão pela correção disponível (exige administrador).", "Restart the print service with the available fix (requires administrator)."),
                    "SPOOLER_RESTART", 35, id: "trabalho.spooler_stopped", scope: W,
                    simple: Loc.T("O serviço que controla as impressões está parado, então nada imprime. Reiniciá-lo costuma resolver.",
                        "The service that handles printing is stopped, so nothing prints. Restarting it usually fixes it."));
        }

        // --- Programas de fábrica
        var remove = f.Bloat.Where(b => b.Verdict == BloatVerdict.Remove).ToList();
        if (remove.Count > 0)
            res.Find(Severity.Aviso, Loc.T($"{remove.Count} programa(s) de fábrica para remover", $"{remove.Count} factory program(s) to remove"),
                string.Join(", ", remove.Take(5).Select(b => b.Name)) + (remove.Count > 5 ? $" (+{remove.Count - 5})" : ""),
                Loc.T("São testes, jogos de propaganda ou atalhos que costumam vir instalados de fábrica. Ocupam espaço e alguns abrem junto com o Windows. O nome não prova que vieram de fábrica: confirme antes de remover.",
                    "These are trials, promotional games or shortcuts that often ship preinstalled. They take up space and some open with Windows. The name does not prove they came from the factory: confirm before removing."),
                Loc.T("Desinstale pelo botão do WinDiag (abre o desinstalador oficial) ou em Configurações > Aplicativos. Mantenha drivers e ferramentas de suporte do fabricante.",
                    "Uninstall with the WinDiag button (opens the official uninstaller) or in Settings > Apps. Keep manufacturer drivers and support tools."),
                impact: Math.Min(40, 8 + 4 * remove.Count), id: "trabalho.bloat", scope: W,
                simple: Loc.T("O PC tem programas de teste ou propaganda que costumam vir de fábrica. Se você não usa, pode desinstalar pelo desinstalador oficial de cada um.",
                    "The PC has trial or promotional programs that often come from the factory. If you do not use them, you can uninstall them with each one's official uninstaller."));

        // --- Windows Update
        if (f.RebootPending == true)
            res.Find(Severity.Aviso, Loc.T("Reinício do Windows pendente", "Windows restart pending"), Loc.T("O Windows Update ou a manutenção do sistema pede reinício", "Windows Update or system servicing is asking for a restart"),
                Loc.T("Há uma atualização instalada que só termina depois de reiniciar. Até lá, a correção pode não estar em vigor.", "An installed update only finishes after a restart. Until then, the fix may not be in effect."),
                Loc.T("Salve o trabalho e reinicie o PC (Reiniciar, não Desligar).", "Save your work and restart the PC (Restart, not Shut down)."),
                impact: 30, id: "trabalho.reboot_pending", scope: W,
                simple: Loc.T("O Windows está esperando um reinício para terminar de instalar uma atualização. Salve o que está fazendo e reinicie.", "Windows is waiting for a restart to finish installing an update. Save your work and restart."));

        if (f.LastUpdateInstalled is { } lu && (now - lu).TotalDays > 45)
        {
            int days = (int)Math.Floor((now - lu).TotalDays);
            bool unconfirmed = f.LastUpdateUnconfirmed;
            res.Find(unconfirmed ? Severity.Info : Severity.Aviso, Loc.T("Sem atualização do Windows há mais de 45 dias", "No Windows update for more than 45 days"),
                unconfirmed
                    ? Loc.T($"Última atualização instalada em {Fmt.Date(lu)} ({days} dias), segundo o registro de hotfixes; não foi possível confirmar a data pelo histórico do Windows Update",
                        $"Last update installed on {Fmt.Date(lu)} ({days} days), according to the hotfix log; the date could not be confirmed from the Windows Update history")
                    : Loc.T($"Última atualização instalada em {Fmt.Date(lu)} ({days} dias), segundo o registro de hotfixes e o histórico do Windows Update", $"Last update installed on {Fmt.Date(lu)} ({days} days), according to the hotfix log and the Windows Update history"),
                Loc.T("O Windows costuma receber correções todo mês. Muito tempo sem nenhuma pode indicar atualizações pausadas ou falhando. O registro de hotfixes não lista todas as atualizações, então confirme no Windows Update.",
                    "Windows normally receives fixes every month. A long time without any may mean updates are paused or failing. The hotfix log does not list every update, so confirm in Windows Update."),
                Loc.T("Abra Configurações > Windows Update e clique em Verificar atualizações.", "Open Settings > Windows Update and click Check for updates."),
                impact: unconfirmed ? 0 : 35, id: "trabalho.update_old", scope: W,
                simple: Loc.T("Faz mais de 45 dias desde a última atualização que o WinDiag conseguiu ver. Vale verificar o Windows Update.", "It has been more than 45 days since the last update WinDiag could see. It is worth checking Windows Update."));
        }
        // --- Bateria
        if (f.HasBattery && f.BatteryWearPct is { } wear)
        {
            string ev = Loc.T($"Desgaste de {Fmt.Num(wear, 1)}% ({Fmt.Num(f.BatteryFullMWh ?? 0, 0)} de {Fmt.Num(f.BatteryDesignMWh ?? 0, 0)} mWh)", $"Wear of {Fmt.Num(wear, 1)}% ({Fmt.Num(f.BatteryFullMWh ?? 0, 0)} of {Fmt.Num(f.BatteryDesignMWh ?? 0, 0)} mWh)");
            if (wear >= 40)
                res.Find(wear >= 60 ? Severity.Atencao : Severity.Aviso, Loc.T("Bateria muito desgastada", "Heavily worn battery"), ev,
                    Loc.T("A bateria segura bem menos carga do que quando era nova, então a autonomia é menor. O desgaste vem do uso e do tempo.", "The battery holds much less charge than when new, so battery life is shorter. Wear comes from use and age."),
                    wear >= 60
                        ? Loc.T("Considere trocar a bateria. Para ganhar tempo, use o plano Equilibrado e evite calor.", "Consider replacing the battery. To gain time, use the Balanced plan and avoid heat.")
                        : Loc.T("Se a autonomia já atrapalha, considere trocar a bateria; enquanto isso, use o plano Equilibrado e evite calor.", "If battery life already gets in the way, consider replacing the battery; meanwhile, use the Balanced plan and avoid heat."),
                    impact: wear >= 60 ? 55 : 35, id: "trabalho.battery_wear", scope: W,
                    simple: Loc.T("A bateria já perdeu boa parte da capacidade e dura menos do que antes. Quando incomodar, a solução é trocar a bateria.", "The battery has lost a good part of its capacity and lasts less than before. When it gets in the way, the fix is to replace the battery."));
            else
                res.Find(Severity.Ok, Loc.T("Saúde da bateria", "Battery health"), ev, id: "trabalho.battery_wear", scope: W);
        }

        // --- Energia
        bool highPerfNow = IsHighPerformance(f);
        bool batteryHigh = f.IsLaptop && f.OnAcPower == false && highPerfNow;
        var planFix = FixCatalog.Get("POWER_PLAN_WORK");
        bool planApplicable = planFix is not null && planFix.Applicable(f);
        if (batteryHigh)
            res.Find(Severity.Info, Loc.T("Notebook na bateria com plano de alto desempenho", "Laptop on battery with a high performance plan"),
                Loc.T($"Plano ativo: {f.ActivePowerPlanName ?? Fmt.NA}", $"Active plan: {f.ActivePowerPlanName ?? Fmt.NA}"),
                Loc.T("O plano de alto desempenho mantém o processador mais ativo e gasta bateria mais rápido, com pouco ganho em tarefas de escritório.", "The high performance plan keeps the processor more active and drains the battery faster, with little gain in office tasks."),
                Loc.T("Use o plano Equilibrado quando estiver na bateria (correção disponível, com desfazer).", "Use the Balanced plan when on battery (fix available, with undo)."),
                planApplicable ? "POWER_PLAN_WORK" : "", 15, id: "trabalho.power_battery_high", scope: W,
                simple: Loc.T("O notebook está na bateria com um plano que gasta mais energia. O plano Equilibrado faz a bateria durar mais, sem diferença para tarefas de escritório.", "The laptop is on battery with a plan that uses more power. The Balanced plan makes the battery last longer, with no difference for office tasks."));
        else if (planApplicable)
            res.Find(Severity.Info, Loc.T("Plano de energia diferente do recomendado para trabalho", "Power plan differs from the one recommended for work"),
                Loc.T($"Plano ativo: {f.ActivePowerPlanName ?? Fmt.NA}", $"Active plan: {f.ActivePowerPlanName ?? Fmt.NA}"),
                f.IsLaptop
                    ? Loc.T("Em notebook, o plano Equilibrado costuma dar o melhor meio-termo entre fluidez e bateria.", "On a laptop, the Balanced plan usually gives the best trade-off between responsiveness and battery.")
                    : Loc.T("Em desktop ligado na tomada, o plano Alto desempenho evita que o processador demore a acelerar.", "On a plugged-in desktop, the High performance plan keeps the processor from being slow to speed up."),
                Loc.T("Troque o plano de energia pela correção disponível (o plano atual fica salvo para desfazer).", "Switch the power plan with the available fix (the current plan is saved for undo)."),
                "POWER_PLAN_WORK", 10, id: "trabalho.power_plan", scope: W,
                simple: Loc.T("O plano de energia do Windows não é o mais indicado para este tipo de PC. Dá para trocar e desfazer a qualquer momento.", "The Windows power plan is not the best fit for this type of PC. It can be switched and undone at any time."));
        else if (f.ActivePowerPlanGuid is not null && planFix?.IsActive(f) == true)
            res.Find(Severity.Ok, Loc.T("Plano de energia", "Power plan"), f.ActivePowerPlanName ?? f.ActivePowerPlanGuid, id: "trabalho.power_plan", scope: W);

        // --- Inicialização
        if (f.BootTimeMs is { } boot)
        {
            double s = boot / 1000d;
            if (boot > 60000)
                res.Find(Severity.Aviso, Loc.T("Inicialização lenta", "Slow startup"), Loc.T($"{Fmt.Num(s, 1)} s no último boot medido", $"{Fmt.Num(s, 1)} s on the last measured boot"),
                    Loc.T("O Windows levou mais de 60 s para ficar utilizável. Muitos programas abrindo junto ou um disco lento costumam pesar, mas a medida sozinha não aponta a causa.",
                        "Windows took more than 60 s to become usable. Many programs opening together or a slow disk are common factors, but the measurement alone does not identify the cause."),
                    Loc.T("Revise o que abre com o Windows (Gerenciador de Tarefas > Aplicativos de inicialização) e veja se o sistema está em SSD.", "Review what opens with Windows (Task Manager > Startup apps) and check whether the system is on an SSD."),
                    impact: 40, id: "trabalho.slow_boot", scope: W,
                    simple: Loc.T("O PC demorou mais de um minuto para ficar pronto no último boot. Reduzir os programas que abrem sozinhos costuma ajudar.", "The PC took more than a minute to be ready on the last boot. Cutting the programs that open by themselves usually helps."));
            else
                res.Find(Severity.Ok, Loc.T("Tempo de inicialização", "Boot time"), Loc.T($"{Fmt.Num(s, 1)} s no último boot medido", $"{Fmt.Num(s, 1)} s on the last measured boot"), id: "trabalho.slow_boot", scope: W);
        }

        // --- RAM e disco do sistema (não existem regras equivalentes para jogos)
        double ramGb = x.InstalledRamGB ?? (f.RamGB > 0 ? f.RamGB + 0.5 : 0);
        if (ramGb > 0 && ramGb < 8)
            res.Find(Severity.Aviso, Loc.T("Pouca memória RAM para trabalho", "Little RAM for work"), Loc.T($"{Fmt.Num(ramGb, 1)} GB instalados", $"{Fmt.Num(ramGb, 1)} GB installed"),
                Loc.T("Com menos de 8 GB, navegador com várias abas e programas de escritório abertos juntos fazem o Windows usar o disco como memória, o que deixa tudo lento.",
                    "With less than 8 GB, a browser with several tabs and office programs open together make Windows use the disk as memory, which slows everything down."),
                Loc.T("Adicione memória RAM para chegar a 8 GB ou mais (custo: hardware).", "Add RAM to reach 8 GB or more (cost: hardware)."),
                impact: 45, id: "trabalho.low_ram", scope: W,
                simple: Loc.T("O PC tem pouca memória para o uso atual de trabalho. Colocar mais memória é o que mais ajuda.", "The PC has little memory for current work use. Adding more memory helps the most."));

        if (x.SystemDriveIsHdd == true)
            res.Find(Severity.Atencao, Loc.T("Windows instalado em HDD", "Windows installed on an HDD"), Loc.T("A unidade do Windows é um disco rígido (HDD)", "The Windows drive is a hard disk (HDD)"),
                Loc.T("O HDD é muito mais lento que um SSD para abrir o Windows e os programas. Isso pesa mais no dia a dia do que processador ou memória.", "An HDD is much slower than an SSD for starting Windows and programs. This weighs more in daily use than the processor or memory."),
                Loc.T("Migre o Windows para um SSD (custo: hardware).", "Move Windows to an SSD (cost: hardware)."),
                impact: 55, id: "trabalho.system_hdd", scope: W,
                simple: Loc.T("O Windows está em um disco lento (HDD). Trocar por um SSD é a melhoria que mais deixa o PC rápido no dia a dia.", "Windows is on a slow disk (HDD). Switching to an SSD is the upgrade that most speeds up the PC in daily use."));
    }

    /// <summary>true se o plano ativo é Alto desempenho, Desempenho máximo (GUID e9a42b02-...) ou tem nome equivalente (idiomas pt-BR e en). Usado para avisar de consumo alto na bateria.</summary>
    private static bool IsHighPerformance(SystemFacts f) =>
        string.Equals(f.ActivePowerPlanGuid, FixCatalog.HighPerformancePlanGuid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(f.ActivePowerPlanGuid, "e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase)
        || (f.ActivePowerPlanName is { } n && Regex.IsMatch(n, @"alto desempenho|high performance|ultimate|desempenho máximo|desempenho maximo", RegexOptions.IgnoreCase));
}
