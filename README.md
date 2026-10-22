# BlitzCS

Legacy syntax. Modern runtime. Zero mandatory rewrites.

Compilador experimental BlitzBasic → C# → assembly .NET 10, com interop híbrida
tipada, Roslyn, símbolos portáveis e diagnósticos mapeados para o `.bb`.

## Executar

Requer SDK .NET 10 estável no `PATH`. Neste ambiente ele está em
`/var/home/marcelo/.local/share/causoryn-dotnet`; acrescente esse diretório ao `PATH`
da sessão, se necessário.

```bash
dotnet build BlitzCS.slnx
dotnet test BlitzCS.slnx
dotnet run --project src/BlitzCS.CLI -- build samples/Legacy/hello.bb -o artifacts/hello.dll
dotnet artifacts/hello.dll
```

Saída esperada:

```text
Hello from BlitzCS
14
-3
```

O destino também recebe `.generated.cs`, `.pdb` e `.runtimeconfig.json`.
O assembly executa com o runtime .NET 10, sem precisar da CLI ou de Roslyn.
`bash scripts/verify.sh` automatiza build Release, testes .NET/Python, execução dos
exemplos e fixtures de compatibilidade. Requer Python 3 para o harness.

## Exemplo híbrido

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/answer.bb -o artifacts/answer.dll
dotnet artifacts/answer.dll
```

Imprime `42`. O exemplo chama C# a partir de BASIC, C# chama uma função BASIC e
um bloco C# modifica a global compartilhada antes de BASIC imprimi-la.
`Global answer%` é exposto como `BB.answer`; `Function Add%(a%,b%)` como
`BB.add(int,int)`. Os dois lados usam o mesmo campo `int` no mesmo assembly.
Blocos `<?cs ... ?>` executam instruções; `<?cs module ... ?>` definem classes.
O [contrato híbrido](docs/hybrid.md) detalha tipos, escopos e sintaxe `CS::`.

## Exemplo tipado

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/typed.bb -o artifacts/typed.dll
dotnet artifacts/typed.dll
```

Imprime `42`, `3.5` e `[Blitz] C#`. Troca Integer `%` (`Int32`), Float `#`
(`Single`) e String `$` (`String`) nos dois sentidos, com globais compartilhadas.
O [contrato tipado](docs/typed-interop.md) documenta conversões, sobrecargas e
limites da compatibilidade semântica.

## Linguagem legada e arrays

São suportados os blocos If/ElseIf/Else, While, For, Repeat, Select e Exit,
com aninhamento, precedência histórica, expressões relacionais e bitwise.
`Dim` oferece arrays Integer/Float/String, múltiplas dimensões, limites inclusivos,
acesso por funções e reinicialização ao executar `Dim` novamente.

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/legacy-loops.bb -o artifacts/legacy-loops.dll
dotnet artifacts/legacy-loops.dll
```

Imprime `15` usando C# dentro de laços BASIC. `samples/Legacy/arrays.bb` demonstra
arrays tipados e redimensionamento. Veja o [contrato legado](docs/legacy-language.md).
O [harness diferencial](docs/differential.md) executa as fixtures localmente e
aceita o [adaptador original](docs/original-adapter.md), validado com a distribuição
oficial V1.118 via Proton em um prefixo isolado. Expectativas locais continuam
separadas das capturas reais.

## Vetores fixos e constantes

`Global estados#[6]`, `Local nomes$[2]` e `Field filhos.Node[3]` aceitam
limites constantes inclusivos e acesso por colchetes. `Const` oferece valores
escalares tipados e expressões constantes. Veja o [contrato](docs/fixed-vectors.md)
e a [evolução da auditoria SCP](docs/scp-milestone8.md).

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/FixedVectors/main.bb -o artifacts/fixed-vectors.dll
dotnet artifacts/fixed-vectors.dll
```

O exemplo multifile imprime `42` e `3.5`, usando funções BASIC para acessar
vetores a partir de C# e preservando a ponte pública de tipos primitivos.

## Objetos legados

`Type / Field / End Type`, referências `.Tipo`, `New`, `Delete`, `Delete Each`,
`First / Last / Before / After`, `Insert` e `For Each` estão implementados.
O [contrato de objetos](docs/legacy-objects.md) descreve aliases, invalidação,
ordem das listas e limites de gerenciamento de memória.

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/objects.bb -o artifacts/objects.dll
dotnet artifacts/objects.dll
```

Imprime `42` e `1`, com C# chamando funções BASIC que manipulam a lista de objetos.

## Projetos com vários arquivos

`Include "arquivo.bb"` compartilha tipos, funções, globais, arrays e módulos C#
no mesmo programa e assembly. Todos os caminhos relativos usam a pasta do arquivo
principal, inclusive Includes em subpastas. Duplicatas e ciclos seguem as regras
verificadas no original; veja [compilação multifile](docs/multifile.md).

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/MultiFile/main.bb -o artifacts/multifile.dll
dotnet artifacts/multifile.dll
```

O exemplo imprime `42` com interoperabilidade nos dois sentidos. A
[auditoria do fonte oficial de SCP](docs/scp-compatibility.md) registra a cobertura
e os bloqueios reais; o jogo completo ainda não compila. Blitz3DCS será uma
integração que consome BlitzCS, sem dependências de engine no compilador.

## Expansão legada e funções externas

Parâmetros BASIC aceitam defaults constantes e omissão de argumentos finais.
Literais `$FF`, `$80000000` e `$FFFFFFFF` usam Integer com wrap em 32 bits.
Inicializadores de `Field` são aceitos; conforme Blitz3D V1.118, `New` mantém
campos em zero, string vazia ou Null, sem executar essas expressões.
Veja a [pesquisa de linguagem M9](docs/m9-language-research.md).

Um registro explícito liga nomes BASIC a métodos .NET públicos estáticos,
com assinaturas Integer/Float/String e chamadas diretas na execução. A
[biblioteca opcional de RuntimeError](docs/external-functions.md) demonstra
um serviço real fora do compilador; não há dependência do núcleo em Blitz3DCS.
A [auditoria SCP M9](docs/scp-milestone9.md) separa erros de linguagem,
símbolos nativos não registrados e dependências externas.

## Handles e fluxo Data

`Handle(objeto)` retorna um Integer estável; `Object.Tipo(numero)` recupera
referências tipadas ou Null. Delete invalida o número e remove o registro sem
reter objetos excluídos. Veja [handles gerenciados](docs/handles.md), o exemplo
multifile `samples/Hybrid/Handles/main.bb` e a [medição de memória](docs/handles-design.md).
`Abs` e `Sgn` preservam os tipos Integer/Float como operadores da linguagem original.
`Data`, `Read` e `Restore` oferecem um fluxo primitivo compartilhado, labels para
Restore e coerções históricas; [o contrato Data](docs/data-M10.md) registra limites.

A [auditoria M10](docs/scp-milestone10.md) inclui compilação integrada e profundidade
do arquivo do diagnóstico. SCP ainda não compila como projeto; não há renderer
nem comandos fictícios para mascarar dependências.

## Lifetime e memória gerenciada

Após `Delete`, os campos são limpos e a lista deixa de manter uma referência
forte ao objeto. Aliases ainda observáveis preservam identidade e posição;
objetos excluídos sem aliases podem ser coletados, e a limpeza incremental
remove posições vazias. Objetos vivos continuam pertencendo à lista até Delete.

O [projeto de lifetime](docs/object-lifetime-design.md) explica a referência fraca
restrita aos registros excluídos e seus trade-offs. O
[relatório de memória](docs/memory-methodology.md) separa contagens estruturais,
alocações, memória após coleta e memória comprometida pelo GC. O
[contrato de erros](docs/runtime-errors.md) mantém falhas de runtime não zero,
mesmo quando o original encerra com código zero.

## Limites

Suporta atribuições primitivas, `Global`, `Local`, funções tipadas com parâmetros
e `Return`, chamadas e blocos C#, identificadores case-insensitive com `%`
opcional e tags `#`/`$`, `Print` de texto ou número, `End`, comentários `;`, separadores `:`,
aritmética `+ - * /` e parênteses. Outras construções produzem diagnóstico.
Literais binários e hexadecimais preservam os 32 bits baixos. Literais decimais
positivos saturam em 2147483647 antes do menos unário, conforme V1.118;
`-2147483648` resulta em -2147483647. Veja a [pesquisa M11](docs/m11-language-research.md).
`Print` sem argumento ainda não é suportado.

O host desta fatia mapeia `Print` para o console para validar os valores calculados.
No Blitz3D original, `Print` desenha texto na superfície gráfica; equivalência
visual e semântica completa não são reivindicadas. Há comparações executáveis
limitadas com o original; não há renderização 3D. Interop direta de arrays e
objetos não integra a ponte pública `BB`. Fronteiras x87/CRT, encoding e
reutilização de objetos ainda precisam de cobertura diferencial ampliada.

Veja [arquitetura](docs/architecture.md), [pesquisa](docs/research.md) e
[compatibilidade](docs/compatibility.md).

## SCP e próximo runtime

A [auditoria M12](docs/scp-milestone12.md) distingue análise sintática,
resolução de símbolos e emissão. A árvore integrada de Main passa na sintaxe,
mas a compilação para em `FSOUND_Init` não registrado em FMod.bb:15; o assembly do jogo não foi emitido.
`blitzcs check-syntax projeto.bb` valida somente sintaxe e Includes.
A [arquitetura proposta do Blitz3DCS](docs/blitz3dcs-architecture.md) mantém
o compilador independente e planeja Vulkan 1.4, sem renderer implementado.
O [runtime inicial](docs/native-runtime-m12.md) implementa consultas reais de arquivos,
sistema e duas funções de strings através de um catálogo opt-in, com publicação
de dependências gerenciadas transitivas. O [catálogo de APIs SCP](docs/scp-native-catalog-m12.md)
separa frequências candidatas de chamadas semanticamente resolvidas.

## Trail-Code

O projeto foi inicializado com Trail-Code. `trail build` atualiza o mapa local,
`trail map` apresenta a estrutura e `trail ask "BasicCompiler" --source` recupera
os símbolos. O cache é ignorado pelo Git; decisões ficam em `.trail/learnings/`.
Se o comando não estiver no `PATH`, use a instalação existente:

```bash
node /var/home/marcelo/.local/share/dev-agent-tools/npm/lib/node_modules/@trailhq/trail-code/dist/bin/trail.js build
```

## Dependências e licenças

Implementação própria; nenhum código dos forks foi incorporado.
Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.0.0, MIT) é dependência do compilador
(para tokenizar os blocos C#) e da CLI (para emitir o assembly).
Os testes usam xUnit (Apache-2.0), runner Visual Studio (Apache-2.0) e Microsoft
Test SDK (MIT). A licença de distribuição do próprio BlitzCS ainda não foi
escolhida; nenhuma licença de código externo é implicitamente adotada.
