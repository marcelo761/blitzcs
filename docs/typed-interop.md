# Typed Interoperability & Semantic Compatibility

Tipos são preservados nos identificadores, AST, IR, ponte `BB` e chamadas C#.
As duas linguagens geram um único assembly Roslyn, sem reflexão ou resolução
de overloads por chamada durante a execução.

| BASIC | .NET / C# | Inicialização |
|---|---|---|
| Integer `%` | `System.Int32` / `int` | `0` |
| Float `#` | `System.Single` / `float` | `0.0` |
| String `$` | `System.String` / `string` | `""` |

Declarações sem sufixo são inteiras. Referências sem sufixo usam o tipo declarado;
sufixos explícitos incompatíveis geram diagnóstico. Um sufixo na chamada BASIC
verifica o retorno, sem convertê-lo. Funções sem `Return` retornam o valor inicial
do seu tipo. Locais, parâmetros e globais preservam seus escopos anteriores.

## Conversões e operações

As regras foram verificadas no código original, conforme
[typed-semantics-research.md](typed-semantics-research.md). Não são evidência de
equivalência diferencial executável.

- Atribuições, argumentos e retornos BASIC permitem conversão entre os três
  primitivos. `Int`, `Float` e `Str` usam os mesmos helpers de compatibilidade.
  Casts aceitam um operando unário com ou sem parênteses, incluindo os sufixos
  opcionais correspondentes: `Int 3.5`, `Int%(3.5)`.
- Integer com Integer mantém aritmética inteira; Integer com Float promove para
  Single. `+` com String formata os operandos e concatena; outros operadores
  aritméticos com String são erros.
- Divisão inteira trunca rumo a zero. Float→Integer usa o inteiro mais próximo,
  com empates para par: `Int(2.5)=2`, `Int(3.5)=4`. Um cast C# truncante não
  implementaria essa política observada no caminho de constantes original.
- Parsing reconhece prefixos decimais com sinal/espaços. `Int(" -42tail")=-42`,
  `Int("1e2")=1`; ausência de prefixo produz zero. Float aceita ponto e expoente.
- Float é formatado com seis dígitos significativos e `.0` para valores inteiros;
  expoentes são usados fora da faixa fixa. Parsing/formatação são invariantes à
  cultura .NET corrente. Não se usa `ToString()` cultural como tradução legada.
- String lida pelo BASIC normaliza `null` fornecido por C# para vazia.

## Ponte e resolução estática

`Global fraction#=1.5` produz `BB.fraction` de tipo `float`; `Global message$`
produz `BB.message` de tipo `string`. Funções preservam parâmetros e retornos:
`Function Scale#(v#,factor#)` exporta `BB.scale(float,float)` retornando `float`.
Globais usam os mesmos campos estáticos, sem serialização nem cópia de estado.
C# usa nomes normalizados para minúsculas, com escape `@` quando necessário.

Chamadas de C# para `BB` seguem a tipagem C#: a conversão histórica acontece em
atribuições, parâmetros e retornos controlados pelo compilador BASIC. Não se
reescrevem as operações do próprio código C# embutido.

`CS::Namespace.Type.Method(...)` resolve símbolos em uma compilação de análise
com os módulos C# e assinaturas tipadas de `BB`. Os módulos não são executados.
Usa-se o modelo semântico [GetSymbolInfo](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.csharp.csharpextensions.getsymbolinfo?view=roslyn-dotnet-5.0.0)
e o grupo de métodos Roslyn. Na emissão final, são chamadas diretas.

Só participam assinaturas acessíveis, estáticas, não genéricas, de aridade fixa,
com parâmetros Integer/Float/String. Correspondência exata é preferida;
Integer→Float é a única promoção implícita em `CS::`. Outras conversões exigem
`Int`, `Float` ou `Str` antes da chamada. As conversões escolhidas são emitidas
explicitamente, evitando nova seleção de uma assinatura diferente pelo C#.
Alternativas sem uma melhor opção geram diagnóstico de ambiguidade.

Retornos são Integer/Float/String. O alargamento seguro de `byte`, `sbyte`,
`short`, `ushort` e `char` para Integer permanece disponível da ponte anterior.
Assinaturas .NET não suportadas não ocultam alternativas primitivas válidas.
Métodos ausentes, tipos inválidos e ambiguidades apontam para o `.bb`;
erros nos corpos C# usam `#line` na compilação final.

## Demonstração

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/typed.bb -o artifacts/typed.dll
dotnet artifacts/typed.dll
```

O exemplo troca os três tipos nos dois sentidos e altera as três globais em C#:

```text
42
3.5
[Blitz] C#
```

## Limites conhecidos

- Sem comparação executável com o original. Precisão intermediária/arredondamento
  x87 pode diferir da política Single/empates-para-par testada localmente.
- Float→Integer não finito/fora de faixa usa `int.MinValue`; overflow de prefixo
  inteiro usa módulo 32 bits. Essas políticas não são equivalência universal com
  C/MSVC em casos indefinidos ou dependentes do ambiente.
- Expoentes usam `e±ddd`. Formatação de fronteira, denormais, NaN, infinitos,
  locale C e encoding de `BBStr` precisam de validação diferencial. Strings .NET
  são UTF-16; não há garantia de equivalência de bytes com strings legadas.
- O folding dos primitivos suportados agora reconhece conversões literais:
  `4 / Int("none")` produz diagnóstico de divisão por zero na compilação. Isso
  corrige a limitação registrada na primeira entrega tipada; fronteiras x87/CRT
  ainda aguardam comparação executável.
- Não há `ref`, `out`, `params`, genéricos, opcionais, `void`, retorno
  `double`/objetos ou importação configurável de assemblies externos em `CS::`.
- Estruturas de controle e arrays primitivos são tratados no contrato de
  [linguagem legada](legacy-language.md). API completa de strings, renderização e
  shutdown legado completo continuam fora do subconjunto.

Próximo passo: executar fixtures no compilador/runtime original e comparar casts,
formatação, fronteiras numéricas e diagnóstico de constantes.


## Evidência diferencial posterior

O milestone de objetos passou a executar a distribuição oficial Blitz3D V1.118
via Proton; consulte `original-adapter.md` e `validation.md`. A fixture de
formatação revelou que o original preserva o ponto em `1.e-004`; runtime e
folding de constantes foram corrigidos com esse dado. Esse resultado limitado
não resolve todas as fronteiras x87/CRT, valores não finitos ou encoding.
