# Handoff M15 — áudio funcional; compatibilidade total ainda não certificada

## Estado do repositório

Base M14 `ec33c9c`, branch `codex/milestone-15`; o checkpoint final é identificado
pela tag `blitzcs-m15-checkpoint`. O [relatório](docs/audio-milestone15.md) delimita
cobertura, evidências e reprodução. Não há um impasse estrutural que tenha exigido
modelo caro: implementação, referências e revisão usaram subagentes econômicos.

## Código funcional entregue

`Blitz3DCS.Audio` implementa as 10 funções FSOUND usadas no SCP, com 13 declarações
vinculadas, decoder WAV/Vorbis OGG libsndfile, mixer estéreo, sample/stream/canais,
volume/pan/frequência positiva, looping, pausa, interrupção e liberação. Os comandos
Blitz LoadSound/PlaySound/FreeSound e controles Channel*/Sound* usam o mesmo mixer.
Há arquivos reais, vozes simultâneas e fila SDL nativa; não são stubs.

O produtor usa buffer fixo/pinado e nenhuma função managed no callback nativo.
Tokens são Int32 positivos sem reutilização; slots internos podem ser reutilizados
sem reviver tokens antigos. Fechamento invalida recursos e aguarda a thread;
loads concorrentes são limitados à geração da sessão. Falhas do produtor impedem
novo Play bem-sucedido até Close/reinit. A libsndfile fecha o arquivo em finally.
BlitzCS e seus objetos/handles BASIC permanecem independentes de engine/áudio.

## Evidências

- `docs/evidence/m15-playback.json`: asset autêntico SCP, sample + stream
  simultâneos, dummy/default/disk; zero assets/canais depois de fechar.
- PCM pós-SDL disk: 48.128 frames F32LE estéreo 44,1 kHz, RMS ~0,0099758,
  pico ~0,0761947. Não é prova de som ouvido em hardware físico.
- `docs/evidence/m15-fmod/`: original FMOD 3.75 Win32 via Proton, loop depois
  de restaurar frequência, volume/pan/frequência, pause/stop/close, erros e reinit.
- `m15-fmod-comparison.json`: 42 grupos, 34 iguais e 8 diferenças explicitadas;
  os 86 oráculos de linguagem permanecem separados e reutilizados (85 iguais).
- A captura Pulse privada não demonstrou sinal: o stream foi roteado para outro
  sink pelo ambiente. Não se alterou EQ/default do usuário nem se gravou monitor
  global. Usou-se o driver disk SDL como prova objetiva; sinks temporários removidos.
- `m15-validation.json`: regressões, build, testes, hashes e limitações.

## Limites que impedem declarar FMOD completa / toda sessão SCP validada

1. Streams são decodificados inteiramente, com limite de 256 MiB por arquivo;
   não há decodificação incremental. Causa: o mixer atual consome DecodedAudio
   residente, e o decoder abre/lê/fecha no thread do chamador fora do gate.
   Alternativas: leitor libsndfile por worker com ring buffer limitado, ou uma
   fonte PCM incremental por canal com backpressure e política explícita de underrun.
2. Perfil Init=0 e mono/estéreo; não implementados frequência zero/reversa,
   índices físicos de canal e flags fora do perfil comprovado. O token gerenciado
   deliberadamente não imita endereços/identidades Win32. FMOD original aceita
   alguns casos adicionais, registrados como divergências, sem falsa equivalência.
3. O conjunto completo de assets não está instalado. Foi recuperado e decodificado
   DoorOpen1.ogg do commit oficial pinado; testes sintéticos cobrem WAV e OGG.
   Não há evidência sobre todos os arquivos, duração máxima, picos de memória ou
   caminhos dinâmicos numa sessão inteira. Obter distribuição legítima completa
   e rodar um inventário/decode em lote é o próximo teste para essa garantia.
4. Não há confirmação de audibilidade por um ouvinte. Default/dummy/disk são
   evidências distintas; a captura disk comprova PCM através da fila SDL.
5. Linux x64 foi executado. Empacotamento por RID e dependências nativas indiretas
   precisam ser completados para distribuição entre sistemas, respeitando as
   licenças. O teste atual publica SDL/libsndfile/Audio.dll em diretório isolado.

Esses limites não são causados por falta de renderer ou por um vínculo fictício.
Não certificar “Complete FMOD 3.75” com base no subconjunto funcional implementado.

## SCP e continuidade

Fontes originais sem alteração: Main +23 alvos Include passam sintaxe e a análise
integrada continua parando em `fullscreen_window_fix.bb:45:23`, `SystemProperty`.
São 3/30 arquivos isolados com assembly; não há assembly integrado/execução do jogo.
Não implementar SystemProperty ou outro subsistema dentro desta tarefa de áudio.

Reprodução geral: `PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash
scripts/verify.sh`, seguido de build/test Debug. O programa híbrido é
`samples/Audio/playback.bb`; o probe reproduzível é `tools/probe_audio_playback.py`.
A comparação usa `tools/compare_fmod_m15.py`; nova captura do original requer
`tools/probe_fmod_m15.py`, SDK/DLL/Proton e as variáveis registradas nos docs.
