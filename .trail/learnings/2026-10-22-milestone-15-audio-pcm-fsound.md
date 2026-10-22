# M15 — PCM real, FSOUND e limites do perfil SCP

Começar em docs/audio-milestone15.md, docs/evidence/m15-validation.json e handoff.md.
Blitz3DCS.Audio continua sem dependência do compilador. Nenhuma mudança de renderer,
física ou SystemProperty foi feita. O Main original ainda para em
fullscreen_window_fix.bb:45:23; 3/30 isolados emitem assembly, mesmos 30 hashes.

AudioDecoder usa libsndfile Linux x64 para WAV/OGG. Decode fora do gate evita IO
no render. Streams são pré-decodificados (256 MiB/arquivo), não incrementais.
AudioMixer.Render produz estéreo F32 sem lock/alocação. AudioRuntime.Playback
sincroniza comandos/render no produtor; SdlPlayback fixa buffer e submete fila
SDL3, sem callback managed no thread nativo. Close sinaliza, invalida/libera e
aguarda thread fora do gate. Geração de sessão impede publicar decode antigo
após Close+Init; falha do produtor impede falso sucesso em novos Plays.

Assets e canais têm tokens Int32 positivos/monotônicos, independentes de pointers
ou índices físicos. Reuso de slot não revive token. Exhaustion falha antes de
começar voz não rastreada; assets liberados e canais parados não retêm PCM.
InvalidDataException não deriva de IOException: capturá-la explicitamente no
Load, retornando 0/erro 10 para arquivo corrupto. Flags0 e mono/estéreo são o perfil.

Original FMOD 3.75 via Win32/Proton: GetError não apaga o erro ao ler. IsPlaying após
Stream_Close retorna 0/14; após Close do dispositivo retorna 0/2 e reinit funciona. Stream
Stop pode preservar erro anterior; Close limpa. Volume -1/256 foi observado como
sucesso com volume0. Canal pausado continua IsPlaying=1. Restaure frequência nativa
antes de comparar duração de loop: metade da frequência duplica duração.
Fonte original gxsound.cpp/gxaudio.cpp trunca float para int nos adapters Sound*;
não usar Math.Round como substituição presumida.

Stream_Open original tem 4 argumentos / @16; SCP declara 3. O binding gerenciado tem 4 com
length opcional0, aceitando a chamada original(file,mode,0). Não replicar pilha
Win32 incorreta nem carregar FMOD PE32 em .NETx64. as 13 declarações têm bindings;
10 símbolos têm chamadas ativas. Não há uso FMUSIC. AliasesSetVolumeAbsolute só
cobrem perfil sem master-volume independente; não prometer FMOD completa.

Evidências separadas: testes unitários PCM/fake sink; default e dummy reais;
SDL disk capturou PCM pós-fila nativa com DoorOpen1.ogg autêntico. Disk não prova
audibilidade física. Tentativa Pulse privada foi redirecionada a outro sink;
não gravar monitor global nem alterar EQ/default para mascarar falha de captura.
Reproduzir via tools/probe_audio_playback.py --capture-disk. Original via
probe_fmod_m15.py; comparação compare_fmod_m15.py normaliza somente identidades.
42 grupos: 34 iguais / 8 diferenças documentadas, não 42 fixtures independentes.
A linguagem preserva 85/86 oráculos reutilizados, separada desses grupos de áudio.
