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
    /// <summary>Versão do WinDiag gravada em cada snapshot (permite saber qual formato o gravou).</summary>
    private static string AppVersion => AppInfo.Version;
    /// <summary>Chave do registro onde fica o valor <c>HibernateEnabled</c> (hibernação).</summary>
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
            if (fix.Temp || fix.StartupApps || fix.IsAction)
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
        if (expected.PowerPlan is not null && !string.Equals(expected.PowerPlan, current.PowerPlan, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Compara dois estados de um valor de registro: existência e, se existir, o valor normalizado pelo tipo salvo (assim 1 inteiro e 1 vindo do JSON como long são iguais). Erro de conversão conta como diferente.</summary>
    /// <param name="a">Estado esperado.</param>
    /// <param name="b">Estado atual.</param>
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
            foreach (var m in Apply(fix, null, facts)) Msg(r, fix.Id, m);
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

    /// <summary>
    /// Executa uma ação pontual do catálogo (<see cref="FixDefinition.IsAction"/>: reiniciar o Spooler, limpar a fila de impressão). Não é reversível e não grava snapshot,
    /// mas cada passo vai para o changes.log. Em DryRun nada é executado: só as mensagens do que seria feito. Exige administrador.
    /// </summary>
    /// <param name="fix">Item do catálogo com <see cref="FixDefinition.IsAction"/> = true.</param>
    public OpResult RunAction(FixDefinition fix)
    {
        var r = new OpResult();
        if (!fix.IsAction)
        {
            r.Ok = false; r.Error = Loc.T("Este item não é uma ação pontual.", "This item is not a one-off action.");
            return r;
        }
        try
        {
            if (!DryRun) Store.Log($"{fix.Id}: ação iniciada");
            foreach (var m in ActionSteps(fix)) Msg(r, fix.Id, m);
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
        if (fix.PowerPlan) st.PowerPlan = PowerCfg.ActiveSchemeGuid();
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
        if (saved.PowerPlan is not null) st.PowerPlan = PowerCfg.ActiveSchemeGuid();
        if (saved.Bcd.Count > 0)
        {
            var cur = BcdRead();
            foreach (var b in saved.Bcd) st.Bcd.Add(new BcdState { Name = b.Name, Old = cur.TryGetValue(b.Name, out var v) ? v : null });
        }
        if (saved.Hib is not null) st.Hib = Reg.State(HibPath, "HibernateEnabled");
        return st;
    }

    // ------------------------------------------------------------------ aplicar / restaurar

    /// <summary>Executa os passos de uma otimização: Temp, registro, serviços, DirectX, apps de inicialização, energia, compressão de memória, BCD e plano de energia. Em DryRun só monta as mensagens.</summary>
    /// <remarks>Os valores anteriores já foram capturados em <see cref="Capture"/> antes desta chamada; é isso que torna cada passo reversível. Falha em qualquer passo lança exceção, tratada por quem chamou (<see cref="Fail"/>).</remarks>
    /// <param name="fix">Otimização a aplicar.</param>
    /// <param name="picked">Apps de inicialização escolhidos (somente para a otimização de inicialização).</param>
    /// <param name="facts">Fatos do PC (necessários ao plano de energia de trabalho).</param>
    /// <returns>Mensagens do que foi feito.</returns>
    private List<string> Apply(FixDefinition fix, IEnumerable<StartupItem>? picked, SystemFacts? facts = null)
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
        if (fix.PowerPlan)
        {
            string target = FixCatalog.WorkPlanGuid(facts ?? new SystemFacts());
            if (!DryRun)
            {
                Check(Proc.Run(Proc.Sys32("powercfg.exe"), "/setactive " + target), "powercfg /setactive");
                if (!string.Equals(PowerCfg.ActiveSchemeGuid(), target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Loc.T("O Windows não ativou o plano de energia pedido (ele pode não existir neste PC).", "Windows did not activate the requested power plan (it may not exist on this PC)."));
            }
            msgs.Add(Loc.T($"Plano de energia ativo: {target}", $"Active power plan: {target}"));
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
        if (state.PowerPlan is { Length: > 0 } plan)
        {
            if (PowerCfg.ParseSchemeGuid(plan) is not { } planGuid) throw new InvalidOperationException(Loc.T("GUID do plano de energia salvo é inválido.", "The saved power plan GUID is invalid."));
            if (!DryRun) Check(Proc.Run(Proc.Sys32("powercfg.exe"), "/setactive " + planGuid), "powercfg /setactive");
            msgs.Add(Loc.T($"Plano de energia restaurado: {planGuid}", $"Power plan restored: {planGuid}"));
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

    /// <summary>Converte um valor lido do JSON do snapshot em objeto .NET: números viram long (ou double), listas viram <c>List&lt;object?&gt;</c>. Mantém compatibilidade com snapshots da versão 7.</summary>
    /// <param name="je">Elemento JSON.</param>
    private static object? FromJson(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.String => je.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => je.EnumerateArray().Select(FromJson).ToList(),
        _ => null,
    };

    /// <summary>Representação textual canônica de um valor de registro, usada só para comparar (bytes separados por vírgula, listas por barra vertical).</summary>
    /// <param name="v">Valor já convertido para o tipo do registro.</param>
    private static string Describe(object v) => v switch
    {
        byte[] b => string.Join(",", b),
        string[] s => string.Join("|", s),
        _ => v.ToString() ?? "",
    };

    // ------------------------------------------------------------------ privados

    /// <summary>Atalho para gravar um snapshot com as entradas informadas, marcado com a versão atual.</summary>
    /// <param name="fixes">Entradas (Aplicar ou Desfazer).</param>
    /// <returns>Caminho do arquivo; null em simulação.</returns>
    private string? SaveSnap(params SnapshotFix[] fixes) => SaveSnap(new Snapshot { Version = AppVersion, Fixes = fixes.ToList() });

    /// <summary>Grava o snapshot e registra no changes.log. Em DryRun não grava nada. É chamado ANTES de alterar o sistema, para o estado anterior existir mesmo se a aplicação falhar no meio.</summary>
    /// <param name="snap">Snapshot a gravar.</param>
    /// <returns>Caminho do arquivo; null em simulação.</returns>
    private string? SaveSnap(Snapshot snap)
    {
        if (DryRun) return null;
        var path = Store.Save(snap);
        Store.Log($"Snapshot {Path.GetFileName(path)} | {string.Join(",", snap.Fixes.Select(f => (f.Action ?? "") + ":" + f.Id))}");
        return path;
    }

    /// <summary>Adiciona uma mensagem ao resultado (com prefixo de simulação em DryRun) e a registra no changes.log.</summary>
    /// <param name="r">Resultado em construção.</param>
    /// <param name="id">Id da otimização.</param>
    /// <param name="m">Mensagem.</param>
    private void Msg(OpResult r, string id, string m)
    {
        r.Messages.Add(DryRun ? Loc.T("[simulação] ", "[simulation] ") + m : m);
        if (!DryRun) Store.Log($"{id}: {m}");
    }

    /// <summary>Marca o resultado como falho. Acesso negado vira uma mensagem orientando a executar como administrador; outras exceções usam o texto original. A falha também vai para o changes.log.</summary>
    /// <param name="r">Resultado em construção.</param>
    /// <param name="id">Id da otimização.</param>
    /// <param name="ex">Exceção ocorrida.</param>
    private void Fail(OpResult r, string id, Exception ex)
    {
        r.Ok = false;
        r.Error = ex is UnauthorizedAccessException or System.Security.SecurityException
            ? Loc.T("Acesso negado. Esta alteração exige executar o WinDiag como administrador.", "Access denied. This change requires running WinDiag as administrator.")
            : ex.Message;
        if (!DryRun) Store.Log($"{id}: FALHA {ex.Message}");
    }

    /// <summary>Executa uma verificação que não pode derrubar o app: qualquer exceção vira false.</summary>
    /// <param name="f">Verificação.</param>
    private static bool Try(Func<bool> f) { try { return f(); } catch { return false; } }

    /// <summary>Lança exceção com a saída do processo se um comando externo falhou (código de saída diferente de zero ou tempo esgotado).</summary>
    /// <param name="r">Resultado do processo.</param>
    /// <param name="what">Descrição do comando, usada na mensagem.</param>
    private static void Check(Proc.Result r, string what)
    {
        if (!r.Ok) throw new InvalidOperationException(Loc.T($"{what} falhou: {(r.StdOut + " " + r.StdErr).Trim()}", $"{what} failed: {(r.StdOut + " " + r.StdErr).Trim()}"));
    }

    /// <summary>Captura o estado de um serviço: se está em execução (via <c>sc query</c>), o tipo de início e o início atrasado (via registro). Serve para restaurar exatamente como estava.</summary>
    /// <param name="name">Nome curto do serviço.</param>
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

    /// <summary>Grava um valor de energia em CA com <c>powercfg /setacvalueindex</c> e reaplica o plano ativo para valer na hora.</summary>
    /// <param name="sub">GUID do subgrupo.</param>
    /// <param name="set">GUID da configuração.</param>
    /// <param name="val">Novo índice.</param>
    /// <param name="scheme">GUID do plano; null usa o plano atual.</param>
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

    /// <summary>Confirma no registro que o tipo de início do serviço ficou como esperado; é uma checagem extra depois do <c>sc config</c>, para não reportar sucesso sem efeito.</summary>
    /// <param name="name">Nome do serviço.</param>
    /// <param name="expected">Valor "Start" esperado.</param>
    private static void VerifyServiceStart(string name, int expected)
    {
        var now = Reg.GetInt(@"HKLM:\SYSTEM\CurrentControlSet\Services\" + name, "Start");
        if (now is not null && now != expected)
            throw new InvalidOperationException(Loc.T($"Serviço {name}: o tipo de início continua {now} (esperado {expected}) depois do sc config.", $"Service {name}: the start type is still {now} (expected {expected}) after sc config."));
    }

    /// <summary>
    /// Executa as ações pontuais (reiniciar o Spooler, limpar a fila de impressão). Não são reversíveis: não há estado anterior a restaurar.
    /// O Spooler é parado dentro de um bloco protegido e SEMPRE iniciado de novo no fim (inclusive se a limpeza falhar); os serviços dependentes que estavam
    /// em execução antes são anotados e religados. Em simulação (<see cref="DryRun"/>) nada é lido nem alterado no serviço.
    /// </summary>
    /// <param name="fix">Otimização do tipo ação.</param>
    /// <returns>Mensagens do que foi feito.</returns>
    private List<string> ActionSteps(FixDefinition fix)
    {
        var msgs = new List<string>();
        if (fix.SpoolerRestart)
        {
            List<string> deps = new();
            if (!DryRun) deps = WithSpoolerStopped(null);
            msgs.Add(Loc.T("Serviço Spooler: parado e iniciado", "Spooler service: stopped and started"));
            if (deps.Count > 0) msgs.Add(Loc.T("Serviços dependentes religados: ", "Dependent services started again: ") + string.Join(", ", deps));
        }
        if (fix.PrintQueueClear)
        {
            var dir = SpoolDirectory;
            int deleted = 0, failed = 0;
            List<string> deps = new();
            if (DryRun) deleted = CountSpoolFiles(dir);
            else deps = WithSpoolerStopped(() => { (deleted, failed) = ClearSpoolDirectory(dir); });
            msgs.Add(Loc.T($"Fila de impressão: {deleted} arquivo(s) apagado(s) de {dir}" + (failed > 0 ? $"; {failed} não puderam ser apagados" : ""),
                $"Print queue: {deleted} file(s) deleted from {dir}" + (failed > 0 ? $"; {failed} could not be deleted" : "")));
            msgs.Add(Loc.T("Serviço Spooler: parado e iniciado", "Spooler service: stopped and started"));
            if (deps.Count > 0) msgs.Add(Loc.T("Serviços dependentes religados: ", "Dependent services started again: ") + string.Join(", ", deps));
        }
        return msgs;
    }

    /// <summary>
    /// Anota os serviços dependentes do Spooler que estão em execução, para religá-los depois; para o Spooler; executa o trabalho opcional; e, no <c>finally</c>,
    /// inicia o Spooler (verificando RUNNING) e religa os dependentes anotados. Se o trabalho falhar, o erro original é o que sobe, mas o Spooler é iniciado do mesmo jeito.
    /// </summary>
    /// <param name="work">Trabalho a fazer com o Spooler parado (null = só reiniciar).</param>
    /// <returns>Nomes dos serviços dependentes religados.</returns>
    private static List<string> WithSpoolerStopped(Action? work)
    {
        var deps = ParseRunningServices(SafeEnumDepend());
        var restarted = new List<string>();
        bool failed = false;
        try
        {
            SpoolerStop();
            work?.Invoke();
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            try
            {
                SpoolerStart();
                foreach (var d in deps)
                {
                    var r = Proc.Run(Proc.Sys32("sc.exe"), "start " + d, 30000);
                    if (r.Ok || r.ExitCode == 1056) restarted.Add(d);
                }
            }
            catch (Exception) when (failed) { /* mantém o erro original da etapa anterior */ }
        }
        return restarted;
    }

    /// <summary>Saída de <c>sc enumdepend Spooler</c>; texto vazio se a consulta falhar (a lista de dependentes é um extra).</summary>
    private static string SafeEnumDepend()
    {
        try { return Proc.Run(Proc.Sys32("sc.exe"), "enumdepend Spooler", 10000).StdOut; }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// Interpreta a saída de <c>sc enumdepend</c>/<c>sc query</c> e devolve os nomes (SERVICE_NAME) dos serviços cujo STATE é RUNNING.
    /// Os rótulos e estados do sc.exe são impressos em inglês em qualquer idioma do Windows.
    /// </summary>
    /// <param name="output">Saída do sc.exe.</param>
    public static List<string> ParseRunningServices(string? output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return list;
        string? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^SERVICE_NAME:\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success) { current = m.Groups[1].Value; continue; }
            if (current is not null && Regex.IsMatch(line, @"^STATE\s*:\s*\d+\s+RUNNING\b", RegexOptions.IgnoreCase))
            {
                if (!list.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Add(current);
                current = null;
            }
        }
        return list;
    }
    /// <summary>Pasta onde o Spooler guarda os trabalhos (%SystemRoot%\System32\spool\PRINTERS).</summary>
    public static string SpoolDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "PRINTERS");

    /// <summary>Conta os arquivos da fila de impressão (usado na simulação). Qualquer erro devolve 0.</summary>
    /// <param name="dir">Pasta da fila.</param>
    private static int CountSpoolFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count() : 0; } catch (Exception) { return 0; }
    }

    /// <summary>Apaga os arquivos (não as subpastas) de uma pasta de fila de impressão. Devolve quantos foram apagados e quantos falharam. Pasta inexistente devolve (0, 0).</summary>
    /// <param name="dir">Pasta da fila.</param>
    public static (int Deleted, int Failed) ClearSpoolDirectory(string dir)
    {
        int ok = 0, bad = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(f); ok++; } catch (Exception) { bad++; }
        }
        return (ok, bad);
    }

    /// <summary>true se a saída de <c>sc query Spooler</c> contém a palavra do estado (STOPPED ou RUNNING).</summary>
    /// <param name="word">Palavra do estado, em inglês como o sc.exe imprime.</param>
    private static bool SpoolerState(string word) =>
        Proc.Run(Proc.Sys32("sc.exe"), "query Spooler", 8000).StdOut.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Para o Spooler e espera até 20 s (40 x 500 ms) o estado STOPPED. Tolera "já parado" (1062) e para serviços dependentes se necessário (1051); acesso negado (5) vira UnauthorizedAccessException.</summary>
    private static void SpoolerStop()
    {
        var st = Proc.Run(Proc.Sys32("sc.exe"), "stop Spooler", 30000);
        if (!st.Ok && st.ExitCode != 1062) // 1062 = já estava parado
        {
            if (st.ExitCode == 5) throw new UnauthorizedAccessException("Spooler");
            if (st.ExitCode == 1051) // serviços dependentes em execução
                Check(Proc.Run(Proc.Sys32("net.exe"), "stop Spooler /y", 45000), "net stop Spooler");
            else Check(st, "sc stop Spooler");
        }
        for (int i = 0; i < 40 && !SpoolerState("STOPPED"); i++) Thread.Sleep(500);
        if (!SpoolerState("STOPPED"))
            throw new InvalidOperationException(Loc.T("O serviço Spooler não parou a tempo.", "The Spooler service did not stop in time."));
    }

    /// <summary>Inicia o Spooler e espera até 20 s o estado RUNNING. Tolera "já em execução" (1056). Se o estado final não for RUNNING, lança erro claro.</summary>
    private static void SpoolerStart()
    {
        var st = Proc.Run(Proc.Sys32("sc.exe"), "start Spooler", 30000);
        if (!st.Ok && st.ExitCode != 1056) // 1056 = já estava em execução
        {
            if (st.ExitCode == 5) throw new UnauthorizedAccessException("Spooler");
            Check(st, "sc start Spooler");
        }
        for (int i = 0; i < 40 && !SpoolerState("RUNNING"); i++) Thread.Sleep(500);
        if (!SpoolerState("RUNNING"))
            throw new InvalidOperationException(Loc.T("O serviço Spooler não voltou ao estado em execução. Inicie-o em services.msc antes de imprimir.", "The Spooler service did not return to the running state. Start it in services.msc before printing."));
    }
    /// <summary>Lê as opções do <c>bcdedit /enum {current}</c> em um dicionário nome (minúsculo) para valor. O formato é "nome, dois ou mais espaços, valor" e não depende do idioma.</summary>
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

    /// <summary>Grava ou remove uma opção do BCD atual. Remover uma opção que não existe não é erro; qualquer outra falha lança exceção.</summary>
    /// <param name="name">Nome da opção.</param>
    /// <param name="value">Novo valor; null remove a opção.</param>
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
    /// <summary>Caminho do Windows PowerShell 5.1 (presente em todo Windows 10/11), usado só para os cmdlets de MMAgent.</summary>
    private static string PsExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Lê se a compressão de memória está ligada. Null se a saída não for "True" nem "False".</summary>
    private static bool? MmcRead()
    {
        var r = Proc.Run(PsExe, "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"", 15000);
        var t = r.StdOut.Trim();
        return t.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : t.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>Liga ou desliga a compressão de memória.</summary>
    /// <param name="on">true para ligar.</param>
    private static void MmcWrite(bool on)
    {
        var cmd = on ? "Enable-MMAgent -MemoryCompression" : "Disable-MMAgent -MemoryCompression";
        Check(Proc.Run(PsExe, $"-NoProfile -NonInteractive -Command \"{cmd}\"", 30000), cmd);
    }

    /// <summary>Soma o tamanho dos arquivos de uma pasta, em MB, ignorando o que não for acessível. Erros devolvem 0.</summary>
    /// <param name="dir">Pasta.</param>
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

    /// <summary>Apaga arquivos e subpastas de uma pasta. O que estiver em uso é ignorado: limpeza de temporários é por melhor esforço e nunca falha o app.</summary>
    /// <param name="dir">Pasta a limpar.</param>
    private static void CleanDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var di = new DirectoryInfo(dir);
        foreach (var f in di.EnumerateFiles()) { try { f.Delete(); } catch { } }
        foreach (var d in di.EnumerateDirectories()) { try { d.Delete(true); } catch { } }
    }
}
