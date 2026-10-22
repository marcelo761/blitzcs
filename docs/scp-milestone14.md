# M14 — camada FMOD e compilação integrada

O bloqueio `FMod.bb:15`, `FSOUND_Init`, foi resolvido com uma implementação
real em `Blitz3DCS.Audio`, separada do compilador. A fachada abre um dispositivo
SDL3 e o fecha explicitamente. O compilador continua sem dependência de engine.

## Progresso sobre os fontes originais

| Evidência | M13 | M14 |
|---|---|---|
| Sintaxe integrada Main + 23 alvos Include | aprovada | aprovada |
| Primeiro diagnóstico integrado | FMod.bb:15, FSOUND_Init | fullscreen_window_fix.bb:45:23, SystemProperty |
| Arquivos isolados com assembly | 2/30 | 3/30 |
| Novo arquivo isolado compilado | — | FMod.bb |
| Arquivos isolados com sintaxe aprovada | 27/30 | 27/30 |
| Assembly integrado / jogo executado | não / não | não / não |

Difficulty.bb e KeyName.bb continuam compilando. Os mesmos 30 hashes do
snapshot foram preservados. Não foram editados fontes do jogo ou usados mocks.
Os primeiros diagnósticos isolados são: 3 compilados, 2 sintáticos de contexto,
9 semânticos/tipos, 15 nativos não registrados e 1 dependência externa.
A árvore estática tem profundidade máxima 2; o novo diagnóstico está no nível
1 (Main → fullscreen_window_fix). Isso não mede resolução completa de toda a
árvore: a análise ainda interrompe no primeiro erro, e o total posterior é
**desconhecido**. `SystemProperty` continua sem implementação registrada.

## Contratos e execução

A [separação de estágios](external-signatures-m14.md) permite declarar tipos
sem executar uma biblioteca. `check-semantics` informa chamadas sem vínculo;
`build` recusa uma chamada usada sem implementação, apontando o `.bb` original.
Uma declaração não é um stub, e declarações não usadas não impedem emissão.
Os catálogos antigos e chamadas estáticas tipadas foram preservados.

A [decisão arquitetural](audio-architecture-m14.md) compara worker FMOD x86,
miniaudio e SDL3. O perfil executado é Linux x64/SDL 3.4.18. A publicação foi
testada em diretório isolado copiando o assembly gerenciado e `libSDL3.so.0`;
a distribuição completa entre sistemas ainda exige empacotar SDL e suas
próprias dependências/licenças. O catálogo de exemplo usa o SDL instalado no
sistema, não inclui binários de terceiros no Git.

O [inventário FMOD](fmod-audit-m14.md) contém 13 declarações, 10 símbolos ativos,
14 pontos de chamada e nenhuma chamada FMUSIC. Expõe convenções StdCall x86,
exports decorados, tipos e diferenças entre o `.decls` e o header 3.75.
`FSOUND_Stream_Open` tem três argumentos no jogo e quatro no header/export:
essa incompatibilidade permanece investigável, não foi escondida num binding.

A fachada Init retorna 0/1 normalizados. Close original é void; seu retorno
Integer 0 na fachada é apenas uma convenção de adaptação ao registro atual.
Só Init/Close estão implementados. A tabela interna de handles usa tokens
positivos sem reutilização, guarda ponteiros nativos sem truncamento, invalida
antes de liberar e libera todos os recursos mesmo quando um callback falha.
Ela é infraestrutura testada para streams futuros: Init/Close não expõem
ponteiros e ainda não consomem essa tabela. Objetos e handles BASIC não mudaram.

## Evidências e limitações

O [probe original](evidence/m14-fmod/init-semantics.json) executou a DLL FMOD
3.75 em processo Win32 x86 via Proton/NOSOUND. A
[comparação normalizada](evidence/m14-audio-comparison.json) com SDL dummy
obteve cinco coincidências e duas divergências deliberadas: flags 0x80 e
0x80000000 são aceitas pelo original nesse perfil e recusadas nesta fatia.
Essa comparação cobre somente resultados Init; não compara erro numérico,
mixer, codecs ou áudio produzido. Não se soma às fixtures de linguagem.

[Três execuções publicadas](evidence/m14-audio-runtime.json) confirmam Init/Close
com driver padrão e dummy (retorno 1), e falha real com driver inexistente
(retorno 0 e mensagem SDL). Não foi solicitado playback. A taxa é solicitada
ao dispositivo; não há implementação do mixer nem reserva de canais FSOUND.
Repetir configurações idênticas é aceito; mudar configuração exige Close,
restrição explícita até validação/implementação adicional. Somente flags=0
são suportadas. Strings/streams, canais e códigos FMOD_GetError continuam
pendentes. Não há renderer, física, áudio funcional do jogo ou compatibilidade
FMOD completa nesta entrega.

## Reprodução

```sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
dotnet test BlitzCS.slnx -c Debug
python3 tools/scp_compile_probe.py --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Audio/scp.json --check-syntax \
  --report artifacts/scp-m14-after.json
python3 tools/compare_audio_profile.py
```

O novo caso original é reproduzível com `tools/probe_fmod_original.py --case
init-semantics --output nosound`, além dos argumentos de DLL/relatório/evidência
e das variáveis Proton documentadas no M13. O SDK e a DLL não são distribuídos.
Os resultados finais de regressão estão no relatório de validação M14.

## Resultado final de validação

Release e Debug passaram **572 testes .NET**: 284 do compilador, 262 da CLI,
11 de interop nativa e 15 de áudio. Release compilou com zero avisos/erros;
Debug compilou sem diagnósticos de aviso/erro. Os 40 testes Python e as 86
fixtures locais passaram. As capturas locais atuais preservaram as 85/86
coincidências históricas usando oráculos M13/M12 de fontes com os mesmos hashes;
não houve nova captura desses 86 casos. O probe FMOD M14 é novo e separado.
O stress existente repetiu 2 ×100.000 New/Delete/Handle, com zero entradas de
handles e posições após coleta/manutenção em ambos os lotes.

Uma tentativa concorrente interrompida de validação ficou incompleta em Release
e registrou timeout num teste multifile em Debug. Sem enfraquecer o teste ou
alterar seu código, a reprodução isolada passou 2/2 e a repetição completa Debug
passou 572/572. A causa daquela ocorrência não foi estabelecida; ela não foi
ocultada nem classificada como defeito corrigido do produto. Os resultados finais
e as medições estão em [m14-validation.json](evidence/m14-validation.json).
