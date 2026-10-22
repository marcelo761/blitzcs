# Milestone 9: observações do Blitz3D original

As cinco fixtures em `tests/Compatibility/milestone9.json` foram executadas no
Blitz3D V1.118 oficial, usando `tools/original_blitz3d_adapter.py`, Proton 11.0
e o prefixo separado descrito em [original-adapter.md](original-adapter.md).
As capturas foram feitas em série no display Xvfb dedicado. Não derivei o stdout
do original dos resultados do BlitzCS. `BLITZ3D_EVIDENCE_DIR=/tmp/m9-evidence`
conservou o comando, stdout/stderr nativos, código de saída e SHA-256 do fonte
para cada execução desta pesquisa.

| Fixture | Captura nativa | Semântica observada |
| --- | --- | --- |
| `functions.bb` | `35`, `75`, `79` | Defaults constantes preenchem somente parâmetros finais omitidos: zero, um ou nenhum argumento explícito. |
| `conversions.bb` | `4`, `4.0`, `5tail`, `1`, `8`, `9.0`, `hello`, `12`, `2.5`, `42`, `1` | Chamadas em expressão e command-style aceitam os mesmos casts dos argumentos para o tipo formal (`%`, `#`, `$`). Float para inteiro arredonda: 3.6→4 e 7.8→8. String numérica para `%`/`#` aceita o prefixo numérico (`12tail`→12, `2.5tail`→2.5); inteiro para `$` vira texto. |
| `fields.bb` | `0`, `[]`, `1` | `Field` aceita inicializadores literais e `New` compila, mas os valores permanecem zero, string vazia e referência `Null`. |
| `hex32.bb` | `2147483647`, `-2147483648`, `-1`, `0`, `0` | Literais hexadecimais e operações inteiras mantêm os 32 bits baixos, interpretados com sinal; `$FFFFFFFF+1` volta a zero. |
| `omitted_middle.bb` | `compile_error`, código 255 | `Sum(,9)` não representa um argumento omitido; o compilador original rejeita o slot vazio. |

## Base no código-fonte legado consultado

O repositório legado consultado está em `/tmp/blitz-research/blitz3d`, commit
`6beb288cb5962393684a59a4a44ac11524894939`. Ele ajuda a explicar as capturas,
mas não é apresentado como o código exato dos binários V1.118.

- `compiler/parser.cpp:452-474` lê os parâmetros e `compiler/declnode.cpp:52-79`
  exige que defaults de parâmetros sejam constantes e guarda seu valor tipado.
- `compiler/parser.cpp:523-532` permite expressão opcional só no início da lista;
  depois de vírgula, a próxima expressão é obrigatória. `compiler/exprnode.cpp:112-139`
  completa apenas argumentos que faltam no fim e falha se o parâmetro não tem default.
- `compiler/parser.cpp:121-151` classifica statement iniciado por identificador
  como chamada quando não é atribuição ou acesso indexado/campo; reconhece
  parâmetros com e sem parênteses. `parser.cpp:725-748` reconhece chamada em
  expressão como `Ident(...)`. `exprnode.cpp:147-156` busca a função no escopo e
  converte os argumentos para os tipos formais. `environ.cpp:37-40` procura
  funções do escopo atual e dos escopos globais.
- `compiler/parser.cpp:699-712` acumula cada dígito hexadecimal em `int`, logo o
  valor observável guarda 32 bits. O resultado do compilador oficial confirma
  o comportamento incluindo overflow na soma.

A captura de `Field` é a evidência decisiva para inicializadores: no binário
V1.118 eles não inicializam novas instâncias. O comportamento do fonte legado
não foi usado para substituir essa observação empírica.

## Consolidação final

A sexta fixture `command-grouping.bb` foi executada realmente no original e
imprimiu `3`, `3`, `3`, `9`, `9`, `9`. Ela valida agrupamento no primeiro
argumento e chamadas sem argumentos separadas por colon e em If inline.
A comparação final dos sete manifestos está em
`artifacts/m9-final-differential.json`: 65 locais passaram, 64 capturas originais
atuais coincidiram, e `upper-bound-error` ficou como adapter_error após dois
timeouts. As seis fixtures M9 coincidiram e as 58 coincidências antigas foram
preservadas. A divergência histórica de status de saída desse caso está separada
em historical_comparison; não substitui a captura atual indisponível.
Procedência, hashes e snapshots por fixture estão em
`artifacts/m9-original-capture-cache/`. Ver [validation.md](validation.md).
