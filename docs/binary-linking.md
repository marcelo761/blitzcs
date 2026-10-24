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

## Limitações

- Imports da DLL que nada resolve falham no carregamento; não há stubs e não há
  hook de catálogo para fornecê-los.
- Somente Linux x86-64; as DLLs têm base fixa e a execução é serializada por módulo.
- Retorno `String`, ponteiros de saída, structs e callbacks não são suportados pelo
  catálogo (a API gerenciada `GuestFunction.InvokeWithBuffers` e
  `CallStructViaHiddenPointer` cobre ponteiros de saída e structs; ver
  `docs/binary-abi-coverage.md`).
- Imports atrasados (delay-load, formato RVA) são resolvidos como imports comuns no
  carregamento; imports não resolvidos continuam falhando no carregamento.
- Exemplo: `samples/BinaryLinking` (`binlib.c` compilado por `tools/x86_oracle/build.sh`).
