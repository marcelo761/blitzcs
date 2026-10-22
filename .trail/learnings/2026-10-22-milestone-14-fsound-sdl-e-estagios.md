# M14 — FSOUND, SDL3 e estágios externos

Consultar docs/scp-milestone14.md, docs/audio-architecture-m14.md e
docs/external-signatures-m14.md antes de ampliar a integração.

ExternalFunctionSignature/ExternalValueType separam tipos de MethodInfo.
BasicCompiler.CheckSemantics devolve UnboundExternalCall com fonte/posição;
Compile recusa chamadas usadas sem binding antes do emitter. Declarações
não usadas não impedem emissão. Só Integer/Float/String; Void e defaults
em declarações sem método não suportados. Métodos existentes mantêm defaults.
Teste CLI garante que inspeção e build não executam inicializador estático.

Blitz3DCS.Audio não referencia Compiler/CLI. FSOUND.Init/Close usam SDL3 real,
Cdecl, bool I1 e SDL_AudioSpec de 12 bytes. Perfil Linux x64, libSDL3.so.0.
Dummy é teste explícito headless; default também abriu dispositivo; sem playback.
Não confundir abrir dispositivo com implementar mixer, canais ou streams.

FMOD 3.75 Win32/Proton continua como referência isolada. Init original signed char
é normalizado pelo byte baixo, nunca EAX inteiro. Close é void embora .decls use %.
Init repetido idêntico e canais zero foram aceitos. Flags 0x80/0x80000000 aceitas
no NOSOUND original são recusadas no SDL inicial: divergência explícita.
Mudança de configuração exige Close até validação adicional. A tabela interna
nint → tokens Int32 não reutiliza identidade e libera explicitamente recursos;
ainda não é consumida por streams.

SCP sem edição: FMod.bb agora compila; 3/30 isolados. Main para em
fullscreen_window_fix.bb:45:23, SystemProperty. Sintaxe Main +23 Includes válida;
resolução completa, vínculo e execução não comprovados. Stream_Open tem três
argumentos no jogo/.decls e quatro no header/export: não presumir quarto
argumento nem truncar ponteiro.
