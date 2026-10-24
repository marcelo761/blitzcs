# Ligação transparente de bibliotecas binárias

Um `.bb` pode chamar exports de uma DLL PE32 x86 sem alterar o `.bb` nem a DLL e
sem escrever uma implementação gerenciada da API. O catálogo de externos descreve
a assinatura BASIC e o export; a CLI gera o binding.

## Catálogo

```json
{ "name": "AddInts", "parameters": ["Integer", "Integer"], "returnType": "Integer",
  "defaults": [10],
  "binary": { "library": "binlib.dll", "export": "add32", "convention": "cdecl" } }
```

- `binary.library`: caminho relativo ao catálogo. A biblioteca é traduzida e copiada
  para a saída automaticamente (equivale a listá-la em `binaryLibraries`).
- `binary.export`: nome exato no export table. Se não existir, a CLI aceita uma
  única variante decorada (`_nome`, `_nome@N`, `nome@N`); várias candidatas é erro
  de ambiguidade, assim como export duplicado ou forwarder.
- `binary.convention`: `cdecl`, `stdcall`, `thiscall` ou `fastcall`. Opcional somente
  quando a decoração do export a revela (`_f`, `_f@N`, `@f@N`); caso contrário o build
  falha. Se o catálogo contradiz a decoração, ou um stdcall/fastcall decorado não
  declara `4 * parâmetros` bytes, o build falha. Em `binary.export` basta o nome sem
  decoração (`f` encontra `_f@8` ou `@f@8` se for único).
  - `thiscall`: o primeiro parâmetro declarado deve ser `Integer` e vai em ECX (`this`);
    os demais ficam na pilha, em ordem, e o callee desempilha. Não há decoração para
    inferir: o catálogo precisa dizer `thiscall`.
  - `fastcall` (regra MSVC): os dois primeiros parâmetros que não são `Float`
    (`Integer`, `String`), em ordem, vão em ECX e EDX; `Float` e o restante ficam na
    pilha. O callee desempilha.
- `defaults`, `parameters` e `returnType` seguem as regras normais de externos.
  Não se combina `binary` com `assembly`/`method`.

Tipos: `Integer` vira palavra de 32 bits; `Float` vira IEEE single de 32 bits na
pilha; `String` é copiada, terminada em NUL e em Windows-1252 (caracteres não
mapeáveis viram `?`), para um buffer verificado da memória do guest, liberado após
a chamada; o ponteiro é o argumento. Retorno: `Integer` (EAX), `Float` ou `Void`.

`Float` como retorno lê ST0 (onde float/double/long double voltam em cdecl, stdcall,
thiscall e fastcall, no MSVC e no GCC). O registrador de 80 bits é reduzido a
precisão simples uma única vez, direto do valor estendido, com arredondamento ao mais
próximo (empate para par); não passa por double, que arredondaria duas vezes. O
runtime desempilha o ST0. Pilha x87 vazia (o callee na verdade devolve inteiro ou
void) ou com sobra (o callee devolve float mas o catálogo diz `Integer`/`Void`) é
erro em tempo de execução (`BinaryLibraryException`), nunca um valor zero. `String`
como retorno é erro de build (a posse do ponteiro é desconhecida). Retorno de 64 bits
não existe nos tipos BASIC e não é oferecido.

## Como funciona

1. A CLI valida cada `binary` contra o PE (existe, único, não é forwarder).
2. Compila `BlitzCS.BinaryBindings.dll` (Roslyn), uma classe por biblioteca com um
   método estático por função, e a registra pelo mecanismo normal de externos
   (diagnósticos de assinatura sem implementação, defaults e tipos preservados).
3. Traduz as DLLs (cache de conteúdo) e valida convenção/tamanho dos argumentos
   contra o artefato antes de gravar qualquer saída.
4. Copia para a saída o binding, `BlitzCS.BinaryCompiler.Runtime.dll`,
   `BlitzCS.BinaryCompiler.dll`, `Iced.dll`, a DLL original (bytes intactos) e
   `<dll>.bcx`, com as proteções de sobrescrita existentes.

Em tempo de execução `BinaryLibrary` abre o módulo de forma preguiçosa e segura
entre threads, um por arquivo por processo, e o descarta ao sair. Falhas do guest,
artefato desatualizado (`.bcx` de outra DLL) e imports não resolvidos viram
`BinaryLibraryException` com biblioteca, export e a mensagem original (por exemplo
`missing.dll!NeverProvided`).

## Ambiente Win32, bibliotecas reais e conjuntos de módulos

Uma DLL de terceiros (HeapAlloc, seções críticas, CRT, outras DLLs) roda a partir de um `.bb` sem
código de ligação escrito à mão. `BinaryLibrary` (Runtime, sem dependência do Win32) obtém os imports de
um `IBinaryHostEnvironment` (`CreateResolver(moduleName)`, um resolvedor por módulo; `CompositeBinaryHostEnvironment`
compõe ambientes). O binding gerado referencia `BlitzCS.BinaryCompiler.Win32` e registra `Win32ImportResolver`
como ambiente; o assembly Win32 e suas dependências são copiados para a saída com as proteções de sobrescrita.

`binaryLibraries` aceita, além de strings, objetos (chaves desconhecidas são rejeitadas; a mesma DLL listada duas
vezes com opções é erro de ambiguidade; dois arquivos com o mesmo nome também):

| chave | padrão | efeito |
|---|---|---|
| `path` | obrigatório | caminho relativo ao catálogo |
| `win32` | ausente = automático | `true` sempre cria o ambiente Win32; `false` nunca; automático só se a DLL importa de uma DLL que não é irmã do catálogo |
| `runEntryPoint` | `true` | executa DllMain(PROCESS_ATTACH) (inicialização da CRT); sem efeito se o PE não tem entry point. Falha vira `BinaryLibraryException` ("Binary library 'x.dll' failed to initialise ...") |
| `unboundImports` | `"fault"` | `"fault"`: import sem provedor = aviso no build (stderr, lista `dll!nome`) e falha na chamada nomeando `dll!nome`; `"error"`: erro de build |
| `defaultConvention` | nenhum | só `"cdecl"`: exports sem decoração e sem entrada própria |
| `exportConventions` | vazio | `{"export": "cdecl" \| "stdcall:N", "#ordinal": ...}`; contradizer `binary.convention` ou a contagem de parâmetros é erro |
| `unpack` | `false` | ver `docs/binary-unpack.md` |
| `fileRoot` | `"none"` | `"app"`: as APIs de arquivo da biblioteca veem o diretório da aplicação (onde ficam a DLL, o `.bcx` e os `--assets`) como `C:\` e diretório de trabalho; `..` não sai dele. `"none"`: todo acesso a arquivos falha com ACCESS_DENIED |
| `audio` | ausente | backend waveOut do ambiente Win32: `"host"` (winmm do sistema, só Windows), `"virtual"` (sumidouro em processo, tempo real), `"none"` (sem dispositivos); ausente = padrão do componente (host no Windows, nenhum nos demais); `BLITZCS_AUDIO` sobrepõe em tempo de execução. Ver `docs/binary-audio.md` |
| `audioRecord` | ausente | nome relativo (sem unidade nem `..`) sob o diretório da aplicação que recebe um WAV com tudo que a biblioteca tocou |

`fileRoot`, `audio` e `audioRecord` configuram o ambiente Win32; combiná-los com `"win32": false` é erro de build. Exemplo completo com a FMOD 3.75 original: `samples/FmodAudio`.

A política de imports ausentes: se um import ausente é alcançável só se sabe em tempo de execução, então o build
avisa (com `Win32ImportResolver.Analyze` e a checagem dos irmãos) e a chamada falha nomeando `dll!nome`.

**Vários módulos.** Com mais de uma biblioteca, o build aloca bases com `ModuleBaseAllocator` (uma única biblioteca
mantém sua base preferida), traduz cada uma para a sua base (chave de cache por base; a base fica no `.bcx`) e
valida os imports entre irmãos (export existe, é código, convenção cdecl/stdcall declarada ou decorada;
nunca presumida). Em tempo de execução todas as bibliotecas do mesmo binding formam um `GuestModuleSet` por
processo: carregado de forma preguiçosa (a biblioteca chamada mais os irmãos que ela importa), seguro entre threads,
com um estado Win32 por módulo, descartado ao sair.

**Memória do guest a partir do BASIC.** Funções do catálogo com `"binary": {"library": "x.dll", "intrinsic": "alloc"}`
(sem `export`): `alloc(Integer)->Integer` (bloco zerado), `free(Integer)`, `peek8/peek32(Integer)->Integer`
(lê qualquer memória do guest mapeada, verificada), `poke8/poke32(Integer, Integer)` (só dentro de blocos de `alloc`
vivos). Ponteiros e structs são `Integer`; não há tipo `Bank` nem cópia de entrada/saída. `samples/BinaryLinking/Real/`
usa isso com o libogg real (`ogg.bb`/`ogg.json`; a DLL fica fora do repositório).

## Limitações

- Imports que nem o ambiente Win32 nem um irmão fornecem viram stub que falha na chamada nomeando `dll!nome` (`unboundImports`).
- Somente Linux x86-64; as DLLs têm base fixa e a execução é serializada por módulo.
- Retorno `String`, ponteiros de saída, structs e callbacks não são suportados pelo
  catálogo (a API gerenciada `GuestFunction.InvokeWithBuffers` e
  `CallStructViaHiddenPointer` cobre ponteiros de saída e structs; ver
  `docs/binary-abi-coverage.md`).
- Imports atrasados (delay-load, formato RVA) são resolvidos como imports comuns no
  carregamento; imports não resolvidos continuam falhando no carregamento.
- Exemplo: `samples/BinaryLinking` (`binlib.c` compilado por `tools/x86_oracle/build.sh`).
