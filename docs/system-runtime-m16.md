# M16 — SystemProperty e próximo bloqueio integrado

`SystemProperty(String) → String` agora é registrado nos catálogos Native/Audio
e implementado em `Blitz3DCS.Runtime.LegacySystem`, sem alterar o compilador.
Os comandos continuam chamadas estáticas tipadas; registrar uma assinatura não
executa a consulta nem comprova execução do jogo.

## Fonte original e contrato

Referência local pinada do Blitz3D: `bbruntime/bbruntime.cpp:46–48,154` registra
`$SystemProperty$property`; `gxruntime/gxruntime.cpp:1130–1190` compara nomes com
lowercase e retorna String. Propriedades desconhecidas retornam vazio; espaços e
NUL embutido não são removidos. O SCP usa AppHWND em fullscreen_window_fix.bb:45
e TempDir/TEMPDIR em Blitz_File_ZipApi.bb:401,402,429; Map Creator também usa AppHWND.

| Propriedade | Runtime original | Implementação e limites M16 |
|---|---|---|
| tempdir | GetTempPath, separador final | Path.GetTempPath real, separadores legados e final preservado |
| appdir | Diretório do executável | AppContext.BaseDirectory do assembly publicado |
| cpu | Constante Intel | Arquitetura real do processo .NET; divergência deliberada |
| os | Tabela Win32 até Windows 7; vazio fora dela | Descrição real do SO; divergência deliberada |
| windowsdir/systemdir | Diretórios Win32 | BCL real no Windows; falha explícita no Linux; ramo Windows não executado nesta sessão |
| AppHWND/AppHInstance e interfaces Direct* | Handles/ponteiros do host Win32 | PlatformNotSupportedException: não existe integração de janela/engine neste runtime |
| desconhecida | String vazia | String vazia |

Não se retorna zero, um pointer truncado ou HWND fictício para avançar compilação.
A consulta AppHWND **resolve semanticamente e vincula**, mas **falha em execução**
na integração atual. Não há alegação de compatibilidade completa do wrapper de
janela do SCP no Linux. Um futuro host deverá fornecer uma integração explícita e
segura, junto das APIs que consomem os identificadores; isso não foi iniciado aqui.

## Evidência original e execução .NET

[O probe genuíno](evidence/m16-original/system-property.json) rodou o Blitz3D 1.118
Win32 via Proton. Confirmou desconhecida vazia, CPU sem distinção de caixa,
TempDir não vazio com separador final, e AppHWND real não zero. O OS retornou
vazio naquele Wine; CPU retornou Intel. A captura preserva os valores brutos,
que não são constantes esperadas para outros processos ou plataformas.

[A comparação](evidence/m16-comparison.json) executou um assembly híbrido .NET:
as quatro observações determinísticas compartilhadas coincidiram. O AppHWND
original não zero é registrado como divergência de capacidade: no .NET o build
vincula, e a execução termina com erro explícito na linha BASIC original.
Não se normalizam caminhos/OS/CPU distintos para inventar igualdade.
O instrumento [probe_system_property_original.py](../tools/probe_system_property_original.py)
permite repetir a execução original; exige o SDK, Wine/prefix e Xvfb documentados.

## Compilação integrada

Os 30 hashes do SCP permanecem idênticos à baseline M15. A análise avançou de
`fullscreen_window_fix.bb:45:23`, SystemProperty, para
**`fullscreen_window_fix.bb:47:8`, Windowed3D** (símbolo nativo não registrado).
O resultado continua **3/30** arquivos isolados com assembly. A sintaxe Main
mais 23 alvos Include passa; resolução completa, vinculação e emissão do jogo
não foram alcançadas. Nenhum jogo foi executado.

Windowed3D consulta capacidade gráfica; não é um comando simples de sistema
que possa ser honestamente implementado sem integração gráfica. Ficou fora do
escopo, assim como renderer, física e alterações arquiteturais grandes.
[A auditoria completa](evidence/m16-scp.json) registra o primeiro erro real e
as categorias, sem estimar erros posteriores ainda não observados.

## Reprodução

```sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
dotnet build BlitzCS.slnx -c Debug
dotnet test BlitzCS.slnx -c Debug --no-build
python3 tools/scp_compile_probe.py --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Audio/scp.json --check-syntax \
  --report artifacts/scp-m16-after.json
```

Os exemplos versionados são `samples/Native/system-property.bb` e
`system-property-unsupported.bb`. O segundo deve compilar e terminar com erro
de capacidade no runtime atual. Os resultados finais estão em m16-validation.json
na [pasta de evidências](evidence/m16-validation.json). As próximas tarefas estão
registradas neste relatório; notas locais de retomada não integram a distribuição.

## Regressões finais

Release e Debug: **611 testes .NET** (284 compilador, 272 CLI, 11 interop nativa,
44 áudio), zero falhas/skips, builds com zero avisos/erros. Os 601 testes anteriores
permanecem; foram adicionados 10 testes. Passaram também 40 Python e 86 fixtures
locais. As 85/86 coincidências de linguagem foram verificadas com oráculos
anteriores, não recapturados. A divergência upper-bound-error permanece explícita.
A pipeline anterior inteira e o novo trecho M16 de verify.sh foram executados;
o trecho novo foi executado separadamente após ser acrescentado ao script.
[Evidência final](evidence/m16-validation.json).
