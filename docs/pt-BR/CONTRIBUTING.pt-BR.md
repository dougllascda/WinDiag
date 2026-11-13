# Contribuindo com o WinDiag

[English](../../.github/CONTRIBUTING.md) | **Português (Brasil)**

Obrigado pelo interesse. Este guia resume como compilar, testar e estender o projeto. A visão geral do código está em [docs/ARCHITECTURE.md](../ARCHITECTURE.md) (em inglês). O WinDiag tem código visível sob a [Licença do WinDiag](LICENSE.pt-BR.md) (versão em inglês: [LICENSE](../../LICENSE)); não é código aberto. Issues e sugestões em português ou inglês são bem-vindas; pull requests só são aceitos sob o Contrato de Licença de Contribuidor abaixo. Em caso de divergência, vale a versão em inglês, em [.github/CONTRIBUTING.md](../../.github/CONTRIBUTING.md).

## Issues e sugestões

Relatos de bugs, ideias e dúvidas são bem-vindos como issues no GitHub. Informe a versão do WinDiag, a versão do Windows e, em caso de bug, o que você esperava e o que aconteceu. Para problemas de segurança, veja [SECURITY.pt-BR.md](SECURITY.pt-BR.md).

## Contrato de Licença de Contribuidor (CLA)

Ao enviar uma contribuição (pull request, patch, código, texto, tradução ou qualquer outro material) para este repositório, você concorda com o seguinte:

1. **Autoria.** Você declara ser o autor da contribuição, ou ter o direito de enviá-la, e que ela não viola direitos de terceiros.
2. **Cessão.** Você cede e transfere a dougllascda todos os direitos patrimoniais sobre a contribuição, na máxima extensão permitida em lei.
3. **Licença subsidiária.** Onde a cessão não for possível ou válida pela lei aplicável, você concede a dougllascda uma licença perpétua, mundial, irrevogável, gratuita e exclusiva para usar, reproduzir, modificar, distribuir e explorar a contribuição de qualquer forma, com direito de sublicenciar.
4. **Sem obrigação.** dougllascda não tem obrigação de usar, manter ou publicar sua contribuição e decide sozinho o que entra no projeto.
5. **Sem remuneração.** A contribuição é feita sem pagamento.

Se você não concorda, não envie um pull request; abra uma issue. O modelo de pull request pede a confirmação deste contrato.

## Requisitos

- Windows 10 ou 11 (o app lê WMI, registro e contadores de desempenho do Windows).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- PowerShell 7 (`pwsh`) ou Windows PowerShell 5.1, para o `tools\publish.ps1`.

## Compilar e testar

```powershell
dotnet build app\WinDiag.sln -c Debug
dotnet test app\WinDiag.sln
```

O build em Debug usa o manifesto `asInvoker` (sem UAC). O Release pede administrador ao abrir. Corrija todos os erros e avisos novos antes de enviar. Compilar localmente é permitido apenas para avaliar o software e preparar contribuições sob o CLA; não distribua os binários gerados (veja a Licença do WinDiag).

## CLI de teste

A CLI roda sem interface e é a forma mais rápida de validar um coletor ou item de otimização:

```powershell
dotnet run --project app\WinDiag.Cli -- --dump saida.json      # varredura completa em JSON (stdout se sem arquivo)
dotnet run --project app\WinDiag.Cli -- --live 5               # 5 amostras ao vivo, uma por segundo
dotnet run --project app\WinDiag.Cli -- --fixes                # estado de cada otimização (não altera nada)
dotnet run --project app\WinDiag.Cli -- --dry-enable GAMEDVR   # simula ligar (nada é gravado)
dotnet run --project app\WinDiag.Cli -- --dry-disable GAMEDVR  # simula desfazer só esse item
dotnet run --project app\WinDiag.Cli -- --tools                # lista as ferramentas
dotnet run --project app\WinDiag.Cli -- --dry-tool NET_TEST    # mostra o que uma ferramenta executaria (não executa nada)
```

Rode em um terminal como administrador para ver as leituras completas.

## Convenções

- **Interface bilíngue.** Todo texto visível existe em português do Brasil e em inglês (`Loc.T(pt, en)`). O texto em pt-BR deve ter acentuação correta (arquivos em UTF-8). Números e datas seguem o idioma escolhido; em pt-BR, as datas são `dd/MM/yyyy`.
- **Tom sóbrio e técnico.** Nada de "100% seguro". **Não afirme causa sem medição**: descreva o que foi lido e o que isso pode indicar.
- **Comentários XML** (`///`) em tipos e membros públicos.
- **Coletores são somente leitura** e nunca derrubam o app. Cada leitura em `try/catch`; falha vira nota (ex.: "exige administrador") e o campo mostra `n/d`.
- **Todo ajuste passa pelo `FixEngine`.** Ele salva antes um snapshot em `%ProgramData%\WinDiagMaint\snapshots` e tem desfazer.
- **Toda ação de um clique passa pelo `ToolRunner`** (aba Ferramentas). Tem comandos fixos, não gera snapshot, sempre pede confirmação mostrando os comandos e é registrada em `changes.log` e `tools-history.json`. Desinstalações só abrem o desinstalador do próprio programa, nunca em modo silencioso.
- Processos externos (`nvidia-smi`, `powercfg`, `bcdedit`, `fltmc`) via `Util.Proc`: timeout e sem janela.
- Não use ofuscação, packer, download em tempo de execução, `Reflection.Emit` ou código gerado em runtime. P/Invoke documentado da Win32 é permitido.
- Não mascare dados na interface; o mascaramento de usuário e seriais vale só para o relatório exportado.
- Terminações de linha e codificação são definidas por `.editorconfig` e `.gitattributes`.

## Como adicionar um coletor

1. Crie `app\WinDiag.Core\Collectors\MeuCollector.cs` implementando `ICollector` (`Key`, `Title`, `Collect(SystemFacts)`).
2. Monte o `SectionResult`: grupos com `Group(...).Add(rótulo, valor)`, tabelas, `Find(...)` para verificações e `Note(...)` para leituras parciais.
3. Se outros componentes precisarem de um dado, adicione uma propriedade em `SystemFacts` e preencha no coletor.
4. Registre o coletor em `ScanEngine.CreateCollectors()` (a ordem define a ordem das abas e do relatório).
5. Crie a aba correspondente no App, se for uma nova área.
6. Adicione um teste em `app\WinDiag.Tests` e confirme com `--dump`.

## Como adicionar uma regra de análise

As regras ficam em `app\WinDiag.Core\Rules\` e trabalham sobre `SystemFacts` e os `Finding` já coletados (seção "Análise da plataforma", fase C).

1. Escreva a regra como função pura sobre os fatos, sem E/S.
2. Use somente evidência medida; se o dado for `n/d`, a regra não conclui nada.
3. Gere um `Finding` com `Evidence`, `Explain` ("o que significa"), `Action` ("o que fazer") e `Impact` (0 a 100).
4. Cubra os limiares com testes, incluindo o caso sem dado.

## Como adicionar um item de otimização

1. Em `FixCatalog.All`, adicione uma `FixDefinition` com `Id` único, `Name`, `ShortName`, `Risk`, `Reboot`, `Explain`, os alvos (`Reg`, `Svc`, `Bcd`, `Power`...), `Applicable` e `IsActive`.
2. Se o alvo é de um tipo novo, estenda `FixState`/`Capture`/`Apply` no `FixEngine` e garanta que o estado "antes" é gravado e restaurado.
3. Itens que não podem ser restaurados não são otimizações: faça deles uma ferramenta (abaixo).
4. Teste com `--fixes` e `--dry-enable ID` / `--dry-disable ID`, e adicione testes em `FixTests` (use `SnapshotStore` com pasta temporária).

## Como adicionar uma ferramenta

Ferramentas são ações de um clique que rodam uma vez e não são interruptores (veja [docs/modules/tools.md](../modules/tools.md), em inglês). Uma contribuição de ferramenta precisa de:

1. Um `ToolDefinition` em `ToolCatalog` com `Id` único, textos bilíngues, categoria, linhas de comando fixas e os indicadores de administrador, reinício e risco.
2. Comandos fixos no código. Sem download, sem scripts gerados ou arquivos `.bat`, e a ferramenta nunca reinicia o PC.
3. Suporte a `DryRun` que lista os comandos exatos e não altera nada. Confira com `--dry-tool ID` (e liste o catálogo com `--tools`).
4. Um `BlockReason` para os casos em que ela não deve rodar.
5. Comandos executados por `Proc.RunStreaming` (timeout, sem janela, cancelável).
6. Testes do catálogo, do `DryRun` e do `BlockReason`.
7. Se a ação é reversível e mantém uma configuração alterada, ela é uma otimização, não uma ferramenta.

## Como publicar

```powershell
pwsh -File tools\publish.ps1                # testa e gera dist\
pwsh -File tools\publish.ps1 -SkipTests     # só gera dist\
pwsh -File tools\publish.ps1 -Scan          # e varre dist\ com o Kaspersky, se instalado
```

Saída em `dist\`: `WinDiag-Setup-<versão>.exe`, `WinDiag-Portable-<versão>.exe`, `WinDiag-<versão>-fx.exe` e `SHA256SUMS.txt`. O processo de release está em [docs/RELEASING.md](../RELEASING.md) (em inglês).

## Pull requests

Descreva o que mudou e como validou (comandos e resultado). Mantenha mudanças pequenas e focadas, e atualize o `CHANGELOG.md` quando houver impacto para o usuário. O modelo de pull request traz uma lista de verificação, incluindo a confirmação do CLA. O titular decide o que é incorporado.
