# Contribuindo com o WinDiag

[English](CONTRIBUTING.md) | **Português (Brasil)**

Obrigado pelo interesse. Este guia resume como compilar, testar e estender o projeto. A visão geral do código está em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (em inglês). Ao participar, você concorda em seguir o [Código de Conduta](CODE_OF_CONDUCT.md) (em inglês). Issues e pull requests em português ou inglês são bem-vindos.

## Requisitos

- Windows 10 ou 11 (o app lê WMI, registro e contadores de desempenho do Windows).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- PowerShell 7 (`pwsh`) ou Windows PowerShell 5.1, para o `publish.ps1`.

## Compilar e testar

```powershell
dotnet build app\WinDiag.sln -c Debug
dotnet test app\WinDiag.sln
```

O build em Debug usa o manifesto `asInvoker` (sem UAC). O Release pede administrador ao abrir. Corrija todos os erros e avisos novos antes de enviar.

## CLI de teste

A CLI roda sem interface e é a forma mais rápida de validar um coletor ou item de otimização:

```powershell
dotnet run --project app\WinDiag.Cli -- --dump saida.json      # varredura completa em JSON (stdout se sem arquivo)
dotnet run --project app\WinDiag.Cli -- --live 5               # 5 amostras ao vivo, uma por segundo
dotnet run --project app\WinDiag.Cli -- --fixes                # estado de cada otimização (não altera nada)
dotnet run --project app\WinDiag.Cli -- --dry-enable GAMEDVR   # simula ligar (nada é gravado)
dotnet run --project app\WinDiag.Cli -- --dry-disable GAMEDVR  # simula desfazer só esse item
```

Rode em um terminal como administrador para ver as leituras completas.

## Convenções

- **Interface bilíngue.** Todo texto visível existe em português do Brasil e em inglês (`Loc.T(pt, en)`). O texto em pt-BR deve ter acentuação correta (arquivos em UTF-8). Números e datas seguem o idioma escolhido; em pt-BR, as datas são `dd/MM/yyyy`.
- **Tom sóbrio e técnico.** Nada de "100% seguro". **Não afirme causa sem medição**: descreva o que foi lido e o que isso pode indicar.
- **Comentários XML** (`///`) em tipos e membros públicos.
- **Coletores são somente leitura** e nunca derrubam o app. Cada leitura em `try/catch`; falha vira nota (ex.: "exige administrador") e o campo mostra `n/d`.
- **Toda alteração no sistema passa pelo `FixEngine`**, com snapshot em `%ProgramData%\WinDiagMaint\snapshots` e desfazer.
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
3. Itens sem como restaurar devem ter `Reversible = false` e dizer isso no `Explain`.
4. Teste com `--fixes` e `--dry-enable ID` / `--dry-disable ID`, e adicione testes em `FixTests` (use `SnapshotStore` com pasta temporária).

## Como publicar

```powershell
pwsh -File publish.ps1                # testa e gera dist\
pwsh -File publish.ps1 -SkipTests     # só gera dist\
pwsh -File publish.ps1 -Scan          # e varre dist\ com o Kaspersky, se instalado
```

Saída em `dist\`: `WinDiag-Setup-<versão>.exe`, `WinDiag-Portable-<versão>.exe`, `WinDiag-<versão>-fx.exe` e `SHA256SUMS.txt`. O processo de release está em [docs/RELEASING.md](docs/RELEASING.md) (em inglês).

## Pull requests

Descreva o que mudou e como validou (comandos e resultado). Mantenha mudanças pequenas e focadas, e atualize o `CHANGELOG.md` quando houver impacto para o usuário. O modelo de pull request traz uma lista de verificação.
