using System.Management;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Armazenamento: discos físicos (MSFT_PhysicalDisk), contadores de confiabilidade (MSFT_StorageReliabilityCounter),
/// S.M.A.R.T. bruto (root\wmi), partições/volumes, TRIM (fsutil) e bibliotecas Steam.
/// O S.M.A.R.T. bruto e o histórico exigem administrador; sem ele o campo mostra "n/d" e uma nota é registrada.
/// Somente leitura, exceto a gravação do histórico S.M.A.R.T. (smart_history.json, só como administrador).
/// </summary>
public sealed class StorageCollector : ICollector
{
    /// <inheritdoc/>
    public string Key => "armazenamento";
    /// <inheritdoc/>
    public string Title => Loc.T("Armazenamento", "Storage");

    private static readonly string[] TrimFileSystems = { "NTFS", "ReFS" };
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    private sealed class Disk
    {
        public string Id = "";
        public int Number;
        public string Name = "";
        public string Media = "";      // HDD, SSD, SCM, Unspecified
        public uint Spindle;
        public long Size;
        public int Bus;
        public string Firmware = "";
        public string Health = "";
        public string Serial = "";
        public string Pnp = "";        // PNPDeviceID do Win32_DiskDrive (instância do dispositivo)
        public bool IsHdd => Media == "HDD" || (Media != "SSD" && Spindle > 0 && Spindle < uint.MaxValue);
        public string Kind => Media == "SSD" ? (Bus == 17 ? "NVMe" : "SSD") : IsHdd ? "HDD" : MediaLabel;
        public string MediaLabel => Media switch { "SCM" => "SCM", "Unspecified" or "" => Loc.T("indefinido", "unspecified"), _ => Media };
    }

    /// <inheritdoc/>
    public SectionResult Collect(SystemFacts facts)
    {
        var res = new SectionResult { Key = Key, Title = Title };
        try { CollectCore(facts, res); }
        catch (Exception ex) { res.Note(Loc.T("Armazenamento: leitura parcial (", "Storage: partial reading (") + ex.Message.Trim() + ")"); }
        return res;
    }

    private static string BusName(int b) => b switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => Loc.T("Virtual (arquivo)", "Virtual (file)"), 16 => "Storage Spaces",
        17 => "NVMe", _ => Fmt.NA
    };

    private static string HealthName(long? h) => h switch
    {
        0 => Loc.T("Saudável", "Healthy"), 1 => Loc.T("Aviso", "Warning"), 2 => Loc.T("Não saudável", "Unhealthy"), _ => Fmt.NA
    };

    private static void CollectCore(SystemFacts facts, SectionResult res)
    {
        // --- Discos e partições (equivalente WMI de Get-PhysicalDisk / Get-Partition) ---
        var disks = new List<Disk>();
        var letterDisk = new Dictionary<char, Disk>();
        try
        {
            string? err = null;
            foreach (var o in Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", StorageNs, e => err = e))
            {
                var id = o.S("DeviceId") ?? "";
                int.TryParse(id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var num);
                var media = (o.L("MediaType")) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
                disks.Add(new Disk
                {
                    Id = id, Number = num, Name = o.S("FriendlyName") ?? "", Media = media,
                    Spindle = (uint)Math.Clamp(o.L("SpindleSpeed") ?? 0, 0, uint.MaxValue),
                    Size = o.L("Size") ?? 0, Bus = (int)(o.L("BusType") ?? 0),
                    Firmware = o.S("FirmwareVersion") ?? "", Health = HealthName(o.L("HealthStatus")),
                    Serial = (o.S("SerialNumber") ?? "").Trim()
                });
            }
            if (err != null) res.Note(Loc.T("Discos físicos (MSFT_PhysicalDisk): ", "Physical disks (MSFT_PhysicalDisk): ") + err);
            disks.Sort((a, b) => a.Number.CompareTo(b.Number));

            // Instância do dispositivo (PNPDeviceID) de cada disco, para casar o S.M.A.R.T. por instância e não só por modelo.
            try
            {
                foreach (var o in Wmi.Query("SELECT Index, PNPDeviceID FROM Win32_DiskDrive", onError: e => res.Note("Win32_DiskDrive: " + e)))
                {
                    var idx = (int)(o.L("Index") ?? -1);
                    var dk = disks.FirstOrDefault(d => d.Number == idx);
                    if (dk != null) dk.Pnp = o.S("PNPDeviceID") ?? "";
                }
            }
            catch (Exception ex) { res.Note("Win32_DiskDrive: " + ex.Message.Trim()); }

            foreach (var o in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", StorageNs, e => res.Note(Loc.T("Partições (MSFT_Partition): ", "Partitions (MSFT_Partition): ") + e)))
            {
                var dl = o.P("DriveLetter");
                char c = dl switch { char ch => ch, null => '\0', _ => SafeChar(dl) };
                if (c < 'A' || (c > 'Z' && c < 'a') || c > 'z') continue;
                var disk = disks.FirstOrDefault(d => d.Number == (int)(o.L("DiskNumber") ?? -1));
                if (disk != null) letterDisk[char.ToUpperInvariant(c)] = disk;
            }
        }
        catch (Exception ex) { res.Note(Loc.T("Discos físicos: ", "Physical disks: ") + ex.Message.Trim()); }

        facts.HddCount = disks.Count(x => x.IsHdd);
        facts.SsdCount = disks.Count(x => !x.IsHdd);
        facts.IsSystemSSD = letterDisk.TryGetValue('C', out var cd) && cd.Media == "SSD";

        // --- Contadores de confiabilidade ---
        var rel = new Dictionary<string, (long? Temp, long? Hours)>();
        try
        {
            foreach (var o in Wmi.Query("SELECT * FROM MSFT_StorageReliabilityCounter", StorageNs))
            {
                var id = o.S("DeviceId");
                if (id != null) rel[id] = (o.L("Temperature"), o.L("PowerOnHours"));
            }
        }
        catch (Exception ex) { res.Note(Loc.T("Contadores de confiabilidade: ", "Reliability counters: ") + ex.Message.Trim()); }

        // --- S.M.A.R.T. bruto ---
        var smart = ReadSmart(facts, res);
        var hist = Smart.LoadHistory(out var histErr);
        if (histErr != null)
            res.Note(Loc.T("Histórico S.M.A.R.T. ilegível (smart_history.json): " + histErr + " - comparação com leitura anterior indisponível", "S.M.A.R.T. history unreadable (smart_history.json): " + histErr + " - comparison with the previous reading unavailable"));
        bool smartRead = false;
        var noRelDisks = new List<string>();
        string today = Fmt.Date(DateTime.Now);

        if (disks.Count == 0) res.Note(Loc.T("Nenhum disco físico retornado pelo Windows (MSFT_PhysicalDisk).", "No physical disk returned by Windows (MSFT_PhysicalDisk)."));

        // Cada disco recebe a sua própria leitura (instância usada só uma vez); se ficar ambíguo, não atribui.
        var assign = Smart.MatchInstances(disks.Select(x => ((string?)x.Pnp, (string?)x.Name)).ToList(), smart.Select(s => s.Instance).ToList(), out var smartAmbiguous);
        if (smartAmbiguous) res.Note(Loc.T("S.M.A.R.T.: discos do mesmo modelo sem correspondência segura entre a leitura e o disco; leitura não atribuída para evitar misturar discos", "S.M.A.R.T.: disks of the same model with no reliable match between the reading and the disk; reading not assigned to avoid mixing disks"));

        for (int di = 0; di < disks.Count; di++)
        {
            var d = disks[di];
            try
            {
                rel.TryGetValue(d.Id, out var rc);
                var sm = assign[di] is { } si ? smart[si] : null;
                var a = sm?.Attrs;

                long? temp = rc.Temp is > 0 ? rc.Temp : null;
                long? hrs = rc.Hours is > 0 ? rc.Hours : null;
                if (temp == null && a != null && a.TryGetValue(194, out var t194) && (t194.Raw & 0xFF) > 0) temp = t194.Raw & 0xFF;
                if (hrs == null && a != null && a.TryGetValue(9, out var h9) && h9.Raw > 0) hrs = h9.Raw;
                if (temp is null && hrs is null) noRelDisks.Add(Loc.T($"Disco {d.Id} ({d.Name})", $"Disk {d.Id} ({d.Name})"));

                var letters = string.Join(" ", letterDisk.Where(kv => kv.Value == d).Select(kv => kv.Key + ":").OrderBy(x => x));
                var g = res.Group(Loc.T($"Disco {d.Id}", $"Disk {d.Id}") + (letters.Length > 0 ? $" [{letters}]" : ""));
                g.Add(Loc.T("Modelo", "Model"), d.Name);
                g.Add(Loc.T("Tipo", "Type"), d.Kind + (d.IsHdd && d.Spindle > 0 && d.Spindle < uint.MaxValue ? $" {d.Spindle} rpm" : ""));
                g.Add(Loc.T("Capacidade", "Capacity"), d.Size > 0 ? Fmt.Num(Math.Round(d.Size / 1073741824d, 0), 0) + " GB" : null);
                g.Add(Loc.T("Barramento", "Bus"), BusName(d.Bus));
                g.Add("Firmware", d.Firmware);
                g.Add(Loc.T("Saúde do Windows", "Windows health"), d.Health, d.Health == HealthName(0) ? Severity.Ok : d.Health == Fmt.NA ? null : Severity.Atencao);
                g.Add(Loc.T("Temperatura", "Temperature"), temp is null ? null : $"{temp} °C");
                g.Add(Loc.T("Horas ligado", "Power-on hours"), hrs is null ? null : $"{hrs} h");
                g.Add(Loc.T("Número de série", "Serial number"), d.Serial);

                var label = Loc.T($"Disco {d.Id} {d.Name}", $"Disk {d.Id} {d.Name}");
                if (sm == null || a == null)
                {
                    res.Find(Severity.Info, Loc.T("S.M.A.R.T. não lido: ", "S.M.A.R.T. not read: ") + d.Name, Loc.T("Atributos brutos indisponíveis", "Raw attributes unavailable"),
                        Loc.T("Alguns discos/controladores não expõem os atributos pelo Windows. O \"Saudável\" do Windows sozinho não garante ausência de problemas.", "Some disks/controllers do not expose the attributes through Windows. Windows reporting \"Healthy\" alone does not guarantee the absence of problems."),
                        Loc.T("Confira com CrystalDiskInfo.", "Check with CrystalDiskInfo."), id: "storage.smart_unread." + d.Id,
                        simple: Loc.T("O Windows não conseguiu ler os dados de saúde deste disco. Isso não indica defeito; use um programa como o CrystalDiskInfo para conferir.", "Windows could not read the health data of this disk. This does not indicate a defect; use a program such as CrystalDiskInfo to check."));
                    continue;
                }

                smartRead = true;
                var tb = g.Table("S.M.A.R.T.", "ID", Loc.T("Atributo", "Attribute"), Loc.T("Bruto", "Raw"), Loc.T("Valor/Pior", "Value/Worst"));
                foreach (var id in Smart.Names.Keys.OrderBy(k => k))
                {
                    if (!a.TryGetValue(id, out var at)) continue;
                    tb.AddRow(id, Smart.NameOf(id), Smart.RawShown(at).ToString(), $"{at.Value}/{at.Worst}");
                }
                if (Smart.VendorSpecificRaw.Any(a.ContainsKey)) g.Notes.Add(Smart.VendorRawNoteText);

                long? realloc = a.TryGetValue(5, out var a5) ? a5.Raw : null;
                long? pend = a.TryGetValue(197, out var a197) ? a197.Raw : null;
                long? unc = a.TryGetValue(198, out var a198) ? a198.Raw : null;
                long? crc = a.TryGetValue(199, out var a199) ? a199.Raw : null;
                var skey = Smart.SerialHash(d.Serial);
                SmartHistoryEntry? prev = null;
                if (skey != null && hist.TryGetValue(skey, out var pe)) prev = pe;
                long? dReal = null, dPend = null, dCrc = null;
                if (prev != null)
                {
                    g.Add(Loc.T("Leitura anterior", "Previous reading"), prev.Date);
                    if (realloc != null && prev.A5 != null) dReal = realloc - prev.A5;
                    if (pend != null && prev.A197 != null) dPend = pend - prev.A197;
                    if (crc != null && prev.A199 != null) dCrc = crc - prev.A199;
                }

                bool emitted = false;
                if (sm.Predict)
                {
                    res.Find(Severity.Atencao, Loc.T("SMART prevê falha: ", "SMART predicts failure: ") + label, Loc.T("PredictFailure = verdadeiro", "PredictFailure = true"),
                        Loc.T("O próprio disco sinaliza que pode falhar em breve.", "The disk itself signals that it may fail soon."), Loc.T("Faça backup AGORA e planeje a troca.", "Back up NOW and plan a replacement."), id: "storage.smart_predict." + d.Id,
                        simple: Loc.T("O próprio disco avisa que pode falhar em breve. Copie seus arquivos importantes agora e planeje trocar o disco.", "The disk itself warns that it may fail soon. Copy your important files now and plan to replace the disk."));
                    emitted = true;
                }
                else if (pend > 0 || unc > 0)
                {
                    var evid = Loc.T($"pendentes={pend}, incorrigíveis={unc}", $"pending={pend}, uncorrectable={unc}");
                    if (dPend > 0) evid += Loc.T($" (pendentes aumentaram {dPend} desde {prev!.Date})", $" (pending increased by {dPend} since {prev!.Date})");
                    res.Find(Severity.Atencao, Loc.T("Setores ruins: ", "Bad sectors: ") + label, evid,
                        Loc.T("Setores que o disco não consegue ler ou regravar. Causam travadas longas e perda de dados.", "Sectors the disk cannot read or rewrite. They cause long freezes and data loss."),
                        Loc.T("Faça backup e verifique/troque o disco.", "Back up and check/replace the disk."), id: "storage.bad_sectors." + d.Id,
                        simple: Loc.T("O disco tem partes que não consegue mais ler. Isso pode causar travadas e perda de arquivos; faça backup e considere trocá-lo.", "The disk has areas it can no longer read. This can cause freezes and file loss; back up and consider replacing it."));
                    emitted = true;
                }
                else if (dReal > 0)
                {
                    res.Find(Severity.Atencao, Loc.T("Setores realocados aumentando: ", "Reallocated sectors increasing: ") + label,
                        Loc.T($"{realloc} setor(es) (antes {prev!.A5} em {prev.Date})", $"{realloc} sector(s) (previously {prev!.A5} on {prev.Date})"),
                        Loc.T("O número de setores realocados cresceu desde a última leitura: o disco continua perdendo setores, o que costuma preceder falha.", "The number of reallocated sectors has grown since the last reading: the disk keeps losing sectors, which often precedes failure."),
                        Loc.T("Faça backup e acompanhe; considere trocar o disco.", "Back up and keep monitoring; consider replacing the disk."), id: "storage.realloc_growing." + d.Id,
                        simple: Loc.T("O disco continua perdendo pedaços da sua área útil, o que costuma anteceder uma falha. Faça backup e acompanhe.", "The disk keeps losing parts of its usable area, which often precedes a failure. Back up and keep monitoring."));
                    emitted = true;
                }
                else if (realloc > 0)
                {
                    res.Find(Severity.Aviso, Loc.T("Setores realocados: ", "Reallocated sectors: ") + label, Loc.T($"{realloc} setor(es) realocado(s)", $"{realloc} reallocated sector(s)"),
                        Loc.T("O disco já substituiu setores defeituosos. Um número pequeno e estável é tolerado; se crescer, é sinal de desgaste.", "The disk has already replaced defective sectors. A small, stable number is tolerated; if it grows, it is a sign of wear."),
                        Loc.T("Anote o valor e confira de novo em algumas semanas.", "Note the value and check again in a few weeks."), id: "storage.realloc." + d.Id,
                        simple: Loc.T("O disco já precisou substituir alguns pedaços defeituosos. Se o número não crescer, tudo bem; vale conferir de novo em algumas semanas.", "The disk has already needed to replace some defective areas. If the number does not grow, that is fine; it is worth checking again in a few weeks."));
                    emitted = true;
                }

                if (crc > 0)
                {
                    if (prev != null && dCrc != null)
                    {
                        if (dCrc > 0)
                        {
                            res.Find(Severity.Aviso, Loc.T("Erros de CRC SATA aumentando: ", "SATA CRC errors increasing: ") + label,
                                Loc.T($"{crc} erro(s) agora, antes {prev.A199} em {prev.Date} (+{dCrc})", $"{crc} error(s) now, previously {prev.A199} on {prev.Date} (+{dCrc})"),
                                Loc.T("Indicam falha na comunicação entre disco e placa (cabo ou conector). O contador é acumulativo e voltou a subir desde a última leitura.", "They indicate a communication failure between the disk and the board (cable or connector). The counter is cumulative and has risen again since the last reading."),
                                Loc.T("Troque o cabo SATA (e a porta, se possível) e confira se o valor para de subir.", "Replace the SATA cable (and the port, if possible) and check whether the value stops rising."), "", 50,
                                id: "storage.crc_growing." + d.Id,
                                simple: Loc.T("A comunicação entre o disco e a placa-mãe está falhando. Quase sempre é o cabo; trocar o cabo SATA costuma resolver.", "Communication between the disk and the motherboard is failing. It is almost always the cable; replacing the SATA cable usually fixes it."));
                            emitted = true;
                        }
                    }
                    else if (crc >= 10)
                    {
                        res.Find(Severity.Aviso, Loc.T("Erros de CRC SATA: ", "SATA CRC errors: ") + label,
                            Loc.T($"{crc} erro(s) acumulado(s), sem leitura anterior para comparar", $"{crc} accumulated error(s), no previous reading to compare"),
                            Loc.T("Indicam falha na comunicação entre disco e placa (cabo ou conector). O contador é acumulativo: o que importa é se aumenta.", "They indicate a communication failure between the disk and the board (cable or connector). The counter is cumulative: what matters is whether it increases."),
                            Loc.T("Troque o cabo SATA e confira se o valor para de subir.", "Replace the SATA cable and check whether the value stops rising."), "", 50,
                            id: "storage.crc." + d.Id,
                            simple: Loc.T("Houve falhas na comunicação entre o disco e a placa-mãe. Se o número continuar subindo, troque o cabo SATA.", "There were communication failures between the disk and the motherboard. If the number keeps rising, replace the SATA cable."));
                        emitted = true;
                    }
                    else
                    {
                        res.Find(Severity.Info, Loc.T("Erros de CRC SATA: ", "SATA CRC errors: ") + label,
                            Loc.T($"{crc} erro(s); valor base registrado; AVISO se aumentar", $"{crc} error(s); baseline value recorded; WARNING if it increases"),
                            Loc.T("Poucos erros de CRC são comuns ao longo da vida do disco. O contador é acumulativo; a próxima coleta comparará com este valor.", "A few CRC errors are common over the life of a disk. The counter is cumulative; the next collection will compare against this value."), id: "storage.crc." + d.Id);
                        emitted = true;
                    }
                }
                if (!emitted)
                    res.Find(Severity.Ok, "S.M.A.R.T. " + label, Loc.T("Sem setores ruins, realocados ou erros de CRC", "No bad sectors, reallocated sectors or CRC errors"), id: "storage.smart_ok." + d.Id);

                if (skey != null)
                    hist[skey] = new SmartHistoryEntry { Date = today, A5 = realloc, A197 = pend, A198 = unc, A199 = crc };
            }
            catch (Exception ex) { res.Note(Loc.T($"Disco {d.Id}: leitura parcial (", $"Disk {d.Id}: partial reading (") + ex.Message.Trim() + ")"); }
        }

        // Só registra a nota se, depois do contador de confiabilidade e do S.M.A.R.T., algum disco ficou sem temperatura E sem horas.
        if (noRelDisks.Count > 0)
            res.Note(Loc.T("Sem temperatura nem horas ligado: ", "No temperature or power-on hours: ") + string.Join(", ", noRelDisks) + (facts.IsAdmin ? "" : Loc.T("; costuma exigir administrador", "; usually requires administrator")));

        if (smartRead)
        {
            if (facts.IsAdmin)
            {
                var e = Smart.SaveHistory(hist);
                if (e != null) res.Note(Loc.T("Não foi possível gravar o histórico S.M.A.R.T. (smart_history.json): ", "Could not save the S.M.A.R.T. history (smart_history.json): ") + e);
            }
            else res.Note(Loc.T("Histórico S.M.A.R.T. não gravado: exige administrador - comparação com leituras futuras indisponível", "S.M.A.R.T. history not saved: requires administrator - comparison with future readings unavailable"));
        }

        // --- Partições ---
        try { CollectPartitions(res, letterDisk); }
        catch (Exception ex) { res.Note(Loc.T("Partições: ", "Partitions: ") + ex.Message.Trim()); }

        // --- TRIM ---
        try { CollectTrim(res); }
        catch (Exception ex) { res.Note("TRIM: " + ex.Message.Trim()); }

        // --- Steam ---
        try { CollectSteam(facts, res, letterDisk); }
        catch (Exception ex) { res.Note(Loc.T("Bibliotecas Steam: ", "Steam libraries: ") + ex.Message.Trim()); }
    }

    private static char SafeChar(object v)
    {
        try { return (char)Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return '\0'; }
    }

    private static List<SmartTable> ReadSmart(SystemFacts facts, SectionResult res)
    {
        var list = new List<SmartTable>();
        string? err = null;
        var fail = Wmi.Query("SELECT * FROM MSStorageDriver_FailurePredictStatus", "root\\wmi", e => err ??= e);
        var data = Wmi.Query("SELECT * FROM MSStorageDriver_ATAPISmartData", "root\\wmi", e => err ??= e);
        foreach (var d in data)
        {
            try
            {
                var inst = d.S("InstanceName") ?? "";
                var f = fail.FirstOrDefault(x => string.Equals(x.S("InstanceName"), inst, StringComparison.OrdinalIgnoreCase));
                bool predict = f?.B("PredictFailure") ?? false;
                list.Add(new SmartTable(inst, predict, Smart.Parse(d.P("VendorSpecific") as byte[])));
            }
            catch (Exception) { /* instância sem dados utilizáveis: ignorada */ }
        }
        if (list.Count == 0)
        {
            if (!facts.IsAdmin) res.Note(Loc.T("S.M.A.R.T.: exige administrador", "S.M.A.R.T.: requires administrator"));
            else res.Note(Loc.T("S.M.A.R.T.: atributos brutos indisponíveis", "S.M.A.R.T.: raw attributes unavailable") + (err != null ? " (" + err + ")" : ""));
        }
        return list;
    }

    private static string PartTitle() => Loc.T("Partições", "Partitions");

    private static void CollectPartitions(SectionResult res, Dictionary<char, Disk> letterDisk)
    {
        var g = res.Group(PartTitle());
        var tb = g.Table("Volumes", Loc.T("Letra", "Letter"), Loc.T("Sistema de arquivos", "File system"), Loc.T("Rótulo", "Label"), Loc.T("Tipo de disco", "Disk type"), Loc.T("Livre", "Free"), "Total", Loc.T("% livre", "% free"));
        var vols = Wmi.Query("SELECT * FROM Win32_LogicalDisk WHERE DriveType=3", onError: e => res.Note("Win32_LogicalDisk: " + e));
        foreach (var v in vols)
        {
            var dev = v.S("DeviceID") ?? "";
            double tot = Math.Round((v.D("Size") ?? 0) / 1073741824d, 1);
            double free = Math.Round((v.D("FreeSpace") ?? 0) / 1073741824d, 1);
            if (tot <= 0) continue;
            double pct = Math.Round(free / tot * 100, 1);
            Disk? dk = null;
            if (dev.Length > 0) letterDisk.TryGetValue(char.ToUpperInvariant(dev[0]), out dk);
            var media = dk != null ? dk.Kind : "?";
            tb.AddRow(dev, v.S("FileSystem"), v.S("VolumeName"), media,
                Fmt.Num(free, 1) + " GB", Fmt.Num(tot, 1) + " GB", Fmt.Num(pct, 1) + "%");
            if (dk?.Media == "SSD" && pct < 20)
                res.Find(Severity.Aviso, Loc.T("Pouco espaço livre em ", "Low free space on ") + dev, Loc.T($"{Fmt.Num(pct, 1)}% livre (SSD)", $"{Fmt.Num(pct, 1)}% free (SSD)"),
                    Loc.T("SSDs ficam mais lentos nas gravações quando quase cheios (menos área para o controlador trabalhar) e o Windows tem menos espaço para cache e atualizações.", "SSDs get slower at writing when nearly full (less area for the controller to work with) and Windows has less room for cache and updates."),
                    Loc.T("Mantenha ao menos 20% livre. Mova jogos e arquivos grandes para outra unidade.", "Keep at least 20% free. Move games and large files to another drive."), "", 20,
                    id: "storage.low_space." + dev.TrimEnd(':'),
                    simple: Loc.T("A unidade está quase cheia. SSD muito cheio fica mais lento e o Windows perde espaço para trabalhar. Apague ou mova arquivos grandes.", "The drive is almost full. A very full SSD gets slower and Windows loses room to work. Delete or move large files."));
            else if (pct < 10)
                res.Find(Severity.Aviso, Loc.T("Pouco espaço livre em ", "Low free space on ") + dev, Loc.T($"{Fmt.Num(pct, 1)}% livre", $"{Fmt.Num(pct, 1)}% free"),
                    Loc.T("Partição quase cheia.", "Partition almost full."), Loc.T("Libere espaço.", "Free up space."), "", 20,
                    id: "storage.low_space." + dev.TrimEnd(':'),
                    simple: Loc.T("A unidade está quase cheia. Libere espaço apagando ou movendo arquivos grandes.", "The drive is almost full. Free up space by deleting or moving large files."));
        }
    }

    private static void CollectTrim(SectionResult res)
    {
        var r = Proc.Run(Proc.Sys32("fsutil.exe"), "behavior query DisableDeleteNotify", 10000);
        var raw = string.Join(" | ", r.Lines.Where(l => l.Trim().Length > 0));
        var parts = new List<string>();
        foreach (var fs in TrimFileSystems)
        {
            var m = Regex.Match(raw, fs + @"\s+DisableDeleteNotify\s*[=:]\s*(\d)");
            if (m.Success) parts.Add($"{fs}={m.Groups[1].Value} ({(m.Groups[1].Value == "0" ? Loc.T("ativo", "enabled") : Loc.T("DESLIGADO", "OFF"))})");
        }
        if (!r.Ok && parts.Count == 0) res.Note("TRIM (fsutil): " + (r.TimedOut ? Loc.T("tempo esgotado", "timed out") : Loc.T("sem resposta", "no response") + (r.StdErr.Length > 0 ? " - " + r.StdErr.Trim() : "")));
        var g = res.Groups.FirstOrDefault(x => x.Title == PartTitle()) ?? res.Group(PartTitle());
        g.Add("TRIM (DisableDeleteNotify)", parts.Count > 0 ? string.Join(" | ", parts) : null);

        if (Regex.IsMatch(raw, @"NTFS\s+DisableDeleteNotify\s*[=:]\s*0"))
            res.Find(Severity.Ok, "TRIM (NTFS)", Loc.T("Ativo", "Enabled"), id: "storage.trim");
        else if (Regex.IsMatch(raw, @"NTFS\s+DisableDeleteNotify\s*[=:]\s*1"))
            res.Find(Severity.Aviso, "TRIM (NTFS)", Loc.T("Desligado", "Off"), Loc.T("Sem TRIM o SSD perde desempenho de gravação com o tempo.", "Without TRIM the SSD loses write performance over time."),
                Loc.T("Execute como administrador: fsutil behavior set DisableDeleteNotify 0", "Run as administrator: fsutil behavior set DisableDeleteNotify 0"), id: "storage.trim",
                simple: Loc.T("Um recurso que mantém o SSD rápido por mais tempo está desligado. Pode ser religado com um comando de administrador.", "A feature that keeps the SSD fast for longer is turned off. It can be turned back on with an administrator command."));
    }

    private static void CollectSteam(SystemFacts facts, SectionResult res, Dictionary<char, Disk> letterDisk)
    {
        var g = res.Group(Loc.T("Bibliotecas Steam", "Steam libraries"));
        var libs = Steam.Libraries(n => res.Note(n));
        facts.SteamOnHdd = false;
        if (libs.Count == 0) g.Add("Steam", Loc.T("não encontrada", "not found"));
        foreach (var lib in libs)
        {
            Disk? dk = null;
            if (lib.Length > 0) letterDisk.TryGetValue(char.ToUpperInvariant(lib[0]), out dk);
            g.Add(lib, dk != null ? $"{dk.Name} ({dk.Kind})" : Loc.T("disco não identificado", "disk not identified"));
            if (dk != null && dk.IsHdd)
            {
                facts.SteamOnHdd = true;
                res.Find(Severity.Aviso, Loc.T("Jogos Steam em HDD: ", "Steam games on HDD: ") + lib[..Math.Min(2, lib.Length)], $"{dk.Name} ({dk.Kind})",
                    Loc.T("HDD tem tempo de acesso de vários milissegundos e leitura de ~100 MB/s. Jogos modernos carregam texturas e cenários enquanto você joga; quando o disco não acompanha, há engasgos, objetos aparecendo tarde e telas de carregamento longas. Nem todo jogo sofre, mas é um dos suspeitos mais comuns.", "An HDD has an access time of several milliseconds and reads at ~100 MB/s. Modern games load textures and scenery while you play; when the disk cannot keep up, there are stutters, objects popping in late and long loading screens. Not every game suffers, but it is one of the most common suspects."),
                    Loc.T("Para testar: no Steam, Propriedades do jogo > Arquivos instalados > Mover pasta de instalação, para um SSD. Compare com e sem a mudança.", "To test: in Steam, game Properties > Installed Files > Move install folder, to an SSD. Compare with and without the change."),
                    "", 55, id: "storage.steam_hdd." + lib[..Math.Min(1, lib.Length)].ToUpperInvariant(),
                    simple: Loc.T("Seus jogos da Steam estão em um HDD, que é bem mais lento que um SSD. Isso pode causar telas de carregamento longas e engasgos; mover o jogo para um SSD ajuda.", "Your Steam games are on an HDD, which is much slower than an SSD. This can cause long loading screens and stutters; moving the game to an SSD helps."));
            }
        }
    }
}
