# Blitz3D original: `.decls`, ABI nativa e FMOD (M13)

Esta nota registra o contrato observado no Blitz3D original e no `fmod.decls`
do snapshot do SCP:CB. Ela serve para decidir a fronteira de compatibilidade do
M13; não altera o runtime. A referência do jogo usada aqui é o commit
`edb8fe0840b78f14d1aef3a0bf6174630e7be296`, em que `fmod.dll` foi obtido de
`https://raw.githubusercontent.com/Regalis11/scpcb/edb8fe0840b78f14d1aef3a0bf6174630e7be296/fmod.dll`.
O arquivo local auditado e o DLL em `/tmp/blitz-research/package/deps/fmod/bin/fmod.dll`
têm o mesmo SHA-256: `1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22`.
Nenhum DLL foi adicionado ao repositório.

## O que o Blitz3D aceita em `.decls`

O compilador varre `%blitzpath%/userlibs/*.decls`. Cada arquivo declara uma DLL
com `.lib "nome.dll"`; as funções seguintes usam identificadores BASIC e um
alias opcional para o nome exportado. Os sufixos são `%` = inteiro de 32 bits,
`#` = `float` de 32 bits e `$` = string Blitz. Sem sufixo no retorno, o retorno
é `void`; sem sufixo em argumento, o argumento é inteiro. `*` só é reconhecido
como tipo de **argumento** e significa ponteiro opaco (`null_type`); não há
sintaxe de retorno ponteiro. Argumentos são posições escalares de 4 bytes,
sem `ref`/`out`, arrays ou convenção de ownership declarada no arquivo.

O parser aceita comentários com `;`, `.lib "..."`, nomes de função e aliases
entre aspas ou identificadores. As funções BASIC são registradas em minúsculas,
mas a string do alias é preservada para `GetProcAddress`. Símbolos duplicados
entre os `.decls` são erro de compilação. Diretiva desconhecida, função antes
de `.lib`, parênteses ausentes e alias em forma inválida também falham durante
o carregamento das declarações. Fontes: `/tmp/blitz-research/blitz3d/blitz/libs.cpp`
(`loadUserLib`, `linkUserLibs`, `linkLibs`) e
`/tmp/blitz-research/blitz3d/compiler/exprnode.cpp` (`ExprSeqNode`).

O runtime carrega a DLL pelo nome com `LoadLibrary`, com fallback para
`%blitzpath%/userlibs/`, resolve cada alias com `GetProcAddress` e mantém os
módulos carregados até `userlibs_destroy()` no encerramento. Se a DLL faltar,
os símbolos recebem um stub que gera `User lib not found` quando chamados; se
um export faltar, o stub gera `User lib function not found`. A falha fica
adiada até a chamada, não é transformada em retorno nulo. Fonte:
`/tmp/blitz-research/blitz3d/bbruntime/userlibs.cpp`.

Chamadas de userlib passam pela rota C do Blitz. Strings Blitz são convertidas
em `const char *` copiando os bytes e acrescentando NUL; a conversão consome o
objeto `BBStr` e guarda o buffer em um anel estático de 256 posições, válido
para uso transitório. Uma string C retornada é copiada para um novo `BBStr`.
Estruturas Blitz passadas à rota C viram ponteiros para seus dados; um argumento
`void` é desreferenciado a partir do endereço armazenado. Isso é um contrato
de ponteiro bruto e ciclo de vida manual, não uma cópia gerenciada do objeto.
Fontes: `userlibs.cpp` (`_bbStrToCStr`, `_bbCStrToStr`) e
`compiler/exprnode.cpp` (`ExprSeqNode::translate`).

O backend x86 chama o endereço carregado como função e não emite limpeza de
argumentos pelo caller; o comentário em `compiler/codegen_x86/tile.cpp` diz
que essa limpeza “Not needed for STDCALL”. Os aliases `_FSOUND_Init@12` e
`_FSOUND_Stream_Open@16` também são nomes decorados Win32 stdcall, com o número
de bytes de argumentos na pilha. O DLL auditado é PE32/i386. Portanto esse
contrato é de processo Windows x86: copiar apenas o nome do alias para um
processo .NET Linux x64 não torna o DLL carregável nem torna a pilha compatível.
O cabeçalho/export table e VERSIONINFO foram lidos estaticamente com `file` e
`objdump`; o inspetor REA recusou o layout PE com `invalid_format` (bytes de
seção sobrepõem os cabeçalhos). Essa recusa é uma limitação do inspetor, não
evidência de que o DLL seja inválido. A execução separada e controlada da ABI
original está registrada abaixo.

## FMOD do jogo e o significado dos inteiros

O `/tmp/scpcb-audit-source/fmod.decls` começa com `.lib "fmod.dll"` e declara
12 chamadas `FSOUND_*`. Os aliases usados pelo jogo, incluindo
`_FSOUND_Init@12`, `_FSOUND_Close@0`, `_FSOUND_Stream_Open@16` e
`_FSOUND_Stream_Play@8`, existem no export table do DLL pinado. A VERSIONINFO
do mesmo arquivo informa `FileVersion` e `ProductVersion` **3.75**; `file`
identifica PE32 para Intel i386. A data do cabeçalho PE é 28-12-2005. Isso
estabelece a versão deste binário específico, sem inferir a versão a partir do
nome do arquivo.

O `.decls` projeta todos os resultados usados como `%` (inteiro Blitz), mas o
header FMOD correspondente declara `FSOUND_Stream_Open` como retornando
`FSOUND_STREAM *` e recebendo `FSOUND_STREAM *` em funções como
`FSOUND_Stream_Play` (`/tmp/blitz-research/package/deps/fmod/include/fmod.h`,
linhas 1003–1008). No Blitz3D x86, ponteiro e inteiro têm ambos 32 bits, então
o ponteiro opaco cabe no campo inteiro legado; os tipos `%` não tornam esse
valor um inteiro semântico. `StrictLoads.bb` guarda e reusa esse valor em
`st\sfx`, chama `FSOUND_Stream_Close` durante a liberação, e mantém canais e
streams em campos inteiros. O código depende do pareamento correto entre abrir,
usar e fechar; o `.decls` não codifica ownership nem previne uso após close.

Esse atalho não é válido em .NET x64: converter `nint`/`IntPtr` para `int`
trunca o endereço. Para manter a linguagem BASIC atual, uma API gerenciada deve
expor handles inteiros opacos e guardar `nint`/objetos nativos em tabela privada,
validando tipo, slot, geração e ciclo de vida. Para uma API de interop que
exponha ponteiros diretamente, o contrato precisa usar `nint`/`IntPtr` e
documentar ownership; isso exige mudanças explícitas no limite externo, não
casting de `int`.

## Evidência de execução no runtime original

`tools/probe_fmod_original.py` compila e executa um programa pequeno com o
compilador e runtime Blitz3D Win32 originais via Proton/Wine, usando uma cópia
temporária de `bin` e um `.decls` de teste no diretório shadow. O probe usa o
DLL pinado pelo hash acima; não muda os arquivos do jogo ou a instalação original.
Ambas as execuções completaram com código zero, captura de saída e marcador de
conclusão normal (`phase: run`, `exit_code: 0`).

| Perfil | Saída observada | Leitura suportada |
| --- | --- | --- |
| `verified-probe.json`, `FSOUND_OUTPUT_NOSOUND = 0` explícito | `3.75`, `31160833`, `1`, `31138561`, `1`, `0`, `closed` | Versão 3.75; `FSOUND_SetOutput(0)` e `FSOUND_Init(44100, 64, 0)` executaram; os bytes baixos observados foram `1`; `FSOUND_GetError()` foi 0; `FSOUND_Close()` completou. Sem reprodução solicitada ou alegação de áudio audível. |
| `default-probe.json`, saída padrão do SCP | `3.75`, `1`, `1`, `0`, `closed` | Inicialização no output padrão completou; retorno e máscara baixa foram 1; erro foi 0; close completou. Sem reprodução solicitada nem validação de áudio audível. |

Evidências: [`artifacts/m13-fmod/verified-probe.json`](../artifacts/m13-fmod/verified-probe.json),
[`artifacts/m13-fmod/default-probe.json`](../artifacts/m13-fmod/default-probe.json),
[`artifacts/m13-fmod/probe.bb`](../artifacts/m13-fmod/probe.bb) e
[`tools/probe_fmod_original.py`](../tools/probe_fmod_original.py). O probe não
testa carregamento FMOD pelo .NET, reprodução, decodificação de arquivos ou
equivalência auditiva.

O resultado também mostra por que `.decls` não é descrição ABI suficiente: o
header declara `FSOUND_SetOutput` e `FSOUND_Init` como `signed char`, enquanto
o probe os declarou `%` (inteiro Blitz de 32 bits). O retorno bruto impresso
foi `31160833` e `31138561`, embora o byte baixo em cada amostra fosse `1`;
as partes altas são resíduo do registrador de retorno e não devem ser tratadas
como valor estável nem normalizadas automaticamente. Esses números só descrevem
as capturas deste host. O header declara `FSOUND_GetError` como `int` e o
resultado observado foi `0`; `FSOUND_GetVersion` é `float` e o probe observou
`3.75`. `fmod.h` define `F_API` como `__stdcall` em Win32, corroborando a
convenção de chamada; os nomes exportados decorados mostram resolução de
símbolo/bytes de argumentos, mas não estabelecem o tipo ou largura do retorno.

Essa discrepância torna obrigatória uma descrição de assinatura com base no
header da versão efetivamente pinada (ou uma shim tipada), e não apenas a
projeção `%/#/$` do `.decls`. A evidência original não autoriza criar bindings
FMOD no runtime .NET atual nem registrar falsos métodos que façam o SCP avançar.

A auditoria de usos e as novas capturas M14 estão em [`fmod-audit-m14.md`](fmod-audit-m14.md).

`CallDLL` é um caminho diferente: recebe nomes de DLL/função e bancos de bytes
de entrada/saída, passando ponteiros e tamanhos dos bancos ao runtime gráfico.
Não é substituto automático para a semântica `FSOUND_*` nem para handles de
stream. Fonte: `/tmp/blitz-research/blitz3d/bbruntime/bbbank.cpp`.

## Consequências para o runtime atual

O registry externo atual do BlitzCS admite apenas `int`, `float` e `string` em
métodos `public static` de assemblies gerenciados
([`ExternalFunction`](../src/BlitzCS.Compiler/ExternalFunctions.cs)). Isso é
uma fronteira gerenciada útil, mas não é carregador de `.decls`, não resolve
export nativo e não pode representar o ponteiro de stream como `int` com
segurança em x64. Os arquivos de declaração do SCP devem continuar sendo uma
fonte de inventário da superfície usada; não são, por si só, uma ABI executável
no runtime .NET/Linux.

## Alternativas e licenças

| Caminho | O que preserva | Custo e limite principal |
| --- | --- | --- |
| Reusar FMOD 3.75 num worker Windows x86/Wine com bridge IPC | DLL e comportamento legado, incluindo formatos e exports | Mantém processo/ambiente Win32 e ABI antiga; IPC e implantação ficam mais complexas; uma shim dentro do processo Linux x64, sozinha, não supera a incompatibilidade PE32/PE64. |
| Shim com handles estáveis sobre um backend compatível | Nomes BASIC e contratos inteiros legados; traduz IDs para ponteiros/objetos nativos | Implementa e mantém conversão de strings, ciclo de vida, erros e semântica dos comandos; uma shim sozinha não corrige a bitness nem cria um backend de áudio. |
| Backend moderno nativo/gerenciado | Integração direta com Linux x64 e evolução do runtime | Requer reimplementar a superfície necessária (streams, canais, formatos e erros); igualdade de áudio/comportamento precisa ser avaliada separadamente. |

O repositório original do Blitz3D declara zlib/libpng, mas seu `LICENSE.TXT`
também instrui instalar FMOD API 3.75 separadamente. Isso não licencia o DLL
FMOD por extensão. A página jurídica oficial atual da FMOD diz que FMOD 3/Ex
não é coberto para uso comercial pela EULA padrão e requer licença comercial
customizada; a permissão aplicável ao binário histórico distribuído com o jogo
depende dos termos e da proveniência desse binário. Não concluo aqui se a
distribuição específica do SCP:CB está autorizada: isso exige revisar os termos
que acompanharam aquele artefato e a licença do jogo.

Como candidato de backend, o projeto oficial `miniaudio` descreve API de alto
nível para playback/mixing, carregamento de áudio e plataformas desktop e
móveis; seu LICENSE oferece escolha entre domínio público e MIT-0. Isso reduz
dependência de uma API FMOD antiga e encaixa melhor num shim C#, com o custo de
reimplementar a superfície `FSOUND_*` necessária ao jogo. A licença deve ser
reavaliada na versão exata incorporada e junto às dependências/formatos usados.

Referências oficiais consultadas em 2026-10-22: [FMOD Legal Information](https://fmod.com/legal),
[FMOD Licensing](https://www.fmod.com/licensing),
[miniaudio LICENSE](https://github.com/mackron/miniaudio/blob/master/LICENSE) e
[miniaudio README](https://github.com/mackron/miniaudio).
