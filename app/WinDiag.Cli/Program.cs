using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WinDiag.Core;
using WinDiag.Core.Live;

// WinDiag.Cli: ferramenta de linha de comando sem interface e sem UAC (asInvoker), usada em testes e depuração.
// Somente leitura: as fontes de dados são as mesmas dos coletores (WMI, registro, contadores, powercfg/bcdedit/fltmc/nvidia-smi);
// sem administrador, campos que o exigem aparecem como "n/d" com uma nota. As opções de otimização rodam sempre em simulação (dry-run).
//
// Uso:
//   WinDiag.Cli --lang pt-BR|en|auto   (opção global, em qualquer posição) idioma dos textos; padrão: preferência do usuário / idioma do Windows
//   WinDiag.Cli --profile gaming|work  (opção global, em qualquer posição) perfil de uso desta execução (não grava nas preferências)
//   WinDiag.Cli --bloat                 programas de fábrica que a lista do WinDiag reconhece nesta máquina
//   WinDiag.Cli --dump [arquivo.json]   varredura completa em JSON (stdout se sem arquivo); resumo por seção em stderr
//   WinDiag.Cli --history               nota, plano de ação e diff com a última leitura gravada (não grava nada)
//   WinDiag.Cli --live [n]              n amostras ao vivo (padrão 3), uma por segundo
//   WinDiag.Cli --sensors [n]           n leituras do hub de sensores (padrão 1): status + leituras
//   WinDiag.Cli --fixes                 estado de cada otimização do catálogo
//   WinDiag.Cli --dry-enable ID         simula ligar a otimização ID (nada é gravado)
//   WinDiag.Cli --dry-disable ID        simula desligar a otimização ID a partir do snapshot que a aplicou
Console.OutputEncoding = Encoding.UTF8;
WinDiag.Core.Model.UsageProfile? profileOverride = null;

// Opção global --lang: aplica o idioma antes de qualquer outra coisa e a remove dos argumentos.
{
    var rest = new List<string>();
    string? lang = null;
    for (int a = 0; a < args.Length; a++)
    {
        if (args[a] == "--lang" && a + 1 < args.Length) { lang = args[++a]; continue; }
        if (args[a] == "--profile" && a + 1 < args.Length)
        {
            var pv = args[++a];
            if (pv.Equals("gaming", StringComparison.OrdinalIgnoreCase)) profileOverride = WinDiag.Core.Model.UsageProfile.Gaming;
            else if (pv.Equals("work", StringComparison.OrdinalIgnoreCase)) profileOverride = WinDiag.Core.Model.UsageProfile.Work;
            else { Console.Error.WriteLine("--profile: gaming|work"); Environment.Exit(1); }
            continue;
        }
        rest.Add(args[a]);
    }
    if (lang is not null) Loc.Set(lang);
    else
    {
        try { Loc.Set(WinDiag.Core.Settings.UserPrefs.Load().Language); } catch (Exception) { /* mantém o idioma do Windows */ }
    }
    args = rest.ToArray();
}
// Fluxo geral: as opções globais acima já foram removidas de "args"; abaixo, o primeiro argumento escolhe UM modo de execução,
// que devolve o código de saída (0 = ok, 1 = uso incorreto/Id desconhecido, 2 = simulação de otimização falhou).
// Saída em JSON indentado e sem escape de acentos, para ficar legível e comparável entre execuções.
var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

if (args.Length > 0 && args[0] == "--live")
{
    int n = args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var k) && k > 0 ? k : 3;
    using var sampler = new LiveSampler();
    for (int i = 0; i < n; i++)
    {
        Console.WriteLine(JsonSerializer.Serialize(sampler.Sample(), opts));
        Thread.Sleep(1000);
    }
    return 0;
}

// --sensors [n]           status das fontes de sensores e n leituras do SensorHub (padrão 1), uma por segundo
if (args.Length > 0 && args[0] == "--sensors")
{
    int n = args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var k) && k > 0 ? k : 1;
    var open = System.Diagnostics.Stopwatch.StartNew();
    using var hub = new WinDiag.Core.Sensors.SensorHub();
    Console.WriteLine(Loc.T($"Hub aberto em {open.ElapsedMilliseconds} ms", $"Hub opened in {open.ElapsedMilliseconds} ms"));
    var st = hub.Status;
    Console.WriteLine(Loc.T($"HWiNFO={st.HwInfoAvailable} LHM={st.LhmAvailable} (versão {st.LhmVersion ?? "n/d"}) Driver={(st.KernelDriverAvailable ? st.DriverName : "nenhum")}", $"HWiNFO={st.HwInfoAvailable} LHM={st.LhmAvailable} (version {st.LhmVersion ?? "n/a"}) Driver={(st.KernelDriverAvailable ? st.DriverName : "none")}"));
    foreach (var note in st.Notes) Console.WriteLine(Loc.T("  nota: ", "  note: ") + note);
    for (int i = 0; i < n; i++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = hub.Read();
        Console.WriteLine(Loc.T($"--- leitura {i + 1}/{n}: {snap.Readings.Count} sensores em {sw.ElapsedMilliseconds} ms; fontes: {(snap.SourcesUsed.Count == 0 ? "nenhuma" : string.Join(", ", snap.SourcesUsed))}",
            $"--- reading {i + 1}/{n}: {snap.Readings.Count} sensors in {sw.ElapsedMilliseconds} ms; sources: {(snap.SourcesUsed.Count == 0 ? "none" : string.Join(", ", snap.SourcesUsed))}"));
        foreach (var r in snap.Readings)
            Console.WriteLine($"  [{WinDiag.Core.Sensors.HardwareKinds.HardwareKindLabel(r.HardwareKind)}] {r.Hardware} | {r.Name} = {WinDiag.Core.Collectors.SensorsCollector.FormatValue(r)} ({r.Kind}, {r.Source})");
        if (i < n - 1) Thread.Sleep(1000);
    }
    return 0;
}

// --fixes                 status de cada otimização (lê o sistema, não altera nada)
// --dry-enable ID         simula ligar (nada é gravado)
// --dry-disable ID        simula desligar só este item a partir do snapshot que o aplicou
if (args.Length > 0 && (args[0] == "--fixes" || args[0] == "--dry-enable" || args[0] == "--dry-disable"))
{
    // A varredura completa é necessária porque o estado e a aplicabilidade de cada item dependem dos fatos coletados.
    var engine = new ScanEngine { ProfileOverride = profileOverride };
    await engine.RunAsync();
    // dryRun: true garante que nada é alterado nem gravado (nem snapshot), mesmo nos comandos --dry-*.
    var fx = new WinDiag.Core.Fixes.FixEngine(dryRun: true);
    var statuses = fx.GetStatuses(engine.Facts);
    if (args[0] == "--fixes")
    {
        foreach (var s in statuses)
            Console.WriteLine($"{s.Fix.Id,-16} {s.Kind,-13} {s.StatusText}");
        return 0;
    }
    var id = args.Length > 1 ? args[1] : "";
    var st = statuses.FirstOrDefault(s => string.Equals(s.Fix.Id, id, StringComparison.OrdinalIgnoreCase));
    if (st is null) { Console.Error.WriteLine(Loc.T("Id desconhecido: ", "Unknown id: ") + id); return 1; }
    var res = args[0] == "--dry-enable" ? fx.Enable(st.Fix, engine.Facts) : fx.Disable(st);
    Console.WriteLine(Loc.T($"Ok={res.Ok} Reinicio={res.RebootNeeded} Erro={res.Error}", $"Ok={res.Ok} Reboot={res.RebootNeeded} Error={res.Error}"));
    foreach (var m in res.Messages) Console.WriteLine("  " + m);
    return res.Ok ? 0 : 2;
}

// --bloat                 lista o que o BloatwareDb classificou nesta máquina (só leitura)
if (args.Length > 0 && args[0] == "--bloat")
{
    var facts = new WinDiag.Core.Model.SystemFacts();
    var sec = new WinDiag.Core.Collectors.WorkCollector().Collect(facts);
    Console.WriteLine(Loc.T($"{facts.Bloat.Count} programa(s) reconhecido(s) pela lista:", $"{facts.Bloat.Count} program(s) recognised by the list:"));
    foreach (var b in facts.Bloat)
    {
        Console.WriteLine($"  [{WinDiag.Core.Work.BloatwareDb.Label(b.Verdict),-9}] {b.Name} | {(string.IsNullOrEmpty(b.Publisher) ? Fmt.NA : b.Publisher)}{(b.IsAppx ? " | Appx" : "")}");
        Console.WriteLine($"      {b.Reason}");
        if (!string.IsNullOrEmpty(b.UninstallCommand)) Console.WriteLine($"      {Loc.T("desinstalar", "uninstall")}: {b.UninstallCommand}");
    }
    foreach (var n in sec.Notes) Console.Error.WriteLine(Loc.T("nota: ", "note: ") + n);
    return 0;
}

if (args.Length > 0 && args[0] == "--history")
{
    var engine = new ScanEngine { ProfileOverride = profileOverride };
    await engine.RunAsync();
    var all = engine.AllFindings().ToList();
    var prof = engine.Facts.Profile;
    var score = WinDiag.Core.Rules.GameScore.Compute(all, prof, engine.WorkReadingsPartial);
    Console.WriteLine(Loc.T($"Nota: {score.Score}/100 ({score.Grade})", $"Score: {score.Score}/100 ({score.Grade})"));
    Console.WriteLine(score.Summary);
    foreach (var pt in score.Parts) Console.WriteLine($"  -{pt.Lost,2}  {pt.Area}: {pt.Reason}");
    Console.WriteLine();
    Console.WriteLine(Loc.T("Plano de ação:", "Action plan:"));
    foreach (var st in WinDiag.Core.Rules.ActionPlan.Build(all, prof))
        Console.WriteLine($"  {st.Order}. [{st.Cost}] {st.Title} | {Loc.T("Onde", "Where")}: {st.Where} | {st.What} | {Loc.T("Ganho", "Gain")}: {st.Gain}");
    Console.WriteLine();
    var prev = WinDiag.Core.History.ScanHistory.LoadLatest();
    if (prev is null) { Console.WriteLine(Loc.T("Sem leitura anterior em ", "No previous scan in ") + WinDiag.Core.History.ScanHistory.DefaultDir); return 0; }
    // Compare + Summarize (em vez de RecordAndCompare) para não gravar um novo arquivo no histórico.
    var d = WinDiag.Core.History.ScanHistory.Compare(prev, WinDiag.Core.History.ScanHistory.Summarize(engine));
    Console.WriteLine(Loc.T($"Comparação com {Fmt.DateTime(prev.When)}: nota {d.PreviousScore} -> {d.CurrentScore}", $"Comparison with {Fmt.DateTime(prev.When)}: score {d.PreviousScore} -> {d.CurrentScore}"));
    void Show(string name, IEnumerable<WinDiag.Core.History.FindingChange> l) { foreach (var c in l) Console.WriteLine($"  {name}: {c.Title} ({c.OldSev?.ToString() ?? "-"} -> {c.NewSev?.ToString() ?? "-"})"); }
    Show(Loc.T("Resolvido", "Resolved"), d.Resolved); Show(Loc.T("Melhorou", "Improved"), d.Improved); Show(Loc.T("Piorou", "Worsened"), d.Worsened); Show(Loc.T("Novo", "New"), d.New);
    foreach (var c in d.FactChanges) Console.WriteLine($"  {Loc.T("Fato", "Fact")}: {c.Label}: {c.Old} -> {c.New}");
    if (d.IsEmpty) Console.WriteLine(Loc.T("  Nenhuma mudança.", "  No changes."));
    return 0;
}

if (args.Length > 0 && args[0] == "--dump")
{
    var engine = new ScanEngine { ProfileOverride = profileOverride };
    await engine.RunAsync();
    // JSON em stdout (ou no arquivo informado); o resumo por seção vai para stderr para não misturar com o JSON.
    var dump = new { engine.Facts, Sections = engine.Results };
    var json = JsonSerializer.Serialize(dump, opts);
    if (args.Length > 1) File.WriteAllText(args[1], json, new UTF8Encoding(false));
    else Console.WriteLine(json);
    foreach (var r in engine.Results)
        Console.Error.WriteLine(Loc.T($"{r.Key,-14} {r.Elapsed.TotalMilliseconds,7:0} ms  grupos={r.Groups.Count} findings={r.Findings.Count} notas={r.Notes.Count}{(r.Error is null ? "" : "  ERRO: " + r.Error)}",
            $"{r.Key,-14} {r.Elapsed.TotalMilliseconds,7:0} ms  groups={r.Groups.Count} findings={r.Findings.Count} notes={r.Notes.Count}{(r.Error is null ? "" : "  ERROR: " + r.Error)}"));
    return 0;
}

Console.Error.WriteLine(Loc.T("uso: WinDiag.Cli [--lang pt-BR|en|auto] [--profile gaming|work] --bloat | --dump [arquivo.json] | --history | --live [n] | --sensors [n] | --fixes | --dry-enable ID | --dry-disable ID",
    "usage: WinDiag.Cli [--lang pt-BR|en|auto] [--profile gaming|work] --bloat | --dump [file.json] | --history | --live [n] | --sensors [n] | --fixes | --dry-enable ID | --dry-disable ID"));
return 1;

