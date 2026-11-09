# Segurança

[English](../../.github/SECURITY.md) | **Português (Brasil)**

## Princípios

- **Leitura por padrão:** o WinDiag só altera algo quando você liga um interruptor e confirma. Itens de risco alto exigem digitar `CONFIRMO`.
- **Reversível:** antes de cada alteração, o estado anterior vai para `%ProgramData%\WinDiagMaint\snapshots\`. Desligar o interruptor ou restaurar um snapshot volta ao valor salvo.
- **Sem rede:** o app não baixa, não envia e não atualiza nada sozinho, e não tem telemetria.
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

Os executáveis ainda não são assinados, e o WinDiag altera configurações do sistema (registro, BCD, energia) quando você pede. Por isso, alguns antivírus podem desconfiar dele. Cada release é verificada com o Kaspersky antes de ser publicada. Se o seu antivírus acusar algo, abra uma issue com o nome da detecção e a versão.

## Relatar uma vulnerabilidade

Não publique detalhes exploráveis nem prova de conceito antes de existir correção. Forma preferida: o relato privado de vulnerabilidades do GitHub (botão "Report a vulnerability" na aba **Security** do repositório), se estiver habilitado. Caso contrário, abra uma issue marcada como `security`, sem detalhes exploráveis, e peça um canal privado, ou fale com [@dougllascda](https://github.com/dougllascda) no GitHub.
