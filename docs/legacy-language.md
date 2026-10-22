# Legacy Language Compatibility & Differential Validation

Esta etapa amplia o subconjunto original e preserva a ponte tipada descrita em
[typed-interop.md](typed-interop.md). A referência é o código Blitz3D no commit
`6beb288`, com evidências em [legacy-language-research.md](legacy-language-research.md).
Não foi executado o compilador/runtime original nesta validação.

## Estruturas de controle

São suportados `If / Then / ElseIf / Else / EndIf`, `While / Wend`,
`For / To / Step / Next`, `Repeat / Until / Forever`,
`Select / Case / Default / End Select`, `Exit` e seu aninhamento.

- `Then` é opcional. Terminador `:` ou newline após a condição seleciona a
  forma de bloco; a forma de uma linha termina no newline. O terminador original
  de `For` é `Next`, sem nome de variável.
- Condições convertem para Integer antes do teste de zero. Assim, `0.4` é falso
  pela conversão histórica com empates para par; a String `"2tail"` é verdadeira.
- `And`, `Or`, `Xor` são bitwise, avaliados dos dois lados e associados à esquerda
  no mesmo nível de precedência. `Not` é negação lógica do grupo, retornando 0/1;
  `Not 1 And 0` retorna 1. No lado direito de um operador, use parênteses quando
  exigidos pela gramática original.
- Comparações retornam 0/1. São aceitos `<`, `>`, `=`, `<=`, `>=`, `<>` e os
  aliases históricos `=<`, `=>`, `><`. Também estão disponíveis `Mod`,
  `Shl`, `Shr`, `Sar` e potência `^`. Potência associa à esquerda, com sinal
  unário mais forte: `-2^2` é 4.0 e `2^3^2` é 64.0.
- `For` avalia o início uma vez e reavalia `To` em cada teste. `Step` deve ser
  constante após conversão para o tipo numérico do contador. Passo positivo usa
  limite inclusivo crescente; passo negativo ou zero usa o limite decrescente.
  Passo zero pode repetir indefinidamente; nenhum limitador é inserido no programa.
  Contadores podem ser escalares ou elementos Integer/Float de arrays. Os índices
  do contador indexado são reavaliados em cada leitura/escrita do cabeçalho.
- `While` testa antes do corpo; `Repeat` executa o corpo antes de `Until`.
  `Forever` repete sem condição. `Exit` sai do laço mais próximo.
- `Select` avalia o seletor uma vez, testa alternativas de `Case` na ordem até a
  primeira correspondência, sem fallthrough. `Exit` dentro de `Select` sai do laço
  envolvente; sem laço, é erro. A emissão usa um fluxo que preserva esse destino.
- As estruturas não criam novos escopos de variáveis BASIC. `Local` pertence ao
  escopo da função/programa. Declarações `Global` e `Function` continuam restritas
  ao nível principal, conforme a gramática original.

Blocos C# executam na posição alcançada pelo controle de fluxo. Módulos continuam
no nível principal, e chamadas `CS::`/`BB.` preservam assinaturas e estado.

## Arrays `Dim`

```text
Dim grid%(2,1)
grid(2,1)=42
Print grid(2,1)
Dim grid(3,1)
Print grid(2,1)
```

Imprime `42` e `0`. Os limites superiores são inclusivos: o primeiro array tem
3 × 2 elementos. Dimensões múltiplas usam a primeira dimensão como eixo de menor
stride, conforme o armazenamento original.

Arrays Integer, Float e String inicializam com zero, zero Single e String vazia.
Dimensões e índices são expressões convertidas pelas regras de `Int`. Cada
execução de `Dim` descarta os dados anteriores **antes** de avaliar os novos
limites. Não há preservação de conteúdo. Rank e tipo explícito devem corresponder
à declaração existente; `Dim` sem tag em um array existente conserva seu tipo.

O parser reconhece o nome do array a partir de sua declaração `Dim` na ordem do
fonte. Não há forward declaration para acessos anteriores, inclusive em funções
definidas antes do `Dim`. Funções acessam e podem redimensionar arrays já
declarados no programa principal; não podem criar um array novo. Um local escalar
que sombreie o nome torna o acesso indexado inválido naquele escopo.

O armazenamento gerado fica fora da API `BB`. Funções BASIC que leem/escrevem
elementos podem ser chamadas normalmente por C#; não foi criada interop direta
de arrays. Os programas continuam compilados em um único assembly Roslyn.

Rank errado, tag incompatível, declaração vazia e array desconhecido geram erro
de compilação. Acesso fora dos limites, array não dimensionado, dimensão negativa
e produto acima de `System.Array.MaxLength` geram diagnóstico em execução com
nome do array e localização `.bb`. Falha de alocação também é tratada.

## Validação e divergências

`scripts/verify.sh` executa build Release, todos os testes .NET, os cinco exemplos,
os testes Python do harness e as fixtures de compatibilidade. Resultados efetivos
ficam em [validation.md](validation.md) e `artifacts/differential.json`.

O [harness diferencial](differential.md) mantém resultados locais separados das
comparações reais. Sem um adaptador do original, estas permanecem `pending`;
expectativas locais nunca são tratadas como capturas do original.

Diferenças e limites explicitamente conhecidos:

- Os índices são sempre verificados por eixo, inclusive negativos. O original
  verifica offsets cumulativos somente no modo debug e pode acessar memória sem
  verificação em release. Não se reproduz esse acesso inseguro fora do domínio
  válido dos arrays.
- `Print` usa o console nesta implementação; o original desenha texto. O
  adaptador diferencial precisa capturar a saída gráfica real do original.
- Ordem de efeitos colaterais entre índices e valor de uma atribuição indexada
  ainda não foi estabelecida por comparação executável.
- Arredondamento x87, fronteiras MSVC/CRT e encoding continuam com os limites do
  contrato tipado. O folding agora diagnostica `4 / Int("none")` na compilação.
- `For Each`, objetos `Type`, `Goto/Gosub`, `Data/Read/Restore`, `Include`, API
  completa do runtime e renderização continuam fora desta etapa.

O próximo passo diferencial é fornecer um adaptador original genuíno e comparar
as fixtures, começando por precedência, casts, laços, arrays e fronteiras numéricas.

## Atualização M10

As limitações acima descrevem a etapa original deste documento. For Each, Type
e Include foram adicionados em milestones seguintes. M10 implementa
[Handle/Object.Tipo](handles.md), Abs/Sgn e [Data/Read/Restore](data-M10.md).
Goto/Gosub e literais binários continuam pendentes; o próximo bloqueio SCP é
`%110001` em Skybox.bb. Há capturas reais via Proton, separadas das expectativas
locais em [validation.md](validation.md). Não se reivindica equivalência gráfica.
