using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Fixes;

/// <summary>Situação de uma otimização para este PC.</summary>
public enum FixStatusKind
{
    /// <summary>Faz sentido aplicar e ainda não está em vigor.</summary>
    Disponivel,
    /// <summary>Em vigor e ligado pelo WinDiag: pode ser desfeito sozinho com o estado salvo.</summary>
    AtivoWinDiag,
    /// <summary>Em vigor, mas não pelo WinDiag (AtlasOS, ajuste manual ou padrão do Windows): sem valor anterior seguro.</summary>
    AtivoExterno,
    /// <summary>Não se aplica a este PC.</summary>
    NaoAplicavel,
}

/// <summary>Estado calculado de um item do catálogo (disponível, ativo pelo WinDiag, ativo externo ou não aplicável).</summary>
public sealed class FixStatus
{
    /// <summary>Item do catálogo.</summary>
    public required FixDefinition Fix { get; init; }
    /// <summary>Situação do item.</summary>
    public FixStatusKind Kind { get; init; }
    /// <summary>Entrada do snapshot que aplicou o item (somente para <see cref="FixStatusKind.AtivoWinDiag"/>).</summary>
    public SnapshotFix? Source { get; init; }
    /// <summary>Arquivo do snapshot de origem.</summary>
    public SnapshotFile? SourceFile { get; init; }
    /// <summary>Aplicado depois do último boot e o item exige reinício.</summary>
    public bool RebootPending { get; init; }

    /// <summary>true se a otimização está em vigor (pelo WinDiag ou externamente).</summary>
    public bool IsOn => Kind is FixStatusKind.AtivoWinDiag or FixStatusKind.AtivoExterno;
    /// <summary>true se a interface pode oferecer o interruptor (item reversível e disponível ou desfazível).</summary>
    public bool CanToggle => Fix.Reversible && Kind is FixStatusKind.Disponivel or FixStatusKind.AtivoWinDiag;

    /// <summary>Texto de estado no idioma atual para exibição.</summary>
    public string StatusText => Kind switch
    {
        FixStatusKind.AtivoWinDiag => Loc.T($"ATIVO (aplicado pelo WinDiag em {Fmt.DateTime(SourceFile?.Created)})", $"ON (applied by WinDiag on {Fmt.DateTime(SourceFile?.Created)})") + (RebootPending ? Loc.T(" - reinicie para concluir", " - restart to finish") : ""),
        FixStatusKind.AtivoExterno => Loc.T("Já está assim (configurado fora do WinDiag ou padrão do Windows)", "Already set (configured outside WinDiag or Windows default)") + (RebootPending ? Loc.T(" - pendente de reinício", " - restart pending") : ""),
        FixStatusKind.Disponivel => Loc.T("Disponível", "Available"),
        _ => Loc.T("Não se aplica a este PC", "Not applicable to this PC"),
    };
}

/// <summary>Resultado de uma operação do <see cref="FixEngine"/>.</summary>
public sealed class OpResult
{
    /// <summary>false se a operação falhou (veja <see cref="Error"/>).</summary>
    public bool Ok { get; set; } = true;
    /// <summary>Mensagens do que foi feito (prefixadas com "[simulação]" ou "[simulation]" em DryRun).</summary>
    public List<string> Messages { get; } = new();
    /// <summary>true se é preciso reiniciar para concluir.</summary>
    public bool RebootNeeded { get; set; }
    /// <summary>Caminho do snapshot gravado (null em simulação ou se não gravou).</summary>
    public string? SnapshotPath { get; set; }
    /// <summary>Mensagem de erro no idioma atual quando <see cref="Ok"/> é false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Único ponto que altera o sistema: aplica e desfaz otimizações do <see cref="FixCatalog"/>, sempre gravando antes um snapshot
/// JSON do estado anterior (%ProgramData%\WinDiagMaint\snapshots) e registrando no changes.log.
/// Alterações de registro, serviços, BCD e energia exigem administrador; sem ele a operação falha com mensagem clara.
/// Em DryRun nada é alterado nem gravado: só as mensagens do que seria feito.
/// </summary>
public sealed class FixEngine
{
    /// <summary>Repositório de snapshots usado.</summary>
    public SnapshotStore Store { get; }
    /// <summary>true = simulação: nada é gravado nem alterado.</summary>
    public bool DryRun { get; }
    private static string AppVersion => AppInfo.Version;
    private const string HibPath = @"HKLM:\SYSTEM\CurrentControlSet\Control\Power";

    /// <summary>Cria o motor.</summary>
    /// <param name="store">Repositório de snapshots (null = padrão em %ProgramData%).</param>
    /// <param name="dryRun">true para apenas simular.</param>
    public FixEngine(SnapshotStore? store = null, bool dryRun = false)
    {
        Store = store ?? new SnapshotStore();
        DryRun = dryRun;
    }

    /// <summary>Hora aproximada do último boot (agora menos o tempo ligado).</summary>
    public static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    // ------------------------------------------------------------------ estado

    /// <summary>Calcula o estado de cada item do catálogo (somente leitura). Regras que lançam exceção contam como "não aplicável".</summary>
    public List<FixStatus> GetStatuses(SystemFacts facts)
    {
        var snaps = Store.List();
        var list = new List<FixStatus>();
        foreach (var fix in FixCatalog.All)
        {
            bool applicable = Try(() => fix.Applicable(facts));
            if (fix.Temp || fix.StartupApps)
            {
                list.Add(new FixStatus { Fix = fix, Kind = applicable ? FixStatusKind.Disponivel : FixStatusKind.NaoAplicavel });
                continue;
            }
            bool active = Try(() => fix.IsActive(facts));
            var latest = SnapshotStore.LatestEntry(snaps, fix.Id);
            // HVCI: o registro já diz 0, mas a Integridade de Memória continua em execução até reiniciar.
            bool hvciPending = fix.Id == "HVCI_OFF" && facts.HvciRunning;
            if (active)
            {
                if (latest is { } l && l.Fix.IsApply && l.Fix.Reversible && !AppliedStateChanged(l.Fix, facts))
                    list.Add(new FixStatus
                    {
                        Fix = fix, Kind = FixStatusKind.AtivoWinDiag, Source = l.Fix, SourceFile = l.File,
                        RebootPending = hvciPending || (fix.Reboot && l.File.Created > BootTime),
                    });
                else
                    list.Add(new FixStatus { Fix = fix, Kind = FixStatusKind.AtivoExterno, RebootPending = hvciPending });
            }
            else
            {
                list.Add(new FixStatus { Fix = fix, Kind = applicable ? FixStatusKind.Disponivel : FixStatusKind.NaoAplicavel });
            }
        }
        return list;
    }

    /// <summary>
    /// true se o snapshot guardou o estado resultante (<see cref="SnapshotFix.Applied"/>) e o estado atual das mesmas chaves é diferente
    /// (alguém mexeu depois do WinDiag). Snapshots antigos sem Applied devolvem false (mantêm o comportamento anterior). Nunca lança exceção.
    /// </summary>
    private static bool AppliedStateChanged(SnapshotFix fx, SystemFacts facts)
    {
        if (fx.Applied is null) return false;
        try
        {
            var cur = CaptureLike(fx.Applied, readMmc: false);
            if (fx.Applied.Mmc is not null) cur.Mmc = facts.MemCompression;
            return StateDiffers(fx.Applied, cur);
        }
        catch { return false; }
    }

    /// <summary>Compara dois estados das mesmas chaves (registro, serviços - início e atraso -, BCD, energia, hibernação e compressão de memória). O estado de execução de serviços é ignorado.</summary>
    /// <param name="expected">Estado esperado (o capturado logo depois de aplicar).</param>
    /// <param name="current">Estado atual das mesmas chaves.</param>
    public static bool StateDiffers(FixState expected, FixState current)
    {
        foreach (var e in expected.Reg)
        {
            var c = current.Reg.FirstOrDefault(x => string.Equals(x.Path, e.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (c is null || RegDiffers(e, c)) return true;
        }
        foreach (var e in expected.Svc)
        {
            var c = current.Svc.FirstOrDefault(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (c is null || RegDiffers(e.Start, c.Start) || RegDiffers(e.Delayed, c.Delayed)) return true;
        }
        foreach (var e in expected.Bcd)
        {
            var c = current.Bcd.FirstOrDefault(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (c is null || !string.Equals(e.Old, c.Old, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var e in expected.Power)
        {
            var c = current.Power.FirstOrDefault(x => string.Equals(x.Sub, e.Sub, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Set, e.Set, StringComparison.OrdinalIgnoreCase));
            if (c is null || (e.Old is not null && c.Old is not null && e.Old != c.Old)) return true;
        }
        if (expected.Hib is not null && (current.Hib is null || RegDiffers(expected.Hib, current.Hib))) return true;
        if (expected.Mmc is bool em && current.Mmc is bool cm && em != cm) return true;
        return false;
    }

    private static bool RegDiffers(RegState a, RegState b)
    {
        if (a.Exists != b.Exists) return true;
        if (!a.Exists) return false;
        var kind = Enum.TryParse<RegistryValueKind>(a.Kind, true, out var k) ? k : RegistryValueKind.DWord;
        try { return Describe(ToRegistryValue(a.Value, kind)) != Describe(ToRegistryValue(b.Value, kind)); }
        catch { return true; }
    }

    // ------------------------------------------------------------------ operações

    /// <summary>Liga uma otimização: grava snapshot (Aplicar), aplica e acrescenta ao snapshot o estado resultante.</summary>
    public OpResult Enable(FixDefinition fix, SystemFacts facts)
    {
        var r = new OpResult();
        try
        {
            var state = Capture(fix, facts);
            var fx = new SnapshotFix { Id = fix.Id, Name = fix.Name, Reversible = fix.Reversible, Action = SnapshotActions.Apply, State = state };
            var snap = new Snapshot { Version = AppVersion, Fixes = { fx } };
            r.SnapshotPath = SaveSnap(snap);
            foreach (var m in Apply(fix, null)) Msg(r, fix.Id, m);
            r.RebootNeeded = fix.Reboot;
            if (!DryRun && r.SnapshotPath is not null && fix.Reversible)
            {
                try
                {
                    fx.Applied = CaptureLike(state);
                    Store.Rewrite(r.SnapshotPath, snap);
                }
                catch (Exception ex) { Store.Log($"{fix.Id}: não gravou o estado resultante ({ex.Message})"); }
            }
        }
        catch (Exception ex) { Fail(r, fix.Id, ex); }
        return r;
    }

    /// <summary>Desliga só este item, voltando ao estado salvo no snapshot que o aplicou.</summary>
    public OpResult Disable(FixStatus status)
    {
        var r = new OpResult();
        if (status.Kind != FixStatusKind.AtivoWinDiag || status.Source is null)
        {
            r.Ok = false; r.Error = Loc.T("Este item não foi aplicado pelo WinDiag; não há valor anterior salvo para voltar.", "This item was not applied by WinDiag; there is no saved previous value to return to.");
            return r;
        }
        var fix = status.Fix;
        try
        {
            var now = CaptureLike(status.Source.State);
            r.SnapshotPath = SaveSnap(new SnapshotFix { Id = fix.Id, Name = Loc.T("Desfazer: ", "Undo: ") + fix.Name, Reversible = true, Action = SnapshotActions.Undo, State = now });
            foreach (var m in Restore(status.Source.State)) Msg(r, fix.Id, m);
            r.RebootNeeded = fix.Reboot;
        }
        catch (Exception ex) { Fail(r, fix.Id, ex); }
        return r;
    }

    /// <summary>Limpa temporários (não reversível, sem snapshot de estado).</summary>
    public OpResult CleanTemp()
    {
        var r = new OpResult();
        var fix = FixCatalog.Get("TEMP")!;
        try
        {
            if (!DryRun) Store.Log("TEMP: limpeza iniciada");
            foreach (var m in Apply(fix, null)) Msg(r, fix.Id, m);
        }
        catch (Exception ex) { Fail(r, fix.Id, ex); }
        return r;
    }

    /// <summary>Liga ou desliga um app na inicialização (StartupApproved), com snapshot do valor anterior.</summary>
    public OpResult SetStartupApp(StartupItem item, bool enable)
    {
        var r = new OpResult();
        if (item.ApprovedPath is null || item.ValueName is null)
        {
            r.Ok = false; r.Error = Loc.T("Item sem entrada StartupApproved editável.", "Item has no editable StartupApproved entry.");
            return r;
        }
        try
        {
            var before = new FixState { Reg = { Reg.State(item.ApprovedPath, item.ValueName) } };
            r.SnapshotPath = SaveSnap(new SnapshotFix
            {
                Id = "STARTUP_APPS",
                Name = (enable ? Loc.T("Reativar na inicialização: ", "Re-enable at startup: ") : Loc.T("Desativar na inicialização: ", "Disable at startup: ")) + item.Name,
                Reversible = true,
                Action = enable ? SnapshotActions.Undo : SnapshotActions.Apply,
                State = before,
            });
            var bytes = StartupApprovedBytes(enable, DateTime.Now);
            if (!DryRun) Reg.Set(item.ApprovedPath, item.ValueName, bytes, RegistryValueKind.Binary);
            Msg(r, "STARTUP_APPS", Loc.T($"Inicialização: {item.Name} {(enable ? "reativado" : "desativado")}", $"Startup: {item.Name} {(enable ? "re-enabled" : "disabled")}"));
        }
        catch (Exception ex) { Fail(r, "STARTUP_APPS", ex); }
        return r;
    }

    /// <summary>
    /// Restaura um snapshot inteiro (antigo menu 3). Grava antes um snapshot inverso,
    /// para que o histórico e os interruptores continuem coerentes.
    /// </summary>
    public OpResult RestoreSnapshot(SnapshotFile file)
    {
        var r = new OpResult();
        var entries = file.Data.Fixes.Where(f => f.Reversible).ToList();
        try
        {
            var inverse = entries.Select(f => new SnapshotFix
            {
                Id = f.Id,
                Name = Loc.T("Restaurar: ", "Restore: ") + f.Name,
                Reversible = true,
                Action = f.IsApply ? SnapshotActions.Undo : SnapshotActions.Apply,
                State = CaptureLike(f.State),
            }).ToList();
            r.SnapshotPath = SaveSnap(inverse.ToArray());
        }
        catch (Exception ex) { Fail(r, "RESTAURAR", ex); return r; }

        foreach (var f in file.Data.Fixes)
        {
            if (!f.Reversible) { r.Messages.Add(Loc.T($"{f.Name}: não reversível, ignorado", $"{f.Name}: not reversible, skipped")); continue; }
            try
            {
                foreach (var m in Restore(f.State)) Msg(r, "RESTAURAR " + f.Id, m);
                if (FixCatalog.Get(f.Id)?.Reboot == true) r.RebootNeeded = true;
            }
            catch (Exception ex) { Fail(r, "RESTAURAR " + f.Id, ex); }
        }
        return r;
    }

    // ------------------------------------------------------------------ captura

    /// <summary>Captura o estado atual de tudo o que o item vai alterar (para o snapshot).</summary>
    public static FixState Capture(FixDefinition fix, SystemFacts facts)
    {
        var st = new FixState();
        foreach (var rs in fix.Reg) st.Reg.Add(Reg.State(rs.Path, rs.Name));
        if (fix.DxString) st.Reg.Add(Reg.State(FixCatalog.DxPrefPath, FixCatalog.DxPrefName));
        if (fix.Power.Count > 0)
        {
            var scheme = PowerCfg.ActiveSchemeGuid();
            foreach (var p in fix.Power) st.Power.Add(new PowerState { Sub = p.Sub, Set = p.Set, Old = PowerRead(p.Sub, p.Set, scheme), Scheme = scheme });
        }
        foreach (var s in fix.Svc) st.Svc.Add(SvcCapture(s));
        if (fix.Mmc) st.Mmc = facts.MemCompression;
        if (fix.Bcd.Count > 0)
        {
            var cur = BcdRead();
            foreach (var b in fix.Bcd) st.Bcd.Add(new BcdState { Name = b.Name, Old = cur.TryGetValue(b.Name, out var v) ? v : null });
        }
        if (fix.Hib) st.Hib = Reg.State(HibPath, "HibernateEnabled");
        return st;
    }

    /// <summary>Captura o estado atual das mesmas chaves de um estado salvo (para o snapshot de desfazer).</summary>
    /// <param name="saved">Estado salvo que define quais chaves ler.</param>
    /// <param name="readMmc">false evita iniciar o PowerShell para ler a compressão de memória (o chamador preenche <see cref="FixState.Mmc"/>).</param>
    public static FixState CaptureLike(FixState saved, bool readMmc = true)
    {
        var st = new FixState();
        foreach (var r in saved.Reg) if (!string.IsNullOrEmpty(r.Path)) st.Reg.Add(Reg.State(r.Path, r.Name));
        foreach (var p in saved.Power) if (!string.IsNullOrEmpty(p.Sub)) st.Power.Add(new PowerState { Sub = p.Sub, Set = p.Set, Old = PowerRead(p.Sub, p.Set, p.Scheme), Scheme = p.Scheme });
        foreach (var s in saved.Svc) if (!string.IsNullOrEmpty(s.Name)) st.Svc.Add(SvcCapture(s.Name));
        if (saved.Mmc is not null && readMmc) st.Mmc = MmcRead();
        if (saved.Bcd.Count > 0)
        {
            var cur = BcdRead();
            foreach (var b in saved.Bcd) st.Bcd.Add(new BcdState { Name = b.Name, Old = cur.TryGetValue(b.Name, out var v) ? v : null });
        }
        if (saved.Hib is not null) st.Hib = Reg.State(HibPath, "HibernateEnabled");
        return st;
    }

    // ------------------------------------------------------------------ aplicar / restaurar

    private List<string> Apply(FixDefinition fix, IEnumerable<StartupItem>? picked)
    {
        var msgs = new List<string>();
        if (fix.Temp)
        {
            var dirs = new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
            double before = dirs.Sum(DirMB);
            if (!DryRun) foreach (var d in dirs) CleanDir(d);
            msgs.Add($"Temp: {Fmt.Num(before, 1)} MB -> {Fmt.Num(DryRun ? before : dirs.Sum(DirMB), 1)} MB");
        }
        foreach (var rs in fix.Reg)
        {
            if (!DryRun) Reg.Set(rs.Path, rs.Name, rs.Value, rs.Kind);
            msgs.Add(Loc.T($@"Registro {rs.Path}\{rs.Name} = {rs.Value}", $@"Registry {rs.Path}\{rs.Name} = {rs.Value}"));
        }
        foreach (var s in fix.Svc)
        {
            if (!DryRun)
            {
                Proc.Run(Proc.Sys32("sc.exe"), $"stop {s}", 20000); // falha esperada se o serviço já estiver parado
                Check(Proc.Run(Proc.Sys32("sc.exe"), ServiceConfigArgs(s, 4, false)), $"sc config {s}");
                VerifyServiceStart(s, 4);
            }
            msgs.Add(Loc.T($"Serviço {s}: parado e desativado", $"Service {s}: stopped and disabled"));
        }
        if (fix.DxString)
        {
            var cur = Reg.GetString(FixCatalog.DxPrefPath, FixCatalog.DxPrefName) ?? "";
            var merged = MergeDxSetting(cur, "SwapEffectUpgradeEnable", "1");
            if (!DryRun) Reg.Set(FixCatalog.DxPrefPath, FixCatalog.DxPrefName, merged, RegistryValueKind.String);
            msgs.Add(Loc.T($@"Registro {FixCatalog.DxPrefPath}\{FixCatalog.DxPrefName} = {merged}", $@"Registry {FixCatalog.DxPrefPath}\{FixCatalog.DxPrefName} = {merged}"));
        }
        if (fix.StartupApps && picked is not null)
        {
            foreach (var it in picked)
            {
                if (it.ApprovedPath is null || it.ValueName is null) continue;
                if (!DryRun) Reg.Set(it.ApprovedPath, it.ValueName, StartupApprovedBytes(false, DateTime.Now), RegistryValueKind.Binary);
                msgs.Add(Loc.T($"Inicialização: {it.Name} desativado", $"Startup: {it.Name} disabled"));
            }
        }
        foreach (var p in fix.Power)
        {
            if (!DryRun) PowerWrite(p.Sub, p.Set, p.Value);
            msgs.Add(Loc.T($"Energia (na tomada) {p.Sub}/{p.Set} = {p.Value}", $"Power (plugged in) {p.Sub}/{p.Set} = {p.Value}"));
        }
        if (fix.Mmc)
        {
            if (!DryRun) MmcWrite(fix.MmcOn);
            msgs.Add(fix.MmcOn ? Loc.T("Compressão de memória ativada", "Memory compression enabled") : Loc.T("Compressão de memória desativada", "Memory compression disabled"));
        }
        foreach (var b in fix.Bcd)
        {
            if (!DryRun) BcdWrite(b.Name, b.Value);
            msgs.Add(b.Value is null ? Loc.T($"BCD: {b.Name} removido", $"BCD: {b.Name} removed") : $"BCD: {b.Name} = {b.Value}");
        }
        if (fix.Hib)
        {
            if (!DryRun) Check(Proc.Run(Proc.Sys32("powercfg.exe"), "/hibernate off"), "powercfg /hibernate off");
            msgs.Add(Loc.T("Hibernação e Inicialização Rápida desativadas", "Hibernation and Fast Startup disabled"));
        }
        return msgs;
    }

    /// <summary>Restaura um estado salvo (registro, serviços, compressão de memória, energia, BCD e hibernação). Lança exceção se uma gravação falhar.</summary>
    public List<string> Restore(FixState state)
    {
        var msgs = new List<string>();
        foreach (var r in state.Reg)
        {
            if (string.IsNullOrEmpty(r.Path)) continue;
            if (r.Exists)
            {
                var kind = Enum.TryParse<RegistryValueKind>(r.Kind, true, out var k) ? k : RegistryValueKind.DWord;
                var val = ToRegistryValue(r.Value, kind);
                if (!DryRun) Reg.Set(r.Path, r.Name, val, kind);
                msgs.Add(Loc.T($@"Registro {r.Path}\{r.Name} restaurado para {Describe(val)}", $@"Registry {r.Path}\{r.Name} restored to {Describe(val)}"));
            }
            else
            {
                if (!DryRun) Reg.Delete(r.Path, r.Name);
                msgs.Add(Loc.T($@"Registro {r.Path}\{r.Name} removido (não existia antes)", $@"Registry {r.Path}\{r.Name} removed (did not exist before)"));
            }
        }
        foreach (var s in state.Svc)
        {
            if (!s.Start.Exists)
            {
                msgs.Add(Loc.T($"Serviço {s.Name}: tipo de inicialização anterior desconhecido, não restaurado", $"Service {s.Name}: previous startup type unknown, not restored"));
                continue;
            }
            int start = unchecked((int)Convert.ToInt64(ToRegistryValue(s.Start.Value, RegistryValueKind.DWord), System.Globalization.CultureInfo.InvariantCulture));
            bool delayed = s.Delayed.Exists && Convert.ToInt64(ToRegistryValue(s.Delayed.Value, RegistryValueKind.DWord), System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (!DryRun)
            {
                // Gravar o registro não basta: o SCM mantém "Desativado" até reiniciar. A mudança vai pelo sc config.
                Check(Proc.Run(Proc.Sys32("sc.exe"), ServiceConfigArgs(s.Name, start, delayed)), $"sc config {s.Name}");
                VerifyServiceStart(s.Name, start);
                if (s.Running)
                {
                    var st = Proc.Run(Proc.Sys32("sc.exe"), $"start {s.Name}", 20000);
                    if (!st.Ok && st.ExitCode != 1056) // 1056 = já estava em execução
                        throw new InvalidOperationException(Loc.T($"sc start {s.Name} falhou: {(st.StdOut + " " + st.StdErr).Trim()}", $"sc start {s.Name} failed: {(st.StdOut + " " + st.StdErr).Trim()}"));
                }
            }
            msgs.Add(Loc.T($"Serviço {s.Name}: tipo de inicialização restaurado ({ServiceModeName(start, delayed)})" + (s.Running ? " e iniciado" : ""), $"Service {s.Name}: startup type restored ({ServiceModeName(start, delayed)})" + (s.Running ? " and started" : "")));
        }
        if (state.Mmc is bool mmc)
        {
            if (!DryRun) MmcWrite(mmc);
            msgs.Add(mmc ? Loc.T("Compressão de memória ligada (valor anterior)", "Memory compression on (previous value)") : Loc.T("Compressão de memória desligada (valor anterior)", "Memory compression off (previous value)"));
        }
        foreach (var p in state.Power)
        {
            if (p.Old is null) { msgs.Add(Loc.T($"Energia {p.Sub}/{p.Set}: valor anterior desconhecido, não restaurado", $"Power {p.Sub}/{p.Set}: previous value unknown, not restored")); continue; }
            if (!DryRun) PowerWrite(p.Sub, p.Set, (int)p.Old.Value, p.Scheme);
            msgs.Add(Loc.T($"Energia (na tomada) {p.Sub}/{p.Set} restaurado para {p.Old}", $"Power (plugged in) {p.Sub}/{p.Set} restored to {p.Old}"));
        }
        foreach (var b in state.Bcd)
        {
            if (!DryRun) BcdWrite(b.Name, b.Old);
            msgs.Add(b.Old is null ? Loc.T($"BCD: {b.Name} removido (não existia antes)", $"BCD: {b.Name} removed (did not exist before)") : Loc.T($"BCD: {b.Name} restaurado para {b.Old}", $"BCD: {b.Name} restored to {b.Old}"));
        }
        if (state.Hib is { Exists: true } h && Convert.ToInt64(ToRegistryValue(h.Value, RegistryValueKind.DWord), System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            if (!DryRun) Check(Proc.Run(Proc.Sys32("powercfg.exe"), "/hibernate on"), "powercfg /hibernate on");
            msgs.Add(Loc.T("Hibernação reativada", "Hibernation re-enabled"));
        }
        return msgs;
    }

    // ------------------------------------------------------------------ utilitários (públicos para testes)

    /// <summary>Troca ou acrescenta "Chave=Valor;" preservando as demais.</summary>
    /// <param name="current">Texto atual (pode ser vazio).</param>
    /// <param name="key">Chave a definir.</param>
    /// <param name="val">Novo valor.</param>
    public static string MergeDxSetting(string current, string key, string val)
    {
        var parts = new List<string>();
        bool found = false;
        foreach (var seg in (current ?? "").Split(';'))
        {
            var t = seg.Trim();
            if (t.Length == 0) continue;
            if (Regex.IsMatch(t, "^" + Regex.Escape(key) + @"\s*="))
            {
                if (!found) { parts.Add(key + "=" + val); found = true; }
            }
            else parts.Add(t);
        }
        if (!found) parts.Add(key + "=" + val);
        return string.Join(";", parts) + ";";
    }

    /// <summary>StartupApproved: 02 (habilitado) ou 03 (desabilitado) + 3 bytes zero + FILETIME.</summary>
    /// <param name="enabled">true = habilitado (02); false = desabilitado (03 + FILETIME).</param>
    /// <param name="when">Instante gravado quando desabilitado.</param>
    public static byte[] StartupApprovedBytes(bool enabled, DateTime when)
    {
        var b = new byte[12];
        b[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) Array.Copy(BitConverter.GetBytes(when.ToFileTime()), 0, b, 4, 8);
        return b;
    }

    /// <summary>Converte o valor salvo (JsonElement ou nativo) para o tipo do registro.</summary>
    /// <param name="v">Valor salvo.</param>
    /// <param name="kind">Tipo do registro.</param>
    public static object ToRegistryValue(object? v, RegistryValueKind kind)
    {
        if (v is JsonElement je) v = FromJson(je);
        switch (kind)
        {
            case RegistryValueKind.DWord: return unchecked((int)Convert.ToInt64(v ?? 0, System.Globalization.CultureInfo.InvariantCulture));
            case RegistryValueKind.QWord: return Convert.ToInt64(v ?? 0, System.Globalization.CultureInfo.InvariantCulture);
            case RegistryValueKind.Binary:
                if (v is byte[] bytes) return bytes;
                if (v is IEnumerable<object?> seq) return seq.Select(x => Convert.ToByte(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (v is int[] ints) return ints.Select(x => (byte)x).ToArray();
                return Array.Empty<byte>();
            case RegistryValueKind.MultiString:
                if (v is string[] ss) return ss;
                if (v is IEnumerable<object?> sq) return sq.Select(x => x?.ToString() ?? "").ToArray();
                return new[] { v?.ToString() ?? "" };
            default: return v?.ToString() ?? "";
        }
    }

    private static object? FromJson(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.String => je.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => je.EnumerateArray().Select(FromJson).ToList(),
        _ => null,
    };

    private static string Describe(object v) => v switch
    {
        byte[] b => string.Join(",", b),
        string[] s => string.Join("|", s),
        _ => v.ToString() ?? "",
    };

    // ------------------------------------------------------------------ privados

    private string? SaveSnap(params SnapshotFix[] fixes) => SaveSnap(new Snapshot { Version = AppVersion, Fixes = fixes.ToList() });

    private string? SaveSnap(Snapshot snap)
    {
        if (DryRun) return null;
        var path = Store.Save(snap);
        Store.Log($"Snapshot {Path.GetFileName(path)} | {string.Join(",", snap.Fixes.Select(f => (f.Action ?? "") + ":" + f.Id))}");
        return path;
    }

    private void Msg(OpResult r, string id, string m)
    {
        r.Messages.Add(DryRun ? Loc.T("[simulação] ", "[simulation] ") + m : m);
        if (!DryRun) Store.Log($"{id}: {m}");
    }

    private void Fail(OpResult r, string id, Exception ex)
    {
        r.Ok = false;
        r.Error = ex is UnauthorizedAccessException or System.Security.SecurityException
            ? Loc.T("Acesso negado. Esta alteração exige executar o WinDiag como administrador.", "Access denied. This change requires running WinDiag as administrator.")
            : ex.Message;
        if (!DryRun) Store.Log($"{id}: FALHA {ex.Message}");
    }

    private static bool Try(Func<bool> f) { try { return f(); } catch { return false; } }

    private static void Check(Proc.Result r, string what)
    {
        if (!r.Ok) throw new InvalidOperationException(Loc.T($"{what} falhou: {(r.StdOut + " " + r.StdErr).Trim()}", $"{what} failed: {(r.StdOut + " " + r.StdErr).Trim()}"));
    }

    private static SvcState SvcCapture(string name)
    {
        var basePath = @"HKLM:\SYSTEM\CurrentControlSet\Services\" + name;
        var q = Proc.Run(Proc.Sys32("sc.exe"), $"query {name}", 5000);
        return new SvcState
        {
            Name = name,
            Running = q.StdOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase),
            Start = Reg.State(basePath, "Start"),
            Delayed = Reg.State(basePath, "DelayedAutostart"),
        };
    }

    /// <summary>Lê um valor de energia em CA via powercfg: tenta /query e depois /qh (configurações ocultas, comum no AtlasOS); o penúltimo hexadecimal é o índice em CA (independe do idioma). Null se não lido.</summary>
    /// <param name="sub">GUID do subgrupo.</param>
    /// <param name="set">GUID da configuração.</param>
    /// <param name="scheme">GUID do plano; null usa SCHEME_CURRENT.</param>
    public static long? PowerRead(string sub, string set, string? scheme = null)
    {
        foreach (var mode in new[] { "/query", "/qh" })
        {
            var r = Proc.Run(Proc.Sys32("powercfg.exe"), $"{mode} {SchemeArg(scheme)} {sub} {set}", 8000);
            if (string.IsNullOrWhiteSpace(r.StdOut)) continue;
            var m = Regex.Matches(r.StdOut, "0x[0-9a-fA-F]{8}");
            if (m.Count >= 2) return Convert.ToInt64(m[^2].Value[2..], 16);
        }
        return null;
    }

    /// <summary>Argumento de plano do powercfg: o GUID salvo (se válido) ou SCHEME_CURRENT para snapshots antigos.</summary>
    /// <param name="scheme">GUID salvo no snapshot, ou null.</param>
    public static string SchemeArg(string? scheme) =>
        PowerCfg.ParseSchemeGuid(scheme) is { } g && g.Length == scheme!.Trim().Length ? g : "SCHEME_CURRENT";

    private static void PowerWrite(string sub, string set, int val, string? scheme = null)
    {
        var sch = SchemeArg(scheme);
        Check(Proc.Run(Proc.Sys32("powercfg.exe"), $"/setacvalueindex {sch} {sub} {set} {val}"), "powercfg /setacvalueindex");
        // Reaplica o plano ativo para o valor valer já (se o plano salvo não for o ativo, o ativo continua o mesmo).
        Proc.Run(Proc.Sys32("powercfg.exe"), "/setactive " + (PowerCfg.ActiveSchemeGuid() ?? "SCHEME_CURRENT"));
    }

    /// <summary>Argumentos do "sc.exe config" para um tipo de início salvo: 0 boot, 1 system, 2 auto (com atraso = delayed-auto), 3 demand, 4 disabled.</summary>
    /// <param name="name">Nome do serviço.</param>
    /// <param name="start">Valor DWord "Start" do registro.</param>
    /// <param name="delayed">true se DelayedAutostart = 1.</param>
    public static string ServiceConfigArgs(string name, int start, bool delayed) => $"config {name} start= {ServiceModeName(start, delayed)}";

    /// <summary>Nome do modo no sc.exe (boot, system, auto, delayed-auto, demand, disabled) para o valor "Start" do registro.</summary>
    /// <param name="start">Valor DWord "Start".</param>
    /// <param name="delayed">true se DelayedAutostart = 1 (só vale com Start = 2).</param>
    public static string ServiceModeName(int start, bool delayed) => start switch
    {
        0 => "boot",
        1 => "system",
        2 => delayed ? "delayed-auto" : "auto",
        3 => "demand",
        4 => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(start), start, "Unknown service start type."),
    };

    private static void VerifyServiceStart(string name, int expected)
    {
        var now = Reg.GetInt(@"HKLM:\SYSTEM\CurrentControlSet\Services\" + name, "Start");
        if (now is not null && now != expected)
            throw new InvalidOperationException(Loc.T($"Serviço {name}: o tipo de início continua {now} (esperado {expected}) depois do sc config.", $"Service {name}: the start type is still {now} (expected {expected}) after sc config."));
    }

    private static Dictionary<string, string> BcdRead()
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var r = Proc.Run(Proc.Sys32("bcdedit.exe"), "/enum {current}", 8000);
        foreach (var l in r.Lines)
        {
            var m = Regex.Match(l, @"^\s*([A-Za-z0-9_]+)\s{2,}(.+?)\s*$");
            if (m.Success) h[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
        }
        return h;
    }

    private static void BcdWrite(string name, string? value)
    {
        var args = value is null ? $"/deletevalue {{current}} {name}" : $"/set {{current}} {name} {value}";
        var r = Proc.Run(Proc.Sys32("bcdedit.exe"), args);
        if (r.Ok) return;
        var text = (r.StdOut + " " + r.StdErr).Trim();
        // Remover um valor que não existe não é erro; qualquer outra falha (sem permissão, tempo esgotado...) é.
        if (value is null && !r.TimedOut && IsBcdElementNotFound(text)) return;
        throw new InvalidOperationException($"bcdedit {args}: {text}");
    }

    /// <summary>true se a saída do bcdedit indica "elemento não encontrado" (inglês ou pt-BR). Usada para tolerar a remoção de um valor que já não existe.</summary>
    /// <param name="text">Saída do bcdedit (padrão e erro).</param>
    public static bool IsBcdElementNotFound(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant().Replace('ã', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ç', 'c').Replace('á', 'a');
        return t.Contains("element not found") || t.Contains("element was not found") || t.Contains("could not be found")
            || t.Contains("elemento nao foi encontrado") || t.Contains("elemento especificado nao foi encontrado")
            || t.Contains("nao foi possivel encontrar o elemento") || t.Contains("elemento nao encontrado")
            || (t.Contains("elemento") && t.Contains("nao") && t.Contains("encontrad"));
    }

    // A compressão de memória só é exposta pelos cmdlets do módulo MMAgent (Get/Enable/Disable-MMAgent).
    private static string PsExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static bool? MmcRead()
    {
        var r = Proc.Run(PsExe, "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"", 15000);
        var t = r.StdOut.Trim();
        return t.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : t.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static void MmcWrite(bool on)
    {
        var cmd = on ? "Enable-MMAgent -MemoryCompression" : "Disable-MMAgent -MemoryCompression";
        Check(Proc.Run(PsExe, $"-NoProfile -NonInteractive -Command \"{cmd}\"", 30000), cmd);
    }

    private static double DirMB(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long sum = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { sum += f.Length; } catch { }
            }
            return sum / 1048576d;
        }
        catch { return 0; }
    }

    private static void CleanDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var di = new DirectoryInfo(dir);
        foreach (var f in di.EnumerateFiles()) { try { f.Delete(); } catch { } }
        foreach (var d in di.EnumerateDirectories()) { try { d.Delete(true); } catch { } }
    }
}
