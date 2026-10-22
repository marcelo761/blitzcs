# Vetores fixos BlitzBasic: pesquisa e evidência

Este relatório registra a declaração `name[tag][upperBound]` que bloqueava
`Global SCP1025state#[6]` em `Main.bb:291` e os vetores usados em campos do SCP.
O objetivo é guiar a compatibilidade do compilador; não substitui o `Dim` de
arrays BlitzBasic, cujas dimensões são definidas em execução.

## Fontes consultadas

O código-fonte Blitz3D foi consultado em
`/tmp/blitz-research/blitz3d`, commit
`6beb288cb5962393684a59a4a44ac11524894939`. A implementação de declarações
está em `compiler/parser.cpp` (`parseVarDecl` e `parseStructDecl`), a criação
do tipo e a alocação de vetores em `compiler/declnode.cpp`, e o acesso e os
limites em `compiler/varnode.cpp`. O runtime aloca e zera vetores em
`bbruntime/basic.cpp` (`_bbVecAlloc`, `_bbVecFree`, `_bbObjNew` e
`_bbObjDelete`).

O executável diferencial é a distribuição Blitz3D V1.118 da Blitz Research,
que reporta `11.18:SoLoud`; sua captura via Proton está documentada em
[`original-adapter.md`](original-adapter.md). O executável oficial e o código
consultado são referências distintas. Nenhum resultado abaixo é extrapolado
para todos os binários Blitz3D.

## Semântica observada no código original

- `Global`, `Local`, parâmetros e `Field` aceitam um vetor fixo unidimensional
  com um limite constante. A gramática lê uma única expressão entre colchetes;
  isso não é uma forma abreviada de array multidimensional `Dim`.
- O valor declarado é o **limite superior inclusivo**. `[N]` reserva `N+1`
  posições, numeradas de `0` a `N`. O protótipo exige expressão constante;
  dimensões negativas após a conversão inteira são rejeitadas.
- O tipo de elemento vem do sufixo `%`, `#`, `$` ou do nome de `Type`, como em
  `Field items.Item[2]`. Os tipos de vetor só são compatíveis quando tipo de
  elemento e todos os limites coincidem (`VectorType::canCastTo`).
- Vetores globais são alocados durante a inicialização global. Vetores locais
  de uma função são alocados na entrada de cada chamada e liberados no retorno.
  Seus elementos começam zerados: inteiros `0`, floats `0.0`, strings vazias e
  referências a objetos nulas.
- `New` aloca cada vetor declarado como campo e o zera. `Delete` libera os
  elementos do vetor; strings e referências de objetos são liberadas conforme
  seus tipos. A exclusão do objeto não equivale a excluir recursivamente os
  objetos referenciados: cada objeto mantém sua própria identidade/lifetime na
  lista do tipo até ser excluído. Referências e aliases observáveis precisam
  continuar válidos segundo essas regras.
- Um índice constante maior que o limite declarado é erro de compilação. No
  código-fonte consultado, o diagnóstico ocorre para `index >= upperBound+1`.
  O acesso não constante tem checagem gerada em modo de depuração. O adaptador
  atual retornou timeout/erro de infraestrutura para um teste deliberadamente
  inválido de índice dinâmico; portanto não usamos esse teste para afirmar a
  mensagem, saída ou código de término do runtime original.
- Conversão do tamanho é uma particularidade histórica: o compilador faz
  `ConstNode::intValue()`. Para literal Float, o código C++ usa instrução
  x87 `fistp` após selecionar arredondamento ao inteiro mais próximo. No
  executável testado, `[2.5]` resulta em limite `2` (empate para par) e
  `[2.8]` resulta em limite `3`. Isso não deve ser substituído por conversão
  de ponto flutuante padrão sem teste.
- A sintaxe original de iteração é `For item.Item=Each Item ... Next`, não
  `For Each Item`.

## Uso encontrado no SCP auditado

O bloqueio inicial é `Global SCP1025state#[6]` em `Main.bb:291`, com acessos
até índices calculados e índices explícitos. `Main.bb:350` declara também
`MTFrooms.Rooms[10]` e `MTFroomState%[10]`. Vetores em campos aparecem, por
exemplo, em `Main.bb` (`buttons%[2]`, `Sound[1]`), `Items.bb`
(`SecondInv.Items[20]`), `MapSystem.bb` (campos de objetos, números e strings)
e `NPCs.bb` (`Path.WayPoints[20]`, `PathStatus` e campos relacionados).
Logo, vetores em campos não são um caso periférico; vetores de referências a
objetos também são necessários para os módulos do jogo.

`Const` é outro bloqueio frequente no SCP. O parser original permite `Const`
somente no programa principal, aceita declarações escalares separadas por
vírgulas e exige inicializador. O valor precisa resultar em expressão
constante, depois de conversão ao tipo `%`, `#` ou `$` declarado. Arrays não
podem ser constantes. Exemplos reais estão em `Achievements.bb`, `Converter.bb`
e `Difficulty.bb`; `Main.bb` também usa limites derivados de constantes em
declarações de vetores. A pesquisa do fonte confirma essa regra em
`parser.cpp` e `declnode.cpp`; a fixture `const-baseline.bb` cobre uma execução
real de constantes escalares tipadas e separadas por vírgulas. `ProgNode::semant`
processa as declarações na ordem do fonte antes dos tipos, funções e corpo
principal. A distribuição testada rejeita referência a uma constante declarada
posteriormente e rejeita atribuição a uma constante. Esses dois casos também
foram executados no original e terminaram em erro de compilação.

Mensagens observadas para os casos negativos: atribuição, `Constants can not
be assigned to`; referência futura, `Expecting identifier`; `Const` dentro de
Function ou If, `'Const' can only appear in main program`; vetor constante,
`Blitz arrays may not be constant`; símbolo duplicado, `Duplicate variable
name`; sufixo incorreto no uso de uma constante, `Variable type mismatch`.
Um inicializador por chamada a função também falha, com `Function 'compute' not
found`: o original processa protótipos de constantes antes dos protótipos das
funções, então o nome da função ainda não está disponível nesse ponto.

Foram acrescentadas fixtures de `Const` em `tests/Compatibility/constants.json`.
Cinco programas executaram no original: expressões `%/#/$` e concatenação de
strings; referência a constantes anteriores; constante usada como limite em
Global e Field; leitura por função; e parâmetro local que sombreia uma
constante global. O original também rejeitou por erro de compilação atribuição,
referência futura, declaração dentro de Function e If, inicializador não
constante por chamada de função, nome duplicado, sufixo incompatível e vetor
declarado como constante. As saídas de sucesso no manifesto foram capturadas
diretamente do original.

## Comparações reais via Proton

As fontes de `tests/Compatibility/fixtures/vectors/` foram compiladas e
executadas sem edição pelo Blitz3D V1.118. O adaptador instrumenta apenas
`Print` para escrever a expressão calculada num arquivo; compilação, regras de
tipo, expressões, acesso a índices e runtime continuam sendo os originais.
Os resultados esperados estão em `tests/Compatibility/vectors.json`.

| Caso | Resultado original observado |
| --- | --- |
| Globais Float `[6]` | zeros em `[0]` e `[6]`; escrita/leitura `1.25` |
| Globais Integer `[2]` e String `[1]` | zeros/vazio; últimos índices `[2]` e `[1]` aceitos |
| Local Integer `[2]` em função | retorno `49`; Local `[0]` é novo em cada chamada (`1`, `1`) |
| Campos Float `[2]` e String `[1]` | valores inicializados e últimos índices aceitos |
| Campo Integer `[1]` em duas instâncias | valores independentes (`0`, `7`, `0`) |
| Campo `Item[2]` | posições `[0]`/`[2]` inicialmente nulas; escrita de objeto e alias imprimem `42` |
| Exclusão explícita com referência em campo | depois de `Delete child` e `Delete owner`, a iteração não encontra o filho |
| Índice por função com efeito colateral | função chamada uma vez; escreve `42` no índice calculado |
| Acesso constante acima do limite | erro de compilação |
| Tamanho Float `[2.5]`, `[2.8]` | últimos índices `2`, `3` funcionam; `[3]` em `[2.5]` é erro de compilação |
| `Const` `%`, `#`, `$` em uma declaração | saída `6`, `3.5`, `SCP` |
| Expressões, concatenação e constantes anteriores | saídas `6`, `3.5`, `SCP!`; `6`, `1.5` |
| Constante usada em limites Global e Field | saída `42`, `0` |
| Constante lida em função; parâmetro de mesmo nome | saídas `42`; `11`, `4` |
| Oito erros negativos de `Const` | fase `compile_error` em todos |

Todos os casos do manifesto foram executados pelo original. A captura de saída
dinâmica fora do limite não é parte do manifesto por causa do timeout relatado
acima; nenhum resultado de erro dinâmico foi simulado.

## Validação final do Milestone 8 (21/10/2026)

A validação pendente acima foi concluída depois da implementação dos vetores
fixos e de `Const`. Os 59 casos foram compilados/executados localmente e
comparados com a distribuição Blitz3D V1.118 via Proton: os relatórios por
suíte estão em `artifacts/m8-*-differential.json`, e a consolidação por caso,
incluindo as tentativas, está em `artifacts/m8-differential.json`.

| Suíte | Casos | BlitzCS local | Comparações finais coincidentes |
| --- | ---: | ---: | ---: |
| Compatibilidade base | 15 | 15/15 | 14/15 |
| Objetos | 3 | 3/3 | 3/3 |
| Lifetime | 10 | 10/10 | 10/10 |
| Include/multifile | 5 | 5/5 | 5/5 |
| Vetores fixos | 13 | 13/13 | 13/13 |
| Constantes | 13 | 13/13 | 13/13 |
| **Total** | **59** | **59/59** | **58/59** |

Na primeira rodada, o original capturou 13/15 casos da suíte base: `precedence`
teve erro do adaptador antes de qualquer processo nativo ser lançado
(`processes: []` em `artifacts/m8-original-evidence/precedence.json`), enquanto
`upper-bound-error` divergiu no código de saída. Repetimos apenas
`precedence`; a captura nativa observada foi `14`, `4.0`, `64.0`, `0`, `1`, e
ela coincidiu com a saída local. O relatório dessa repetição e os logs de
processo estão em `artifacts/m8-precedence-retry-differential.json` e
`artifacts/m8-original-retry-evidence/precedence.json`. A falha inicial foi
preservada no relatório consolidado; houve 60 tentativas nativas para 59 casos.

A única divergência final permanece `upper-bound-error`: a distribuição
original encerra com erro de runtime, mas retorna código de processo 0; BlitzCS
retorna código diferente de zero. Não normalizamos o código original para
obter uma coincidência. As 59 execuções locais passaram, não houve erro de
adaptador não resolvido nem comparação pendente.

As declarações fixas e os casos documentados de constantes agora têm
comparações reais, inclusive limites inclusivos, inicialização, escopo,
vetores em campos, referências de objetos e erros de compilação. A compilação
por arquivo do snapshot SCP também foi repetida; o resultado atual é 2/30, e
`Main.bb` ultrapassou a declaração de vetor da linha 291 para parar em
`RuntimeError` na linha 16. A matriz, os diagnósticos e os limites dessa
medição estão em [`scp-milestone8.md`](scp-milestone8.md). A rodada não
demonstra que módulos ou o jogo inteiro compilam. O próximo bloqueio de
linguagem a investigar é `RuntimeError`; depois dele, continuam por validar os
diagnósticos seguintes, comandos nativos e dependências externas.
