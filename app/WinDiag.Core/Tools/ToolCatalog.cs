using WinDiag.Core.Fixes;
using WinDiag.Core.Model;

namespace WinDiag.Core.Tools;

/// <summary>Grupo de uma ferramenta, usado para organizar a tela de ferramentas.</summary>
public enum ToolCategory
{
    /// <summary>Impressão: fila, Spooler e página de teste.</summary>
    Printing,
    /// <summary>Rede: teste de qualidade, DNS, IP, pilha de rede e horário.</summary>
    Network,
    /// <summary>Limpeza de espaço em disco.</summary>
    Cleanup,
    /// <summary>Reparo do Windows: arquivos do sistema, atualizações, memória e pontos de restauração.</summary>
    Repair,
    /// <summary>Jogos: cache de shaders.</summary>
    Gaming,
    /// <summary>Relatórios gerados pelo Windows.</summary>
    Reports,
    /// <summary>Atalhos que apenas abrem uma tela do Windows.</summary>
    Shortcuts,
}

/// <summary>Descrição de uma ferramenta: o que faz, o que exige e exatamente quais comandos executa. A execução fica no <see cref="ToolRunner"/>.</summary>
public sealed class ToolDefinition
{
    /// <summary>Identificador estável (ex.: "NET_DNS_FLUSH").</summary>
    public required string Id { get; init; }
    /// <summary>Nome nos dois idiomas (português, inglês).</summary>
    public required (string Pt, string En) NameText { get; init; }
    /// <summary>Explicação nos dois idiomas (português, inglês): o que faz e quais as contrapartidas.</summary>
    public required (string Pt, string En) ExplainText { get; init; }
    /// <summary>Nome no idioma atual.</summary>
    public string Name => Loc.T(NameText.Pt, NameText.En);
    /// <summary>Explicação no idioma atual.</summary>
    public string Explain => Loc.T(ExplainText.Pt, ExplainText.En);
    /// <summary>Grupo da ferramenta.</summary>
    public ToolCategory Category { get; init; }
    /// <summary>Perfil a que a ferramenta se destina (Both = todos).</summary>
    public Scope Scope { get; init; } = Scope.Both;
    /// <summary>Risco exibido ao usuário antes de executar.</summary>
    public FixRisk Risk { get; init; } = FixRisk.Baixo;
    /// <summary>true se exige executar o WinDiag como administrador.</summary>
    public bool NeedsAdmin { get; init; }
    /// <summary>true se o efeito completo exige reiniciar o Windows (a ferramenta nunca reinicia sozinha).</summary>
    public bool NeedsRestart { get; init; }
    /// <summary>true se a ferramenta só lê ou abre telas, sem alterar nada.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>true se pode demorar e deve mostrar o botão Cancelar.</summary>
    public bool LongRunning { get; init; }
    /// <summary>true se o usuário escolhe opções antes de executar (veja <see cref="ToolRunner.GetOptions"/>).</summary>
    public bool HasOptions { get; init; }
    /// <summary>Duração aproximada nos dois idiomas (português, inglês).</summary>
    public (string Pt, string En) DurationText { get; init; } = ("alguns segundos", "a few seconds");
    /// <summary>Duração aproximada no idioma atual.</summary>
    public string Duration => Loc.T(DurationText.Pt, DurationText.En);
    /// <summary>Linhas de comando nos dois idiomas (português, inglês); veja <see cref="Commands"/>.</summary>
    public IReadOnlyList<(string Pt, string En)> CommandLines { get; init; } = Array.Empty<(string Pt, string En)>();
    /// <summary>Linhas mostradas ao usuário no idioma atual: exatamente o que será executado (comandos fixos, escritos no código).</summary>
    public IReadOnlyList<string> Commands => CommandLines.Select(c => Loc.T(c.Pt, c.En)).ToList();
}

/// <summary>Opção escolhível de uma ferramenta (ex.: cada pasta da limpeza de disco).</summary>
/// <param name="Id">Identificador estável da opção.</param>
/// <param name="Label">Rótulo no idioma atual.</param>
/// <param name="Bytes">Tamanho medido em bytes; null se não foi possível medir (exibir "n/d").</param>
/// <param name="DefaultOn">true se a opção vem marcada.</param>
/// <param name="Note">Motivo de o tamanho não ter sido lido (ex.: exige administrador); null se não há nota.</param>
public sealed record ToolOption(string Id, string Label, long? Bytes, bool DefaultOn, string? Note = null);

/// <summary>Catálogo fixo das ferramentas do WinDiag. Cada uma altera só o que descreve em <see cref="ToolDefinition.Commands"/>.</summary>
public static class ToolCatalog
{
    /// <summary>Todas as ferramentas, na ordem de exibição.</summary>
    public static IReadOnlyList<ToolDefinition> All { get; } = new List<ToolDefinition>
    {
        // ------------------------------------------------------------ impressão
        new() { Id = "PRINT_UNSTICK", Category = ToolCategory.Printing, Risk = FixRisk.Medio, NeedsAdmin = true,
            NameText = ("Destravar impressão (limpar a fila)", "Unstick printing (clear the queue)"),
            ExplainText = ("Para o Spooler, apaga os arquivos da fila de impressão e inicia o Spooler de novo. Os documentos que estavam na fila são perdidos e precisam ser enviados outra vez. Não há como restaurar a fila.",
                "Stops the Spooler, deletes the print queue files and starts the Spooler again. Documents that were in the queue are lost and must be sent again. The queue cannot be restored."),
            CommandLines = new (string Pt, string En)[] { (@"sc stop Spooler", @"sc stop Spooler"), (@"apagar os arquivos de %SystemRoot%\System32\spool\PRINTERS", @"delete the files in %SystemRoot%\System32\spool\PRINTERS"), (@"sc start Spooler (e os serviços dependentes que estavam em execução)", @"sc start Spooler (and the dependent services that were running)") } },

        new() { Id = "SPOOLER_RESTART", Category = ToolCategory.Printing, NeedsAdmin = true,
            NameText = ("Reiniciar o serviço de impressão (Spooler)", "Restart the print service (Spooler)"),
            ExplainText = ("Para e inicia o serviço Spooler do Windows. Costuma destravar impressões paradas e não apaga os documentos da fila.",
                "Stops and starts the Windows Spooler service. It usually unblocks stalled printing and does not delete the documents in the queue."),
            CommandLines = new (string Pt, string En)[] { (@"sc stop Spooler", @"sc stop Spooler"), (@"sc start Spooler (e os serviços dependentes que estavam em execução)", @"sc start Spooler (and the dependent services that were running)") } },

        new() { Id = "PRINT_TEST_PAGE", Category = ToolCategory.Printing,
            NameText = ("Imprimir página de teste", "Print a test page"),
            ExplainText = ("Envia a página de teste do Windows para a impressora padrão, para saber se o problema está na impressora ou no programa.",
                "Sends the Windows test page to the default printer, to tell whether the problem is the printer or the program."),
            CommandLines = new (string Pt, string En)[] { (@"rundll32 printui.dll,PrintUIEntry /k /n ""<impressora padrão>""", @"rundll32 printui.dll,PrintUIEntry /k /n ""<default printer>""") } },

        // ------------------------------------------------------------ rede
        new() { Id = "NET_TEST", Category = ToolCategory.Network, ReadOnly = true, LongRunning = true,
            NameText = ("Testar a qualidade da conexão", "Test connection quality"),
            ExplainText = ("Envia 10 pings ao roteador (gateway padrão), a 1.1.1.1 e a 8.8.8.8 e resolve um nome por DNS. Mostra média, oscilação (jitter) e perda por destino e um veredito simples: se o problema parece estar no Wi-Fi ou roteador, no provedor ou se está tudo certo. Não altera nada. É uma medida de poucos segundos: repita se o resultado for duvidoso.",
                "Sends 10 pings to the router (default gateway), to 1.1.1.1 and to 8.8.8.8 and resolves a name through DNS. Shows average, jitter and loss per destination and a simple verdict: whether the problem looks like Wi-Fi or router, the provider, or everything is fine. It changes nothing. It is a measurement of a few seconds: repeat it if the result is doubtful."),
            DurationText = ("cerca de 20 segundos", "about 20 seconds"),
            CommandLines = new (string Pt, string En)[] { (@"ping x10 no gateway padrão (API do .NET, sem janela)", @"ping x10 to the default gateway (.NET API, no window)"), (@"ping x10 em 1.1.1.1", @"ping x10 to 1.1.1.1"), (@"ping x10 em 8.8.8.8", @"ping x10 to 8.8.8.8"), (@"resolução de nome por DNS (Dns.GetHostAddresses)", @"name resolution through DNS (Dns.GetHostAddresses)") } },

        new() { Id = "NET_DNS_FLUSH", Category = ToolCategory.Network, NeedsAdmin = true,
            NameText = ("Limpar o cache de DNS", "Flush the DNS cache"),
            ExplainText = ("Apaga os endereços de sites guardados pelo Windows. Ajuda quando um site não abre ou abre o endereço antigo depois de uma mudança. O Windows refaz o cache sozinho.",
                "Clears the site addresses Windows has stored. It helps when a site does not open or opens the old address after a change. Windows rebuilds the cache by itself."),
            CommandLines = new (string Pt, string En)[] { (@"ipconfig /flushdns", @"ipconfig /flushdns") } },

        new() { Id = "NET_IP_RENEW", Category = ToolCategory.Network, NeedsAdmin = true,
            NameText = ("Renovar o endereço IP", "Renew the IP address"),
            ExplainText = ("Devolve o endereço IP ao roteador e pede um novo. A conexão cai por alguns segundos. Não funciona em adaptador com IP fixo.",
                "Returns the IP address to the router and asks for a new one. The connection drops for a few seconds. It does not apply to an adapter with a fixed IP."),
            DurationText = ("alguns segundos (a conexão cai)", "a few seconds (the connection drops)"),
            CommandLines = new (string Pt, string En)[] { (@"ipconfig /release", @"ipconfig /release"), (@"ipconfig /renew", @"ipconfig /renew") } },

        new() { Id = "NET_STACK_RESET", Category = ToolCategory.Network, Risk = FixRisk.Medio, NeedsAdmin = true, NeedsRestart = true,
            NameText = ("Redefinir a pilha de rede (Winsock e IP)", "Reset the network stack (Winsock and IP)"),
            ExplainText = ("Volta o Winsock e a configuração TCP/IP ao padrão do Windows. Pode resolver conexões que falham sem motivo claro, mas remove ajustes de VPN, firewall ou outros programas de rede, que podem precisar ser reinstalados, e apaga IPs fixos. Só vale depois de reiniciar o PC; o WinDiag não reinicia sozinho.",
                "Returns Winsock and the TCP/IP configuration to the Windows default. It may fix connections that fail for no clear reason, but it removes settings from VPN, firewall or other network programs, which may need to be reinstalled, and erases fixed IPs. It only takes effect after restarting the PC; WinDiag does not restart by itself."),
            CommandLines = new (string Pt, string En)[] { (@"netsh winsock reset", @"netsh winsock reset"), (@"netsh int ip reset", @"netsh int ip reset") } },

        new() { Id = "TIME_SYNC", Category = ToolCategory.Network, NeedsAdmin = true,
            NameText = ("Sincronizar o relógio com a internet", "Sync the clock with the internet"),
            ExplainText = ("Inicia o serviço de horário do Windows se estiver parado e pede uma sincronização. Relógio errado costuma causar falhas de certificado em sites e programas.",
                "Starts the Windows time service if it is stopped and asks for a sync. A wrong clock often causes certificate failures in sites and programs."),
            CommandLines = new (string Pt, string En)[] { (@"sc start w32time (somente se estiver parado)", @"sc start w32time (only if stopped)"), (@"w32tm /resync", @"w32tm /resync") } },

        // ------------------------------------------------------------ limpeza
        new() { Id = "DISK_CLEANUP", Category = ToolCategory.Cleanup, HasOptions = true, LongRunning = true,
            NameText = ("Limpeza de disco", "Disk cleanup"),
            ExplainText = ("Apaga arquivos descartáveis das pastas que você marcar: temporários, cache do Windows Update e da Otimização de Entrega, miniaturas, relatórios de erro e, se quiser, a Lixeira. Arquivos em uso são pulados. Nada é restaurável depois de apagado. Nunca segue atalhos de pasta (junctions e links simbólicos) e só apaga dentro das pastas listadas.",
                "Deletes disposable files from the folders you tick: temporary files, Windows Update and Delivery Optimization caches, thumbnails, error reports and, if you wish, the Recycle Bin. Files in use are skipped. Nothing can be restored once deleted. It never follows folder shortcuts (junctions and symbolic links) and only deletes inside the listed folders."),
            DurationText = ("de alguns segundos a alguns minutos", "from a few seconds to a few minutes"),
            CommandLines = new (string Pt, string En)[] { (@"temporários do usuário (%TEMP%) e do Windows (%SystemRoot%\Temp)", @"user temporary files (%TEMP%) and Windows temporary files (%SystemRoot%\Temp)"), (@"cache do Windows Update (%SystemRoot%\SoftwareDistribution\Download; para e religa wuauserv e bits)", @"Windows Update cache (%SystemRoot%\SoftwareDistribution\Download; stops and restarts wuauserv and bits)"), (@"cache da Otimização de Entrega (Delivery Optimization)", @"Delivery Optimization cache"), (@"miniaturas (%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db)", @"thumbnails (%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db)"), (@"relatórios de erro do Windows (WER)", @"Windows Error Reporting files (WER)"), (@"Lixeira (SHEmptyRecycleBin; só se marcada)", @"Recycle Bin (SHEmptyRecycleBin; only if ticked)") } },

        // ------------------------------------------------------------ jogos
        new() { Id = "SHADER_CACHE_CLEAR", Category = ToolCategory.Gaming,
            NameText = ("Limpar o cache de shaders", "Clear the shader cache"),
            ExplainText = ("Apaga o cache de shaders do DirectX, da NVIDIA e da AMD. Pode corrigir falhas gráficas causadas por cache corrompido depois de atualizar o driver. Os jogos recriam o cache e podem engasgar um pouco nas primeiras partidas. Arquivos em uso são pulados.",
                "Deletes the DirectX, NVIDIA and AMD shader caches. It may fix graphics glitches caused by a corrupted cache after a driver update. Games rebuild the cache and may stutter a little in the first sessions. Files in use are skipped."),
            DurationText = ("alguns segundos", "a few seconds"),
            CommandLines = new (string Pt, string En)[] { (@"apagar %LOCALAPPDATA%\D3DSCache", @"delete %LOCALAPPDATA%\D3DSCache"), (@"apagar %LOCALAPPDATA%\NVIDIA\DXCache e \GLCache", @"delete %LOCALAPPDATA%\NVIDIA\DXCache and \GLCache"), (@"apagar %LOCALAPPDATA%\AMD\DxCache e \DxcCache", @"delete %LOCALAPPDATA%\AMD\DxCache and \DxcCache") } },

        // ------------------------------------------------------------ reparo
        new() { Id = "SYS_SFC", Category = ToolCategory.Repair, NeedsAdmin = true, LongRunning = true,
            NameText = ("Verificar arquivos do sistema (SFC)", "Check system files (SFC)"),
            ExplainText = ("Compara os arquivos protegidos do Windows com a cópia original e repara os que estiverem alterados ou corrompidos. Demora e pode usar bastante disco. Se o SFC não conseguir reparar, rode antes o DISM.",
                "Compares the protected Windows files with the original copy and repairs those that are changed or corrupted. It takes a while and can use a lot of disk. If SFC cannot repair, run DISM first."),
            DurationText = ("10 a 20 min", "10 to 20 min"),
            CommandLines = new (string Pt, string En)[] { (@"sfc /scannow", @"sfc /scannow") } },

        new() { Id = "SYS_DISM", Category = ToolCategory.Repair, NeedsAdmin = true, LongRunning = true, Risk = FixRisk.Medio,
            NameText = ("Reparar a imagem do Windows (DISM)", "Repair the Windows image (DISM)"),
            ExplainText = ("Restaura a imagem de componentes do Windows usando o Windows Update como fonte. Exige conexão com a internet e demora. Costuma ser o passo antes de rodar o SFC de novo.",
                "Restores the Windows component store using Windows Update as the source. It requires an internet connection and takes a while. It is usually the step before running SFC again."),
            DurationText = ("10 a 30 min", "10 to 30 min"),
            CommandLines = new (string Pt, string En)[] { (@"DISM /Online /Cleanup-Image /RestoreHealth", @"DISM /Online /Cleanup-Image /RestoreHealth") } },

        new() { Id = "SYS_CHKDSK", Category = ToolCategory.Repair, NeedsAdmin = true, LongRunning = true, ReadOnly = true,
            NameText = ("Verificar o disco do sistema (CHKDSK, somente leitura)", "Check the system disk (CHKDSK, read-only)"),
            ExplainText = ("Faz uma verificação online do sistema de arquivos da unidade do Windows, sem corrigir nada e sem reiniciar. Se encontrar erros, o resultado diz o que fazer; o reparo é um passo à parte.",
                "Runs an online check of the Windows drive's file system, without fixing anything and without restarting. If it finds errors, the result says what to do; repair is a separate step."),
            DurationText = ("5 a 20 min", "5 to 20 min"),
            CommandLines = new (string Pt, string En)[] { (@"chkdsk <unidade do sistema> /scan", @"chkdsk <system drive> /scan") } },

        new() { Id = "WU_RESET", Category = ToolCategory.Repair, Risk = FixRisk.Medio, NeedsAdmin = true,
            NameText = ("Redefinir o Windows Update", "Reset Windows Update"),
            ExplainText = ("Para os serviços do Windows Update, renomeia as pastas SoftwareDistribution e catroot2 (o Windows recria ambas) e religa os serviços que estavam em execução. Pode resolver atualizações travadas, mas o histórico de atualizações some da tela e a próxima verificação demora mais. Uma cópia .old-<data> fica ao lado; a cópia antiga do WinDiag é apagada.",
                "Stops the Windows Update services, renames the SoftwareDistribution and catroot2 folders (Windows recreates both) and starts again the services that were running. It may fix stuck updates, but the update history disappears from the screen and the next check takes longer. A .old-<date> copy stays alongside; the previous WinDiag copy is deleted."),
            DurationText = ("cerca de 1 minuto", "about 1 minute"),
            CommandLines = new (string Pt, string En)[] { (@"sc stop wuauserv, bits, cryptsvc e msiserver (os que estiverem em execução)", @"sc stop wuauserv, bits, cryptsvc and msiserver (those that are running)"), (@"renomear %SystemRoot%\SoftwareDistribution para SoftwareDistribution.old-<data>", @"rename %SystemRoot%\SoftwareDistribution to SoftwareDistribution.old-<date>"), (@"renomear %SystemRoot%\System32\catroot2 para catroot2.old-<data>", @"rename %SystemRoot%\System32\catroot2 to catroot2.old-<date>"), (@"sc start dos serviços que estavam em execução", @"sc start for the services that were running") } },

        new() { Id = "EXPLORER_RESTART", Category = ToolCategory.Repair,
            NameText = ("Reiniciar o Explorador de Arquivos", "Restart File Explorer"),
            ExplainText = ("Encerra o explorer.exe da sua sessão e o inicia de novo. Resolve barra de tarefas ou menu Iniciar travados. As janelas do Explorador abertas fecham; os outros programas não são afetados.",
                "Ends the explorer.exe of your session and starts it again. It fixes a frozen taskbar or Start menu. Open File Explorer windows close; other programs are not affected."),
            CommandLines = new (string Pt, string En)[] { (@"encerrar explorer.exe da sua sessão", @"end explorer.exe of your session"), (@"explorer.exe", @"explorer.exe") } },

        new() { Id = "MEM_DIAG", Category = ToolCategory.Repair, ReadOnly = true,
            NameText = ("Diagnóstico de memória do Windows", "Windows Memory Diagnostic"),
            ExplainText = ("Abre a ferramenta do próprio Windows, que pergunta se você quer reiniciar agora ou na próxima vez. O WinDiag não agenda nada nem reinicia o PC.",
                "Opens the Windows tool itself, which asks whether you want to restart now or next time. WinDiag does not schedule anything or restart the PC."),
            CommandLines = new (string Pt, string En)[] { (@"mdsched.exe", @"mdsched.exe") } },

        new() { Id = "RESTORE_POINT", Category = ToolCategory.Repair, NeedsAdmin = true,
            NameText = ("Criar um ponto de restauração", "Create a restore point"),
            ExplainText = ("Cria um ponto de restauração chamado \"WinDiag <data>\". Exige a Proteção do Sistema ligada na unidade do Windows. Por padrão o Windows só cria um ponto a cada 24 horas; se houver um recente, ele pode recusar.",
                "Creates a restore point named \"WinDiag <date>\". It requires System Protection to be on for the Windows drive. By default Windows only creates one point every 24 hours; if there is a recent one, it may refuse."),
            DurationText = ("cerca de 1 minuto", "about 1 minute"),
            CommandLines = new (string Pt, string En)[] { ("WMI root\\default: SystemRestore.CreateRestorePoint(\"WinDiag <data>\", MODIFY_SETTINGS, BEGIN_SYSTEM_CHANGE)", "WMI root\\default: SystemRestore.CreateRestorePoint(\"WinDiag <date>\", MODIFY_SETTINGS, BEGIN_SYSTEM_CHANGE)") } },

        // ------------------------------------------------------------ relatórios
        new() { Id = "BATTERY_REPORT", Category = ToolCategory.Reports, ReadOnly = true,
            NameText = ("Relatório de bateria do Windows", "Windows battery report"),
            ExplainText = ("Gera o relatório de bateria do próprio Windows (capacidade de projeto, carga completa atual e histórico de uso) e o abre no navegador. O arquivo fica na pasta temporária.",
                "Generates the Windows battery report (design capacity, current full charge and usage history) and opens it in the browser. The file stays in the temporary folder."),
            CommandLines = new (string Pt, string En)[] { (@"powercfg /batteryreport /output ""%TEMP%\windiag-battery-report.html""", @"powercfg /batteryreport /output ""%TEMP%\windiag-battery-report.html"""), (@"abrir o arquivo HTML gerado", @"open the generated HTML file") } },

        // ------------------------------------------------------------ atalhos
        Shortcut("OPEN_DEVMGMT", ("Abrir o Gerenciador de Dispositivos", "Open Device Manager"), ("Abre a tela do Windows onde ficam os drivers e os dispositivos.", "Opens the Windows screen with drivers and devices."), "devmgmt.msc"),
        Shortcut("OPEN_PRINTERS", ("Abrir Impressoras e scanners", "Open Printers & scanners"), ("Abre a tela de impressoras das Configurações do Windows.", "Opens the printers page of Windows Settings."), "ms-settings:printers"),
        Shortcut("OPEN_APPS", ("Abrir Aplicativos instalados", "Open Installed apps"), ("Abre a tela de aplicativos instalados das Configurações do Windows.", "Opens the installed apps page of Windows Settings."), "ms-settings:appsfeatures"),
        Shortcut("OPEN_WINUPDATE", ("Abrir o Windows Update", "Open Windows Update"), ("Abre a tela do Windows Update nas Configurações do Windows.", "Opens the Windows Update page of Windows Settings."), "ms-settings:windowsupdate"),
        Shortcut("OPEN_DISKMGMT", ("Abrir o Gerenciamento de Disco", "Open Disk Management"), ("Abre a tela do Windows com os discos e as partições.", "Opens the Windows screen with disks and partitions."), "diskmgmt.msc"),
        Shortcut("OPEN_RELIABILITY", ("Abrir o Monitor de Confiabilidade", "Open Reliability Monitor"), ("Abre o histórico de falhas e eventos do Windows em linha do tempo (perfmon /rel).", "Opens the Windows failure and event history as a timeline (perfmon /rel)."), "perfmon /rel"),
    };

    /// <summary>Cria a definição de um atalho que apenas abre uma tela do Windows.</summary>
    /// <param name="id">Id da ferramenta.</param>
    /// <param name="name">Nome (português, inglês).</param>
    /// <param name="explain">Explicação (português, inglês).</param>
    /// <param name="target">Comando ou endereço que será aberto.</param>
    private static ToolDefinition Shortcut(string id, (string Pt, string En) name, (string Pt, string En) explain, string target) => new()
    {
        Id = id, Category = ToolCategory.Shortcuts, ReadOnly = true, NameText = name, ExplainText = explain,
        CommandLines = new[] { (target, target) },
    };

    /// <summary>Busca uma ferramenta pelo Id (sem diferenciar maiúsculas); null se não existir.</summary>
    /// <param name="id">Id da ferramenta.</param>
    public static ToolDefinition? Get(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nome do grupo no idioma atual.</summary>
    /// <param name="c">Grupo.</param>
    public static string CategoryName(ToolCategory c) => c switch
    {
        ToolCategory.Printing => Loc.T("Impressão", "Printing"),
        ToolCategory.Network => Loc.T("Rede", "Network"),
        ToolCategory.Cleanup => Loc.T("Limpeza", "Cleanup"),
        ToolCategory.Repair => Loc.T("Reparo do Windows", "Windows repair"),
        ToolCategory.Gaming => Loc.T("Jogos", "Gaming"),
        ToolCategory.Reports => Loc.T("Relatórios", "Reports"),
        _ => Loc.T("Atalhos", "Shortcuts"),
    };
}
