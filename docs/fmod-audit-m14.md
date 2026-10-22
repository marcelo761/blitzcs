# Auditoria M14: FMOD do SCP:CB

Esta auditoria registra a superfície `FSOUND_*` usada pelo snapshot original do
SCP:CB (`/tmp/scpcb-audit-source`), compara o `.decls` com o header FMOD 3.75 e
adiciona observações de execução no Blitz3D Win32 original. O contexto sobre o
parser `.decls`, bitness e limites de ABI está em
[`native-original-m13.md`](native-original-m13.md).

## Inventário

O inventário legível por máquina está em
[`evidence/m14-fmod/usage-audit.json`](evidence/m14-fmod/usage-audit.json).
Ele cobre as 13 entradas declaradas em `fmod.decls`, com retorno/argumentos do
header pinado, projeção BASIC e ocorrências ativas no código. Há 10 nomes com
uso ativo, 2 entradas declaradas não usadas (`FSOUND_SetVolumeAbsolute` e
`FSOUND_GetVolume`) e `FSOUND_Close` aparece somente numa linha comentada.
Nenhuma chamada `FMUSIC_*` foi encontrada nos arquivos `.bb` ou `.decls` do
snapshot.

O descompasso que merece investigação separada é `FSOUND_Stream_Open`: o
header declara quatro argumentos Win32 e o export decorado é
`_FSOUND_Stream_Open@16`, enquanto o `.decls` e a chamada em `StrictLoads.bb`
mostram três. `FSOUND_Close` também é declarado `%` (retorno Integer) no
`.decls`, embora o header declare `void`; como não há chamada ativa, isso não
foi exercitado pelo jogo.

## Captura de Init/Close

O probe usou a DLL original pinada por SHA-256
`1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22` com
`FSOUND_OUTPUT_NOSOUND`; não pediu playback. O relatório, a captura completa do
adapter e o `.bb` exato estão versionados em:

- [`init-semantics.json`](evidence/m14-fmod/init-semantics.json)
- [`probe.json`](evidence/m14-fmod/probe.json)
- [`probe.bb`](evidence/m14-fmod/probe.bb)

Naquela execução, `Init(44100,64,0)` retornou byte baixo 1 e erro 0. Repetir
`Init` sem `Close` também retornou 1/erro 0. `Close` seguido por uma nova
inicialização com flag documentada `0x80` (`FSOUND_INIT_DONTLATENCYADJUST`)
retornou 1/erro 0. Taxa zero e canais negativos falharam com erro 14
(`FMOD_ERR_INVALID_PARAM`); zero canais foi aceito. O bit desconhecido
`0x80000000` foi aceito nesse perfil. Esses achados valem para este DLL e o
output NOSOUND observado; não se extrapolam para o output de áudio padrão.

`FSOUND_Init` retorna `signed char`, mas o probe precisa declará-lo `%` para
passar pela interface histórica do Blitz. Por isso somente o byte baixo foi
interpretado; os bits altos do EAX são resíduo e não um resultado estável.
`FSOUND_Close` foi declarado sem sufixo no probe, conforme o header.
