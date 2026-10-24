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
- `binary.convention`: `cdecl` ou `stdcall`. Opcional somente quando a decoração do
  export a revela; caso contrário o build falha. Se o catálogo contradiz a
  decoração, ou um stdcall não desempilha `4 * parâmetros` bytes, o build falha.
  `thiscall`/`fastcall` não são suportadas.
- `defaults`, `parameters` e `returnType` seguem as regras normais de externos.
  Não se combina `binary` com `assembly`/`method`.

Tipos: `Integer` vira palavra de 32 bits; `Float` vira IEEE single de 32 bits na
pilha; `String` é copiada, terminada em NUL e em Windows-1252 (caracteres não
mapeáveis viram `?`), para um buffer verificado da memória do guest, liberado após
a chamada; o ponteiro é o argumento. Retorno: `Integer` (EAX) ou `Void`.
`Float` e `String` como retorno são erros de build (st0 não é recuperável por
EnterGuest; a posse do ponteiro de string é desconhecida).

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
- Retorno `Float`/`String`, ponteiros de saída, structs e callbacks não são suportados.
- Exemplo: `samples/BinaryLinking` (`binlib.c` compilado por `tools/x86_oracle/build.sh`).
