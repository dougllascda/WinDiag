# Segurança

[English](../../.github/SECURITY.md) | **Português (Brasil)**

## Princípios

- **Leitura por padrão:** o WinDiag só altera algo quando você liga um interruptor ou executa uma ferramenta, e confirma. Itens de risco alto exigem digitar `CONFIRMO`.
- **Otimizações reversíveis:** antes de cada alteração, o estado anterior vai para `%ProgramData%\WinDiagMaint\snapshots\`. Desligar o interruptor ou restaurar um snapshot volta ao valor salvo.
- **Ferramentas explícitas:** as ferramentas de um clique não geram snapshot. Os comandos são fixos no código e aparecem antes de você confirmar (as somente leitura sem administrador rodam direto); nenhum código de terceiros é baixado e nenhum script é gerado. Cada execução é registrada em `changes.log` e em `%ProgramData%\WinDiagMaint\tools-history.json`. O WinDiag oferece um ponto de restauração antes das de risco médio e nunca reinicia o PC.
- **Sem telemetria. Uso restrito da rede:** O WinDiag não baixa nem executa código de terceiros. A verificação de atualizações (frequência configurável, padrão semanal, pode ser Manual) consulta apenas o repositório oficial; o download e a instalação só acontecem com o seu clique, com o arquivo conferido por SHA-256. As ferramentas de rede (teste de conexão: ping em 1.1.1.1 e 8.8.8.8 e uma resolução de nome; sincronizar relógio) e o DISM usam a internet quando você as executa.
- **Modelo de confiança da atualização:** a verificação lê só a versão mais recente do repositório oficial. O download aceita apenas `https://github.com/dougllascda/WinDiag/releases/download/...` do mesmo release do `SHA256SUMS.txt`, vai para uma pasta protegida (`%ProgramData%\WinDiagMaint\update`, escrita só por Administradores e SYSTEM) quando o app está elevado, e só é aplicado se o SHA-256 conferir. O arquivo fica aberto em somente leitura entre a conferência e a execução. Como o `SHA256SUMS.txt` vem do mesmo release, ele garante integridade, não autoria: os executáveis ainda não têm assinatura de código. Para prova de origem, use `gh attestation verify`. Detalhes: [docs/modules/updates.md](../modules/updates.md) (em inglês).
- **Sem código oculto:** não há ofuscação, empacotador, código gerado em tempo de execução nem arquivos temporários executáveis. As únicas chamadas nativas são APIs documentadas do Windows, para modos de vídeo.
- **Privacidade no relatório:** o relatório exportado mascara o nome de usuário e os números de série. A interface mostra os dados completos só para você.

## Versões suportadas

Correções de segurança são feitas apenas na versão mais recente.

## Verificar um download

Compare o hash com o `SHA256SUMS.txt` do Release:

```powershell
Get-FileHash .\WinDiag-Setup-<versão>.exe -Algorithm SHA256
```

Confirme que o arquivo foi gerado pelo workflow de release deste repositório:

```powershell
gh attestation verify .\WinDiag-Setup-<versão>.exe --repo dougllascda/WinDiag
```

Veja [docs/SIGNING.md](../SIGNING.md) (em inglês) para o estado da assinatura de código e mais detalhes.

## Sobre hashes e atestados

- **SHA256 não é criptografia e não esconde o código.** É uma impressão digital do arquivo, usada para detectar adulteração ou corrupção no download.
- Quem compilar uma versão modificada gera outro hash, que não bate com o `SHA256SUMS.txt` do release oficial.
- O **atestado de build** prova que o arquivo saiu do workflow deste repositório. Não dá para forjá-lo sem acesso à conta dona do repositório.
- A **assinatura digital** acrescenta a identidade do autor (veja [docs/SIGNING.md](../SIGNING.md)).

## Falsos positivos de antivírus

Os executáveis ainda não são assinados, e o WinDiag altera configurações do sistema (registro, BCD, energia) e executa comandos de manutenção (por exemplo `netsh`, `sfc`, `DISM`) quando você pede. Por isso, alguns antivírus podem desconfiar dele. Cada release é verificada com o Kaspersky antes de ser publicada. Se o seu antivírus acusar algo, abra uma issue com o nome da detecção e a versão.

## Relatar uma vulnerabilidade

Não publique detalhes exploráveis nem prova de conceito antes de existir correção. Forma preferida: o relato privado de vulnerabilidades do GitHub (botão "Report a vulnerability" na aba **Security** do repositório), se estiver habilitado. Caso contrário, abra uma issue marcada como `security`, sem detalhes exploráveis, e peça um canal privado, ou fale com [@dougllascda](https://github.com/dougllascda) no GitHub.
