# Verificação — 2026-10-20

Ambiente: Linux x64, SDK .NET 10.0.401, runtime .NET 10.0.12.
Diretório inicial vazio e sem repositório Git; SDK e Trail estavam instalados
fora do PATH. Foi inicializado um repositório Git local, sem remote/publicação.

## Comando reproduzível

```bash
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
```

Resultado observado:

- Build Release: 0 avisos, 0 erros.
- Testes do compilador: 23 aprovados, 0 falhos, 0 ignorados.
- Testes da CLI/emissão: 13 aprovados, 0 falhos, 0 ignorados.
- Assembly `artifacts/hello.dll` executado por `dotnet`, com saída:

```text
Hello from BlitzCS
14
-3
```

Os testes incluem execução de assemblies em processos separados, precedência,
divisão negativa, variáveis implícitas, case-insensitivity, separadores,
comentários/strings, caminhos com espaços, erros e mapeamento de origem Roslyn.
Os testes iniciais falharam antes da implementação do compilador e da emissão;
regressões de palavras reservadas também foram reproduzidas antes da correção.

`dotnet list BlitzCS.slnx package --vulnerable --include-transitive` não encontrou
pacotes vulneráveis nas fontes NuGet consultadas. Licenças das dependências
diretas foram conferidas nos arquivos `.nuspec` restaurados: Roslyn e Test SDK
MIT; xUnit e runner Apache-2.0. Não foi incorporado código upstream.

## Uso efetivo de Trail-Code

Instalação existente em
`/var/home/marcelo/.local/share/dev-agent-tools/npm/lib/node_modules/@trailhq/trail-code`.
Executados `init --agents agents --no-global --no-mcp --no-hooks --no-statusline
--no-build --yes`, `build`, `map`, `ask --source`, `skeleton`, `status` e `note`.
O `map` inicial indicou ausência de grafo; após criação do código, `build`
indexou C# com sucesso e as consultas recuperaram contratos, parser e emissão.
O uso foi local, determinístico, sem passe LLM `--deep`.

## Limites

Não foi executado Blitz3D original. Os testes validam o subconjunto implementado
e o host de console, sem provar equivalência gráfica ou compatibilidade completa.
Pesquisa de arrays, memória, entidades, animação e colisões continua pendente.
Milestone 0 tem baseline funcional e Milestone 1 tem sua primeira fatia; não são
declarados completos em todo o escopo do documento de projeto.

## Entrega híbrida inicial

O mesmo `scripts/verify.sh` agora valida também `samples/Hybrid/answer.bb`.
Resultado final após estabilização dos arquivos:

- Build Release: 0 avisos, 0 erros.
- Compilador: 49 testes aprovados.
- Emissão/CLI: 35 testes aprovados.
- Total: 84 aprovados, 0 falhos, 0 ignorados.
- `artifacts/answer.dll`: saída `42`, processo terminado com sucesso.
- Exemplo legado: saída preservada (`Hello from BlitzCS`, `14`, `-3`).

A integração cobre chamadas nos dois sentidos no mesmo assembly, mutação das
globais, escopos locais isolados, delegates, funções posteriores à chamada,
retorno implícito zero, tipos inválidos e diagnósticos de C# apontando ao `.bb`.
Delimitadores são testados dentro de comentários e strings comuns, verbatim,
interpoladas e raw. Uma regressão verifica a entrada BASIC quando um módulo C#
também possui `Main`.

TDD foi verificado com 11 testes híbridos de integração falhando antes do suporte
e com o teste da entrada explícita falhando antes da nova opção de emissão.
Uma inversão temporária da validação de argumentos também fez os testes falharem;
um build simultâneo capturou essa mutação. Após restaurar a condição e encerrar
as edições concorrentes, o build Release completo e os 84 testes passaram.

O contrato e as limitações constam de `hybrid.md`. A camada de interop inicial
está implementada para inteiros; não se declara concluído todo o Milestone 2
original (tipos avançados, eventos de entidades e assemblies externos pendentes).

## Milestone 3 — Typed Interoperability & Semantic Compatibility

Comando: `PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh`.
Resultado final em 2026-10-20:

- Build Release: 0 avisos, 0 erros.
- Compilador: 66 testes aprovados.
- Emissão/CLI: 66 testes aprovados.
- Total: 132 aprovados, 0 falhos, 0 ignorados.
- Exemplos `hello.bb` e `answer.bb`: saídas anteriores preservadas.
- `samples/Hybrid/typed.bb` gerou `artifacts/typed.dll` e executou com saída:

```text
42
3.5
[Blitz] C#
```

Antes das mudanças, a suíte Release anterior passou integralmente em seus 84
casos. Casos negativos que proibiam os primitivos recém-suportados foram movidos
para testes positivos dos mesmos programas; não representam requisitos de
rejeição desta etapa. Os demais casos de erro e regressão foram preservados.

TDD: a nova integração tipada inicialmente teve 17 falhas em 25 casos contra a
versão anterior. Foram reproduzidas também falhas de seleção de overload e da
sintaxe histórica de casts sem parênteses, antes das correções.

As execuções cobrem Integer/Single/String nos dois sentidos, estado compartilhado,
casts implícitos/explícitos, prefixos numéricos, seis dígitos de formatação,
precisão binária Single, cultura `pt-BR`, String nula, defaults tipados, overloads
exatos/promovidos/ambíguos e tipos/métodos inválidos. O binário gerado é executado
em processo separado em testes de integração.

Limites: sem execução do compilador original; compatibilidade de fronteira x87,
MSVC, encoding e folding de conversões String ainda não está estabelecida.
Consulte `typed-interop.md` e `typed-semantics-research.md`.

## Milestone 4 — Legacy Language Compatibility & Differential Validation

Comando final: `PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh`.

- Build Release: 0 avisos, 0 erros.
- Compilador: 141 aprovados (66 anteriores + 45 controle + 30 arrays).
- Emissão/CLI: 134 aprovados (66 anteriores + 38 controle + 30 arrays).
- Total .NET: 275 aprovados, 0 falhos, 0 ignorados.
- Harness Python: 9 testes aprovados.
- Fixtures locais: 15/15 passaram.
- Comparações reais com o original: 0 realizadas, **15 pendentes**.

Os 132 testes da base foram executados antes das mudanças e continuam cobertos
integralmente. O checkpoint de controle passou primeiro com 215 testes .NET;
arrays foram integrados depois. TDD reproduziu 25 falhas de integração de controle
e 13 falhas positivas de arrays antes das implementações correspondentes.
Nenhum executável original foi encontrado; as expectativas locais do manifesto
não foram usadas como se fossem resultados capturados do original.

Exemplos executados na verificação final:

| Programa | Saída |
|---|---|
| `samples/Legacy/hello.bb` | `Hello from BlitzCS`, `14`, `-3` |
| `samples/Hybrid/answer.bb` | `42` |
| `samples/Hybrid/typed.bb` | `42`, `3.5`, `[Blitz] C#` |
| `samples/Hybrid/legacy-loops.bb` | `15` |
| `samples/Legacy/arrays.bb` | `21`, `[]`, `ready`, `1.5`, `0` |

Controle: If de bloco/linha, ElseIf, While, For inteiro/Single e contador indexado,
Step positivo/negativo/zero constante, Repeat, Select com alternativas, Exit
aninhado, retornos condicionais, aliases relacionais, precedência e avaliação
bitwise eager. Arrays: limites inclusivos, 1D/2D/3D, defaults, re-Dim com descarte,
funções, reconhecimento em ordem do fonte, rank/tags inválidos, índices/dimensões
inválidos e integração escalar C# dentro dos laços.

O relatório real está em `artifacts/differential.json`, com resultados de cada
execução local e `comparison_status: pending`. A flag `--require-original` pode
exigir capturas quando houver adaptador disponível. Testes do harness usam mocks
somente para verificar seu protocolo, incluindo rejeição de falso sucesso após
timeout; esses dados não representam execução do original.

Uma lacuna anterior foi corrigida: `4 / Int("none")` agora falha durante a
compilação. Permanecem sem comparação executável as fronteiras x87/MSVC, encoding
e ordem de efeitos entre índices e valor em atribuições. Guards de array por eixo
e contra índices negativos são deliberadamente mais fortes que os checks
cumulativos debug e acesso sem check release do original. O contrato completo
e limites ficam em `legacy-language.md`; o protocolo, em `differential.md`.

## Legacy Object System & Differential Validation — 2026-10-21

Comando executado após estabilização das implementações:

```bash
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
```

Resultado observado, processo concluído com código 0:

- Build Release: 0 avisos, 0 erros.
- Compilador: 177 testes aprovados (141 anteriores + 36 novos).
- CLI/emissão: 168 aprovados (134 anteriores + 34 novos).
- Total .NET: **345 aprovados**, 0 falhos, 0 ignorados.
- Harness/adaptador: **16 testes Python aprovados** (9 anteriores + 7 novos).
- Expectativas locais: 15 fixtures anteriores + 3 objetos passaram.
- Os seis exemplos foram compilados e executados; saídas anteriores preservadas.
- Novo `samples/Hybrid/objects.bb`: saída `42` e `1`, término com código 0.

Os 275 testes .NET anteriores foram executados antes da implementação e
preservados. Novos testes cobrem campos tipados/defaults, referências próprias e
cruzadas, funções/arrays de objetos, aliases, invalidação, exclusão repetida,
listas vazias, navegação, reordenação, aninhamento e mutações em `For Each`,
fachadas híbridas primitivas e diagnósticos de tipo/objeto inválido.

TDD: a integração de objetos inicialmente reproduziu 20 falhas contra o
compilador anterior; após a implementação, duas expectativas adicionais
(`Delete Null` e navegação por alias excluído) falharam e foram conferidas no
original antes da correção. A comparação executável também reproduziu o erro
`1e-004` versus `1.e-004`; runtime e folding de constantes foram alinhados, com
regressões reproduzidas antes da correção.

As execuções locais acima não usam adaptador original e permanecem marcadas
como `pending` nesses relatórios específicos. As comparações reais são
registradas separadamente em `original-differential.json` e
`original-object-differential.json`, com comandos/procedência em
[original-adapter.md](original-adapter.md). As afirmações de indisponibilidade
nas seções anteriores são checkpoints históricos, anteriores a esta execução.

Limites: os nós de objetos excluídos ficam retidos até o término, podendo
acumular memória; não há recuperação/reutilização nativa por refcount. Vetores
em campos, handles e casts Object estão pendentes. A ponte pública permanece
primitiva, sem ponteiros nativos. Não foi implementada renderização 3D.

### Comparações reais finais

A mesma versão Release foi comparada pelo adaptador genuíno, em série:

| Manifesto | Local | Coincidiram com o original | Divergências | Falhas do adaptador | Pendentes |
|---|---:|---:|---:|---:|---:|
| `fixtures.json` | 15/15 | 14 | 1 | 0 | 0 |
| `objects.json` | 3/3 | 3 | 0 | 0 | 0 |

Total: **17 coincidências em 18 fixtures executadas nos dois runtimes**. Os
três casos de objetos confirmaram lifetime/aliases, exclusão durante iteração e
ordenação por Insert. `float-format` passou após a correção do ponto decimal.
As comparações incluem casos de erro de compilação; não representam 18 programas
bem-sucedidos nem equivalência de toda a linguagem.

A única divergência final é `upper-bound-error`: ambos produziram a fase
`runtime_error` e saída vazia, porém o original terminou naturalmente com código
`0` após o diálogo nativo `Array index out of bounds`; BlitzCS terminou com
código `-6` nesta execução (exceção .NET não tratada). O harness corretamente
registra `success/failure exit status differs`; a execução diferencial desse
manifesto termina com código 1. O erro não foi ocultado nem contado como
coincidência. A execução do manifesto de objetos terminou com código 0.

Relatórios finais: `artifacts/original-differential.json` e
`artifacts/original-object-differential.json`. O relatório inicial 13/15, antes
da correção de formatação, foi preservado em
`artifacts/original-differential-before.json`. Logs e hashes dos fontes ficam em
`artifacts/original-evidence/`; probes extras de Delete Null e navegação por
alias excluído ficam em `artifacts/original-object-probes.json`.

A observação é limitada à distribuição oficial V1.118, modo debug original e
instrumentação de saída descrita em `original-adapter.md`. Não valida gráficos,
fronteiras numéricas completas, todos os encodings ou recuperação de memória.
Próximos passos: ampliar os casos diferenciais de mutação/aliases, implementar
recuperação de nós sem alterar travessias e definir o contrato de término por
erro antes de tentar compatibilizar códigos de saída.

## Milestone 6 — Object Lifetime, Managed Memory & Differential Compatibility

Verificação final em 2026-10-21, SDK 10.0.401 / runtime 10.0.12:

```bash
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
```

O comando terminou com código 0:

- Build Release: **0 avisos, 0 erros**.
- Compilador: **182 aprovados**; CLI/emissão: **172 aprovados**.
- Total: **354 testes .NET**, 0 falhos, 0 ignorados; os 345 anteriores preservados.
- **16 testes Python** preservados e aprovados.
- **28 fixtures locais** aprovadas: 15 anteriores, 3 objetos, 10 lifetime.
- Os seis exemplos anteriores foram compilados e executados com as mesmas saídas.
- Nova integração híbrida força GC e executa 100.000 New/Delete, mantendo um
  alias excluído durante Insert e chamadas C# ↔ BASIC: saída `42,1,1,1,3,1`.

### Recuperação comprovada e custos

Antes da alteração, o baseline de 345 testes passou novamente e o probe
reproduziu a retenção de 100.000 objetos excluídos após GC. Dois testes de
recuperação falharam antes da solução. Foram separados identidade e posição;
Delete limpa campos, e a posição mantém somente referência fraca ao excluído.
A proposta e as alternativas rejeitadas estão em `object-lifetime-design.md`.
Não há refcount manual nem finalizers no runtime implementado.

Cinco testes novos de memória comprovam coleta após liberação de alias, quebra
de referências entre objetos excluídos, coleta após limpeza de slot de array e
limpeza incremental sob churn. A lista estática e a assembly continuam vivas
durante o probe. Na comparação isolada Release com o runtime histórico exato:

| Medida, 100.000 New/Delete | Antes | Depois |
|---|---:|---:|
| Referências fortes da lista a excluídos | 100.000 | 0 |
| Alvos fracos excluídos após GC | não aplicável | 0 |
| Registros após GC, antes da manutenção | 100.000 | 12.473 |
| Registros após manutenção incremental | 100.000 | 0 |
| Bytes alocados na thread durante o programa | 4.002.472 | 10.402.928 |
| Bytes gerenciados vivos do processo após coleta | 19.095.392 | 15.429.120 |
| Bytes comprometidos pelo GC | 29.675.520 | 26.644.480 |

O depois usou 6.237 chamadas normais de Delete tipado com valor null, sem criar
novos registros. O zero foi conferido antes do Scavenge de diagnóstico.
Alocações aumentaram aproximadamente 2,6 vezes neste workload; isso é um custo
real da separação de posições e referências fracas. Heap e comprometimento são
dados do processo de teste, não limites garantidos por objeto. Não há assertions
de RSS, limites de bytes ou prazo de coleta. Espaço virtual reservado pelo GC
**não foi medido**; a API portátil usada não fornece esse contador.
Metodologia, comandos e fixture histórica reproduzível: `memory-methodology.md`.

### Diferenciais reais com o Release final

Os três manifestos foram executados novamente contra o compilador/runtime
oficial V1.118 via Proton, em série, usando o adaptador genuíno:

| Manifesto | Local | Coincidências | Divergências | Erros do adaptador | Pendentes |
|---|---:|---:|---:|---:|---:|
| `fixtures.json` | 15/15 | 14 | 1 | 0 | 0 |
| `objects.json` | 3/3 | 3 | 0 | 0 | 0 |
| `lifetime.json` | 10/10 | 10 | 0 | 0 | 0 |

Total: **27 coincidências em 28 comparações**, incluindo **10/10 de lifetime**.
Os casos abrangem criação/exclusão massiva, aliases, exclusão em For Each,
Insert/reordenação, ciclos, múltiplas exclusões, mutações em iteração e referências
em arrays/campos. O caso adicional de vizinho movido confirma que um alias
excluído conserva posição, e não apenas uma fotografia de seus antigos vizinhos.

Permanece somente `upper-bound-error`: ambos têm fase runtime_error, mas o
original encerra com 0 e BlitzCS não zero. Isso agora é uma decisão explícita
do contrato moderno em `runtime-errors.md`: diagnósticos e automação prevalecem
sobre imitar esse status. Não há perfil Legacy de término por erro, nem
normalização do resultado diferencial. O manifesto com a divergência encerrou
o harness com código 1; os outros dois encerraram com código 0.

Relatórios: `artifacts/original-differential.json`,
`artifacts/original-object-differential.json`, `artifacts/lifetime-differential.json`;
proveniência e capturas em `original-adapter.md` e `lifetime-original-results.md`.
Expectativas locais continuam separadas das capturas reais.

### Limitações e próximos passos

Aliases ainda observáveis preservam a identidade excluída e sua posição, como
exige a navegação. Objetos vivos pertencem à lista até Delete. Após GC sem nova
atividade, registros vazios podem permanecer até manutenção/travessia posterior,
mas não retêm os objetos BASIC excluídos. Travessias podem saltar posições de
aliases excluídos ainda observáveis. Não se reproduzem endereços, reutilização
ou refcount nativos; não se afirma compatibilidade completa.

Próximos passos: ampliar diferenciais de mutações adversariais e medir cargas
com listas vivas grandes/aliases persistentes; investigar reserva virtual do GC
somente se necessário ao orçamento de memória. Não foram implementados renderer,
física ou editor. Documentação e learnings atuais substituem a limitação de
retenção permanente registrada como checkpoint no milestone anterior.

## Milestone 7 — Real-World Compatibility & Multi-File Compilation

Comando final em 2026-10-21:

```bash
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
```

Terminou com código 0:

- Build Release: **0 avisos e 0 erros**.
- Compilador: **196 testes aprovados**; CLI/emissão: **179 aprovados**.
- Total: **375 testes .NET**, preservando os 354 anteriores, sem falhos/ignorados.
- **20 testes Python** aprovados, preservando os 16 anteriores.
- **33 fixtures locais** aprovadas: 15 básicas, 3 objetos, 10 lifetime e 5 multifile.
- Os seis exemplos anteriores preservaram suas saídas.
- Novo projeto `samples/Hybrid/MultiFile/main.bb`, com quatro arquivos: gerou um
  único `artifacts/multifile.dll` e imprimiu **42**, com chamadas BASIC → C# →
  BASIC e mutação de global em bloco C#.

Os 21 testes .NET novos cobrem ordem de includes aninhados, base na pasta do
principal, caminhos Windows, comparação sem caixa, duplicatas, ciclos, reentrada
do principal, escopo em funções, compartilhamento de nomes de arrays, barreiras
sintáticas entre arquivos e diagnósticos de léxico/parser/tipos/Roslyn/CS::/guards
com caminho original. Foram reproduzidas 11 falhas antes do suporte; depois,
dois casos de erro em parâmetros de funções incluídas vazias reproduziram um
fallback incorreto para o arquivo principal e foram corrigidos.

O adaptador preserva as diretivas Include e a árvore no compilador original,
sem pré-expandir o fonte nativo. Um teste adicional e captura nativa confirmam
Return antecipado em arquivo incluído dentro de função; instruções de contagem
de conclusão do adaptador ficam apenas no principal, evitando alterar esse fluxo.
Detalhes e limites: `include-research.md`, `original-adapter.md`, `multifile.md`.

### Auditoria e fronteira real de SCP

Fonte oficial Regalis11/scpcb, commit
`edb8fe0840b78f14d1aef3a0bf6174630e7be296`, mantida intacta fora do repositório.
Foram auditados 30 arquivos .bb (~52 mil linhas), com grafo estático de 24 arquivos
na closure do principal. O Release compilou isoladamente **1/30**: KeyName.bb.
Um wrapper temporário incluiu esse arquivo original sem modificá-lo e executou
KeyName$(1), KeyName$(58), KeyName$(200): **Esc, Caps Lock, Up**.

Main.bb falhou em `Global SCP1025state#[6]`, linha 291, coluna 21: caractere '['
não suportado. O lexer lê o principal inteiro antes do parser, portanto esse erro
**não prova processamento da closure de includes nem binding do texto anterior**.
MusicPlayer.bb atingiu o resolvedor e falhou pela ausência real de BlitzAL.bb no
checkout. Const, vetores fixos, parâmetros opcionais, formas de chamada legadas,
Data/Read/Restore, funções de biblioteca e APIs de engine permanecem bloqueios.
A classificação implementado/parcial/ausente/não verificado e o primeiro erro de
cada arquivo constam de `scp-compatibility.md`.

### Arquitetura e escopo

Blitz3DCS consumirá BlitzCS. Não foram adicionadas dependências de renderização,
áudio, física ou engine no núcleo. O milestone conclui Include e infraestrutura
multifile; Data/Read/Restore, labels/Goto/Gosub foram pesquisados no original e
ficaram para fatias posteriores. O Return existente permanece o de funções.
Não se afirma que SCP compile integralmente nem que todos os recursos auditados
sejam semanticamente equivalentes. Documentação e learnings Trail-Code atualizados.

### Diferenciais reais finais do Milestone 7

O Release final e o adaptador atualizado executaram os quatro manifestos em
série, com binários oficiais V1.118 via Proton:

| Manifesto | Local | Coincidências | Divergências | Erros do adaptador | Pendentes |
|---|---:|---:|---:|---:|---:|
| `fixtures.json` | 15/15 | 14 | 1 | 0 | 0 |
| `objects.json` | 3/3 | 3 | 0 | 0 | 0 |
| `lifetime.json` | 10/10 | 10 | 0 | 0 | 0 |
| `multifile.json` | 5/5 | 5 | 0 | 0 | 0 |

Total: **32 coincidências em 33 comparações**, preservando as 27 coincidências
anteriores. Todas as cinco novas fixtures multifile coincidiram: base do
principal/deduplicação, ciclo entre incluídos, reentrada do principal, include
inexistente e Return antecipado em função com include. A única divergência
continua sendo `upper-bound-error`: runtime_error nos dois, original exit0 e
BlitzCS exit-6 nesta execução, conforme contrato moderno já documentado.
O harness desse manifesto retorna 1; os outros três retornam 0. Nenhuma diferença
foi normalizada para aparecer como coincidência.

Relatórios reais: `artifacts/original-differential.json`,
`artifacts/original-object-differential.json`, `artifacts/lifetime-differential.json`
e `artifacts/multifile-differential.json`. Relatórios locais do verify continuam
separados e pending sem adaptador, como exige o protocolo. Xvfb de captura foi
encerrado; prefixo isolado e binários originais preservados.

A auditoria dos 30 módulos SCP é reproduzível por `tools/scp_compile_probe.py`;
`artifacts/scp-compilation.json` contém hashes e primeiros diagnósticos. Esse
probe não executa código de jogo e não confunde diagnósticos de compilador com
infraestrutura (no resultado observado: 1 compilou, 29 diagnósticos, 0 falhas de
infraestrutura). Não há relação entre 1/30 arquivos isolados e uma porcentagem
de compatibilidade total: os arquivos têm tamanhos, dependências e papéis distintos.

## Milestone 8 — vetores fixos, Const e SCP (2026-10-21)

`PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh`
executa a validação reproduzível. O build Release desta etapa terminou com
**0 avisos e 0 erros**. Passaram **428 testes .NET** (231 compiler, 197 CLI),
sem falhas ou ignorados, preservando os 375 anteriores e acrescentando 53.
Passaram também os **20 testes Python** existentes. Os testes adicionais cobrem
limites inclusivos, inicialização e escopos, tipos de elemento, lvalues, efeitos
colaterais, erros de compilação, Const, sombreamento e liberação de referências
de vetores em campos após Delete. O teste de lifetime separa objetos vivos de
filhos já excluídos; não usa RSS como evidência de coleta.

O exemplo `samples/Hybrid/FixedVectors/main.bb`, com três arquivos e ponte C# ↔
BASIC por funções primitivas, executou em Release e imprimiu `42` e `3.5`.
Os sete exemplos anteriores também executaram no script. A auditoria imutável
do SCP avançou de **1/30 para 2/30** arquivos independentes: `Difficulty.bb`
passou a compilar, preservando `KeyName.bb`. O lexer não para mais no vetor de
`Main.bb:291`; o parser agora para em `RuntimeError` na linha 16, antes dos
Includes. Isso não prova que Main nem sua árvore de includes compila.

Contratos e limitações: [fixed-vectors.md](fixed-vectors.md), fonte e capturas
em [fixed-vectors-research.md](fixed-vectors-research.md), matriz antes/depois
em [scp-milestone8.md](scp-milestone8.md). Não foram modificados os fontes de SCP
nem adicionados mocks ou dependências de engine. Parâmetros opcionais,
Data/Read/Restore e sintaxe adicional ainda exigem fatias separadas.

Trail-Code: `trail build` e `trail map` foram executados; mapa com 120 arquivos,
453 símbolos e 584 arestas. Learning M8 adicionado em `.trail/learnings/`.
`trail check` não funciona neste checkout sem primeiro commit/HEAD (informa
"not a git repository", apesar de `git rev-parse --is-inside-work-tree=true`).
Não foi criado commit para contornar essa limitação. A verificação apropriada
`trail build --check` passou na revisão final. O parser Trail classifica
`.bb` como Clojure; seu índice não é prova de aceitação sintática BlitzBasic.

A execução completa de `scripts/verify.sh` terminou com status **0**. Os seis
manifestos passaram localmente: 15+3+10+5+13+13 = **59/59**. Esses relatórios
locais marcam a comparação original como pendente por desenho; capturas reais
via Proton são registradas separadamente nos relatórios `artifacts/m8-*`.

### Comparação original final

Os seis manifestos foram executados serialmente com o adaptador genuíno Blitz3D
V1.118 via Proton: core15, objetos3, lifetime10, multifile5, vetores13, Const13.
Os 26 casos novos coincidiram integralmente. Na primeira tentativa, precedence
teve `adapter_error` antes de lançar o compilador; essa tentativa permanece em
`artifacts/m8-core-differential.json`. A repetição isolada real em
`artifacts/m8-precedence-retry-differential.json` compilou, executou e coincidiu.
Considerando essa repetição para o mesmo programa, o resultado final é
**58/59 coincidências**, preservando as 32 anteriores e adicionando 26.

A única divergência continua sendo `upper-bound-error`: o original encerra erro
com status0 e a BlitzCS com status não zero, conforme contrato moderno explícito.
Não houve nova divergência semântica nos casos executados. Esses resultados
comparam valores/fluxo instrumentados, não gráficos nem compatibilidade completa.
Relatórios brutos `artifacts/m8-{core,objects,lifetime,multifile,vectors,constants}-differential.json`
e capturas em `artifacts/m8-original-evidence/` preservam evidência de cada rodada.

## Milestone 9 — conclusão de linguagem e integração externa (2026-10-22)

A execução final de `scripts/verify.sh` terminou com status **0**. Build Release:
**0 avisos e 0 erros**. Testes .NET: **469 aprovados**, 247 compiler e 222 CLI,
sem falhas ou ignorados; os 428 anteriores foram preservados. Testes Python:
**25 aprovados**, preservando os 20 anteriores e acrescentando cinco para
classificação do probe, catálogo e passagem de configuração externa.

Passaram **65/65 fixtures locais** (59 anteriores + seis M9). Os exemplos legados,
híbridos e multifile anteriores mantiveram as saídas. O programa externo
`samples/Native/runtime-error.bb` imprimiu somente `before native failure` e
terminou com status não zero e mensagem `native runtime demonstration`. Essa
falha é esperada e verificada pelo script; o shell pode registrar `Aborted`
quando o .NET encerra a exceção não tratada. Os arquivos stdout/stderr estão em
`artifacts/native-runtime-error.*`.

Novos testes cobrem defaults e conversões, hexadecimais signed/wrap, Field com
inicializador aceito e alocação zerada, comandos sem parênteses, agrupamento de
argumentos e comandos sem argumentos separados por `:`. O registro externo tem
testes de tipos, aridade, nomes, colisões e identidade de módulos; integração
exercita Includes, chamadas BASIC → módulo .NET, C# → BB → módulo e CS:: direto
a uma assembly registrada. A última asserção de CS:: foi verificada novamente
em teste focado, aprovado, depois da execução completa.

A revisão identificou e corrigiu sobrescrita de DLL referenciada quando escolhida
como saída e colisões de nomes de DLL na publicação. Testes verificam rejeição
antes de tocar os arquivos. Dependências transitivas de módulos não são
publicadas/resolvidas automaticamente nesta fatia e permanecem responsabilidade
do host, conforme [external-functions.md](external-functions.md).

A [auditoria SCP M9](scp-milestone9.md) recompilou as mesmas 30 unidades originais,
com e sem catálogo de RuntimeError: **2 compiladas, 15 falhas sintáticas,
1 semântica, 10 símbolos nativos não registrados, 2 dependências externas**,
sem falhas lexicais nem de infraestrutura. Todos os hashes permanecem iguais.
O baseline classificado a partir da captura M8 era 2 compiladas, 25 sintáticas,
1 nativa não registrada e 2 externas. A redução de sintaxe expõe dependências;
não representa aumento da taxa de compilação isolada nem equivalência do jogo.
Main agora para em `StrictLoads.bb:59`, na construção `Object` ainda ausente,
antes de o bind semântico alcançar RuntimeError. O módulo RuntimeError foi
validado separadamente e não mascara esse bloqueio.

Contrato público, alternativas e pesquisa: [external-functions-design.md](external-functions-design.md),
[m9-language-research.md](m9-language-research.md) e
[m9-original-research.md](m9-original-research.md). Não há renderer, física,
áudio, mocks de comandos ou dependência do núcleo em Blitz3DCS/LegacyRuntime.

### Comparação nativa final M9

`artifacts/m9-final-differential.json` cruza os sete relatórios locais finais com
as capturas reais da distribuição oficial via Proton: **65/65 locais aprovadas,
64 coincidências originais atuais, 0 divergências atuais observadas e 1 erro de
adaptador não resolvido**. As seis fixtures novas coincidiram e as 58 coincidências
anteriores foram preservadas. Não há casos não executados marcados pending.

O caso `upper-bound-error` sofreu dois timeouts de 6 segundos do adaptador nesta
rodada; seu resultado atual continua `adapter_error`. Não o substituímos por
captura histórica nem contamos esse timeout como coincidência. A captura M8 de
mesmo hash permanece separada em `historical_comparison`: ela documenta a
divergência já conhecida, original runtime_error/status0 versus BlitzCS não zero.
Essa referência histórica não resolve a indisponibilidade atual do adaptador.

`artifacts/m9-original-captures.json` preserva os 64 outputs atuais, o erro e a
procedência de cada fonte. Logs de processo e hashes estão em
`artifacts/m9-original-evidence/`, `m9-original-recapture-evidence/`,
`m9-original-recapture2-evidence/` e `m9-command-grouping-evidence/`.
A comparação usa `compare_capture` do harness existente e normaliza somente
newlines; não altera códigos de saída nem fabrica resultados. Xvfb e processos
Proton da tarefa foram encerrados.

Trail-Code: `trail build` e `trail build --check` executados com sucesso; grafo
wiring sincronizado (135 arquivos, 658 nós). Learning M9 incluído em
`.trail/learnings/2026-10-22-milestone-9-linguagem-e-registro-externo-codex.md`.
Não se executou a camada opcional de resumos deep; o índice de wiring não é
prova da semântica de arquivos `.bb`.

## Milestone 10 — handles gerenciados e progresso integrado (2026-10-22)

Build Release: **0 avisos e 0 erros**. Passaram **510 testes .NET**
(270 compiler, 240 CLI), sem falhas ou ignorados, preservando os 469 anteriores.
Passaram **32 testes Python**, preservando os 25 anteriores. Os sete novos
verificam a métrica integrada e a retenção de evidência em timeouts/saídas anormais.

Os onze manifestos locais somam **79 fixtures aprovadas**, contra 65 no baseline.
A consolidação `artifacts/m10-final-differential.json` verifica caminho e SHA-256
atual e compara capturas reais da distribuição oficial V1.118 via Proton:
**79 capturas comprovadas, 78 coincidências, 1 divergência conhecida, zero erros
de adaptador não resolvidos**. As 64 coincidências anteriores foram preservadas.
`upper-bound-error` agora voltou a executar no original: phase runtime_error,
stdout vazio, status0. BlitzCS mantém runtime_error com status não zero. Os
status não foram normalizados para ocultar a divergência.

O adaptador conserva estágio, comando, stdout/stderr parciais, bytes de saída
instrumentada e marcador de término inclusive ao falhar. A lógica de dismissal
não foi alterada. Um retry de handles/arrays completou com hash correto e cinco
linhas1; o crash anterior e a associação inicial de basename sem prova foram
preservados/retratados, respectivamente. Cada resultado final válido tem sua
própria evidência; uma resposta de outro arquivo com mesmo basename não conta.

As fixtures numéricas revelaram uma diferença entre folding e execução:
Abs Float de constante -0 preserva o sinal; a V1.118 normaliza o Abs de variável
Float para +0. O compilador separa essas rotas. Abs de Integer mínimo, obtido por
`$80000000`, permanece negativo por wrap; Sgn mantém o tipo do operando. Os
probes decimais fora da faixa permanecem pesquisa, não prova desse caso mínimo.

Data/Read/Restore têm fixtures reais para coerções, label futuro, cursor entre
função/main e Includes. Float Data lido como Integer **trunca**, diferentemente
do cast BASIC comum que arredonda. Probes iniciais com Goto ficaram fora do
manifesto: as versões finais, sem controle de fluxo fora desta fatia, foram
recapturadas no original. Exaustão Data tem erro local testado; seu probe nativo
mostrou Out of data mas sofreu timeout, portanto não estabelece status original.
Goto/Gosub continuam não implementados.

O exemplo `samples/Hybrid/Handles/main.bb` executou com saída `42`, `-1`, `0`.
Ele inclui dois arquivos, passa handles Integer por C# ↔ BB e invalida lookup
após Delete. O teste Data híbrido usa uma função BB pública para consumir o
mesmo cursor que BASIC, sem depender do nome interno do runtime.

### Estresse e recuperação de memória

Um teste executou 100.000 ciclos New/Handle/Object/Delete, verificou zero lookups
stale e identificadores não reutilizados. A tabela terminou sem entradas e com
capacidade limitada ao pico simultâneo. Outro teste força coleta após remover
um alias de objeto excluído e autorreferenciado, mantendo assembly/tabela/lista
enraizadas: o objeto foi coletado e seu Integer permaneceu guardado. Exaustão
Int32 foi simulada apenas no teste e falha antes de registrar ou reciclar IDs.

A medição `samples/Diagnostics/handle-memory.bb` aquece 1.000 ciclos e executa
mais duas etapas de 100.000. Na verificação final inicial, cada etapa alocou
11.200.448 bytes na thread. Memória viva após GC/limpeza foi 59.936 e 61.544 bytes;
memória comprometida pelo GC foi 10.575.872 e 10.571.776 bytes. **Zero entradas
vivas de handles, capacidade3, zero posições restantes e zero falhas lógicas**
após cada etapa. São observações do ambiente, não thresholds dependentes de
coletor/timing. Os bytes estimados recuperados incluem ruído do host. RSS não
foi usado como prova. O JSON reproduzível atual está em `artifacts/handle-memory.json`.

### SCP antes e depois

A [matriz M10](scp-milestone10.md) preserva os mesmos 30 hashes do jogo. A taxa
individual continua **2/30** (Difficulty e KeyName); os primeiros erros sintáticos
caíram de15 para5, expondo7 semânticos,14 nativos não registrados e2 externos
no relatório final. São primeiros erros por unidade, não todos os erros do jogo.

Main integrado avançou de StrictLoads.bb:59 (Object.Sound) para Update.bb:838
(Else If) e depois **Skybox.bb:11:38**, em `LoadBrush_Strict(fname$,%110001)`.
O próximo bloqueio real é literal binário. O arquivo do diagnóstico tem
profundidade2, com witness Main→MapSystem→Skybox. A closure estática separada
contém23 Includes únicos e profundidade máxima2; isso não prova que todos foram
analisados. O probe informa um erro observado como limite inferior e deixa os
posteriores desconhecidos (`additional_errors=null`).

Não foram modificados os fontes SCP, adicionados mocks, renderer, física ou áudio.
O núcleo não depende de Blitz3DCS ou LegacyRuntime; chamadas externas permanecem
estáticas e o GC gerenciado preserva as garantias M6. Contratos e limitações em
[handles.md](handles.md), [handles-design.md](handles-design.md),
[math-M10.md](math-M10.md) e [data-M10.md](data-M10.md).

A execução completa final de `PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh`
terminou com **status0**, incluindo os79 locais, exemplos e medição de memória.
`trail build` e `trail build --check` também terminaram com sucesso: wiring com
164 arquivos,749 nós e748 arestas, sincronizado com o código. O learning M10
foi registrado; a camada opcional de resumos deep não foi executada e a
classificação de `.bb` pelo indexador não é prova da sintaxe da linguagem.
Xvfb/processos nativos da tarefa foram encerrados.
