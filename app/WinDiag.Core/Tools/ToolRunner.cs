using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using WinDiag.Core.Fixes;
using WinDiag.Core.Model;
using WinDiag.Core.Util;

namespace WinDiag.Core.Tools;

/// <summary>Registro de uma execução de ferramenta, guardado em tools-history.json.</summary>
/// <param name="When">Data e hora (local) do fim da execução.</param>
/// <param name="ToolId">Id da ferramenta.</param>
/// <param name="Ok">true se terminou sem erro.</param>
/// <param name="Summary">Resumo curto do resultado ou do erro.</param>
public sealed record ToolHistoryEntry(DateTime When, string ToolId, bool Ok, string Summary);

/// <summary>
/// Executa as ferramentas do <see cref="ToolCatalog"/>. Em simulação (<c>dryRun</c>) nada é executado: só as mensagens do que seria feito.
/// Cada execução real vai para o changes.log e para o histórico (%ProgramData%\WinDiagMaint\tools-history.json, últimas 200).
/// O cancelamento encerra a árvore do processo em andamento e, onde há serviços parados, o <c>finally</c> os religa. Nenhuma ferramenta reinicia o PC.
/// </summary>
public sealed partial class ToolRunner
{
    /// <summary>Quantidade máxima de execuções guardadas no histórico.</summary>
    private const int HistoryCap = 200;
    /// <summary>Trava das gravações do histórico (vários executores no mesmo processo).</summary>
    private static readonly object HistoryLock = new();
    /// <summary>Opções do JSON do histórico: legível, sem escape de acentos e leitura sem diferenciar maiúsculas.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Repositório de snapshots e do changes.log (a pasta de dados também guarda o histórico de ferramentas).</summary>
    public SnapshotStore Store { get; }
    /// <summary>true = simulação: nada é executado, gravado ou alterado.</summary>
    public bool DryRun { get; }
    /// <summary>true se o WinDiag foi elevado com outra conta de administrador (veja <see cref="SessionUser"/>): as opções e ferramentas por usuário ficam indisponíveis. Por padrão é detectado; os testes informam o valor.</summary>
    public bool ElevatedAsOtherUser { get; init; } = SessionUser.ElevatedAsOtherUser;

    /// <summary>Cria o executor.</summary>
    /// <param name="store">Repositório (null = padrão em %ProgramData%).</param>
    /// <param name="dryRun">true para apenas simular.</param>
    public ToolRunner(SnapshotStore? store = null, bool dryRun = false)
    {
        Store = store ?? new SnapshotStore();
        DryRun = dryRun;
    }

    /// <summary>Caminho do arquivo de histórico.</summary>
    public string HistoryPath => Path.Combine(Store.DataDir, "tools-history.json");

    /// <summary>Opções de uma ferramenta com tamanho calculado (só a Limpeza de disco tem). Pode demorar: chame em thread de fundo. Nunca lança exceção.</summary>
    /// <param name="tool">Ferramenta.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Assinatura de instância fixada pelo contrato com o App.")]
    public IReadOnlyList<ToolOption> GetOptions(ToolDefinition tool)
    {
        var list = new List<ToolOption>();
        if (!tool.HasOptions || tool.Id != "DISK_CLEANUP") return list;
        bool admin = IsAdministrator();
        foreach (var id in DiskCleanup.AllOptionIds)
        {
            long? bytes = null;
            string? note = null;
            try
            {
                if (ElevatedAsOtherUser && DiskCleanup.IsPerUser(id))
                {
                    bytes = null;
                    note = OtherUserNote;
                }
                else if (DiskCleanup.NeedsAdmin(id) && !admin)
                {
                    bytes = null;
                    note = Loc.T("tamanho não lido (exige administrador)", "size not read (requires administrator)");
                }
                else if (id == DiskCleanup.Recycle) bytes = DiskCleanup.RecycleBinBytes();
                else
                {
                    long sum = 0; bool ok = true;
                    foreach (var t in DiskCleanup.Targets(id, !ElevatedAsOtherUser))
                    {
                        var m = DiskCleanup.Measure(t);
                        if (m is null) { ok = false; break; }
                        sum += m.Value;
                    }
                    bytes = ok ? sum : null;
                    if (!ok) note = Loc.T("tamanho não lido (pasta inacessível)", "size not read (folder not accessible)");
                }
                if (bytes is null && note is null) note = Loc.T("tamanho não lido", "size not read");
            }
            catch (Exception) { bytes = null; note = Loc.T("tamanho não lido", "size not read"); }
            list.Add(new ToolOption(id, DiskCleanup.Label(id), bytes, DiskCleanup.DefaultOn(id), note));
        }
        return list;
    }

    /// <summary>Nota exibida nas opções por usuário quando o WinDiag foi elevado com outra conta.</summary>
    public static string OtherUserNote => Loc.T("indisponível: o WinDiag foi aberto com outra conta de administrador; esta opção é por usuário", "unavailable: WinDiag was opened with another administrator account; this option is per user");

    /// <summary>Aviso forte que o App deve mostrar na confirmação (e exigir "Estou ciente") antes de rodar a ferramenta; null se não há. Hoje só o NET_STACK_RESET: lista os adaptadores com IP fixo e os IPs que seriam apagados, ou avisa que não foi possível verificar. Nunca lança exceção.</summary>
    /// <param name="tool">Ferramenta.</param>
    /// <param name="facts">Fatos da varredura (não usados hoje; reservado).</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Assinatura de instância fixada pelo contrato com o App.")]
    public string? ConfirmWarning(ToolDefinition tool, SystemFacts? facts)
    {
        if (tool.Id != "NET_STACK_RESET") return null;
        try
        {
            var adapters = NetDiag.ReadAdaptersOrNull();
            if (adapters is null) return Loc.T("Não foi possível verificar se há IP fixo. Se algum adaptador usa IP fixo, ele será apagado por este comando.", "Could not check whether a fixed IP is in use. If any adapter uses a fixed IP, this command will erase it.");
            var fixedIp = NetDiag.StaticIpDetails(adapters);
            if (fixedIp.Count == 0) return null;
            return Loc.T("Estes adaptadores usam IP fixo e este comando apagaria a configuração: ", "These adapters use a fixed IP and this command would erase the configuration: ") + string.Join("; ", fixedIp) +
                Loc.T(". Anote o IP, a máscara, o gateway e o DNS antes de continuar.", ". Write down the IP, mask, gateway and DNS before continuing.");
        }
        catch (Exception) { return Loc.T("Não foi possível verificar se há IP fixo.", "Could not check whether a fixed IP is in use."); }
    }

    /// <summary>Motivo pelo qual a ferramenta não deve rodar agora (sem administrador, IP fixo, sem bateria, sem impressora padrão...); null se pode rodar. Nunca lança exceção.</summary>
    /// <param name="tool">Ferramenta.</param>
    /// <param name="facts">Fatos da varredura (null = lê o estado atual do sistema quando preciso).</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Assinatura de instância fixada pelo contrato com o App.")]
    public string? BlockReason(ToolDefinition tool, SystemFacts? facts)
    {
        try
        {
            if (tool.NeedsAdmin && !(facts?.IsAdmin ?? IsAdministrator()))
                return Loc.T("Exige executar o WinDiag como administrador.", "Requires running WinDiag as administrator.");
            switch (tool.Id)
            {
                case "PRINT_UNSTICK":
                case "SPOOLER_RESTART":
                    if (facts?.SpoolerDisabled == true)
                        return Loc.T("O serviço Spooler está desativado neste PC (escolha deliberada). Reative-o antes de usar esta ferramenta.", "The Spooler service is disabled on this PC (deliberate choice). Re-enable it before using this tool.");
                    break;
                case "PRINT_TEST_PAGE":
                    if (DefaultPrinterName(facts) is null)
                        return Loc.T("Não há impressora padrão definida neste PC.", "There is no default printer set on this PC.");
                    break;
                case "SHADER_CACHE_CLEAR":
                    if (facts?.ElevatedAsOtherUser ?? ElevatedAsOtherUser) return OtherUserNote;
                    break;
                case "BATTERY_REPORT":
                    if (!HasBattery(facts))
                        return Loc.T("Este PC não tem bateria.", "This PC has no battery.");
                    break;
                case "RESTORE_POINT":
                    if (SystemProtectionOff())
                        return Loc.T("A Proteção do Sistema está desligada. Ligue-a em Propriedades do Sistema > Proteção do Sistema.", "System Protection is off. Turn it on in System Properties > System Protection.");
                    break;
            }
        }
        catch (Exception) { /* a checagem é um extra: em dúvida, deixa rodar e a própria ferramenta informa a falha */ }
        return null;
    }

    /// <summary>Executa a ferramenta. Em DryRun só devolve as mensagens do que faria. O resultado nunca vem de exceção: falhas voltam em <see cref="OpResult.Error"/>; cancelamento também (Ok = false).</summary>
    /// <param name="tool">Ferramenta.</param>
    /// <param name="log">Recebe o andamento (linhas de saída dos comandos e mensagens).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <param name="optionIds">Opções marcadas (só Limpeza de disco); null usa as marcadas por padrão.</param>
    public Task<OpResult> RunAsync(ToolDefinition tool, IProgress<string>? log, CancellationToken ct, IReadOnlyCollection<string>? optionIds = null)
        => Task.Run(async () =>
        {
            var job = new Job(this, tool, log, optionIds, ct);
            try
            {
                if (!DryRun) Store.Log($"{tool.Id}: ferramenta iniciada");
                if (DryRun) job.Simulate();
                else if (BlockReason(tool, null) is { } why)
                {
                    job.R.Ok = false; job.R.Error = why;
                }
                else await job.ExecuteAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.R.Ok = false; job.R.Error = Loc.T("Cancelado pelo usuário.", "Cancelled by the user.");
            }
            catch (Exception ex)
            {
                job.R.Ok = false;
                job.R.Error = ex is UnauthorizedAccessException or System.Security.SecurityException
                    ? Loc.T("Acesso negado. Esta ferramenta exige executar o WinDiag como administrador.", "Access denied. This tool requires running WinDiag as administrator.")
                    : ex.Message;
            }
            finally
            {
                if (!DryRun)
                {
                    string summary = job.R.Ok ? (job.R.Messages.LastOrDefault() ?? Loc.T("Concluída", "Done")) : (job.R.Error ?? Loc.T("Falhou", "Failed"));
                    Store.Log($"{tool.Id}: {(job.R.Ok ? "concluída" : "FALHA")} - {summary}");
                    AppendHistory(new ToolHistoryEntry(DateTime.Now, tool.Id, job.R.Ok, summary.Length > 300 ? summary[..300] : summary));
                }
            }
            return job.R;
        }, CancellationToken.None);

    /// <summary>Histórico de execuções, da mais recente para a mais antiga. Arquivo ausente ou corrompido devolve lista vazia.</summary>
    /// <param name="max">Quantidade máxima de entradas.</param>
    public IReadOnlyList<ToolHistoryEntry> History(int max = 100)
    {
        lock (HistoryLock)
            return ReadHistory().OrderByDescending(e => e.When).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>Lê o arquivo de histórico; qualquer falha (inexistente, JSON inválido) devolve lista vazia.</summary>
    private List<ToolHistoryEntry> ReadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new();
            var list = JsonSerializer.Deserialize<List<ToolHistoryEntry>>(File.ReadAllText(HistoryPath), JsonOpts);
            return list?.Where(e => e is not null && !string.IsNullOrEmpty(e.ToolId)).ToList() ?? new();
        }
        catch (JsonException)
        {
            try { File.Copy(HistoryPath, HistoryPath + ".bad", overwrite: true); } catch (Exception) { /* a cópia é um extra */ }
            return new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>Acrescenta uma entrada ao histórico, mantendo só as últimas 200. Falhas de gravação são ignoradas.</summary>
    /// <param name="entry">Entrada.</param>
    private void AppendHistory(ToolHistoryEntry entry)
    {
        try
        {
            lock (HistoryLock)
            {
                var list = ReadHistory();
                list.Add(entry);
                if (list.Count > HistoryCap) list = list.Skip(list.Count - HistoryCap).ToList();
                Directory.CreateDirectory(Store.DataDir);
                var tmp = HistoryPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts), new System.Text.UTF8Encoding(false));
                if (File.Exists(HistoryPath)) File.Replace(tmp, HistoryPath, null);
                else File.Move(tmp, HistoryPath);
            }
        }
        catch (Exception) { /* o histórico é um extra */ }
    }

    /// <summary>true se o processo atual roda como administrador.</summary>
    internal static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Nome da impressora padrão: dos fatos, se informados; senão do WMI. Null se não houver.</summary>
    /// <param name="facts">Fatos da varredura ou null.</param>
    internal static string? DefaultPrinterName(SystemFacts? facts)
    {
        if (facts is not null) return facts.Printers.FirstOrDefault(p => p.IsDefault)?.Name;
        using var o = Wmi.First("SELECT Name FROM Win32_Printer WHERE Default = TRUE");
        return o?.S("Name");
    }

    /// <summary>true se há bateria: dos fatos, se informados; senão do WMI.</summary>
    /// <param name="facts">Fatos da varredura ou null.</param>
    private static bool HasBattery(SystemFacts? facts)
    {
        if (facts is not null) return facts.HasBattery || facts.IsLaptop;
        using var o = Wmi.First("SELECT Name FROM Win32_Battery");
        return o is not null;
    }

    /// <summary>true se o registro indica Proteção do Sistema desligada (política DisableSR = 1 ou RPSessionInterval = 0). Sem esses valores, não afirma nada e devolve false.</summary>
    private static bool SystemProtectionOff()
    {
        const string cfg = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        const string pol = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
        return Reg.GetInt(pol, "DisableSR") == 1 || Reg.GetInt(cfg, "DisableSR") == 1 || Reg.GetInt(cfg, "RPSessionInterval") == 0;
    }
}
