# Interoperabilidade híbrida inicial

Contrato desta entrega: um `.bb` pode conter BASIC e blocos C# compilados
uma única vez, no mesmo assembly .NET. Não há intérprete C# nem reflexão por
chamada. O programa continua usando o host de console desta fase.

## Estado e chamadas

```text
Global answer%=0

<?cs module
public static class MathBridge
{
    public static int Compute() => BB.add(19, 22);
}
?>

answer = CS::MathBridge.Compute()
<?cs
BB.answer += 1;
?>
Print answer
End

Function Add%(left%, right%)
    Return left + right
End Function
```

A saída deve ser `42`: BASIC chama `MathBridge.Compute`, C# chama a função BASIC
`add`, o resultado retorna a BASIC, um bloco C# modifica a global e BASIC lê o
mesmo estado. O exemplo completo fica em `samples/Hybrid/answer.bb`.

`BB` é a ponte gerada: globais são campos estáticos tipados e funções são métodos
estáticos com parâmetros e retorno `int`, `float` ou `string`. C# usa os nomes BASIC normalizados para
minúsculas, sem `%`, `#` ou `$`; nomes que sejam palavras reservadas C# usam `@`, como
`BB.@class`. BASIC permanece case-insensitive; C# mantém suas regras de case.
Não há cópia de estado entre as linguagens.

`<?cs ... ?>` contém instruções no ponto em que o programa as alcança, dentro de
um escopo C# próprio. `<?cs module ... ?>` contém definições no nível da unidade
de compilação; não é um bloco executado na ordem das instruções. Use classes ou
namespaces com chaves para agrupar definições. A ponte `BB` e a classe de entrada
do programa são nomes reservados à geração.

`CS::Namespace.Type.Method(...)` resolve métodos estáticos via Roslyn, com os
argumentos primitivos BASIC. Retornos Integer, Float e String são suportados;
`double`, `void` e conversões estreitas implícitas são erros. Métodos e
overloads C# continuam case-sensitive. Esta extensão é um contrato inicial do
BlitzCS, não sintaxe histórica do BlitzBasic.

## Escopos e tipos

- `Global` declara estado compartilhado no programa principal. Sua inicialização
  acontece na posição da declaração; valores iniciais são zero numérico ou String vazia.
- `Local` e variáveis implícitas pertencem ao programa principal ou à função que
  as contém. Funções não capturam locais do programa principal.
- Parâmetros são passados por valor. Locais podem sombrear globais; não são
  exportados pela ponte `BB`.
- O inicializador é resolvido antes da nova declaração: em uma função,
  `Local total%=total` lê uma global `total` já visível antes de sombreá-la.
- Funções são resolvidas antes das instruções, permitindo chamadas a definições
  posteriores. Funções Integer, Float e String são suportadas.
- Atribuições C# aos campos tipados são verificadas por Roslyn; não há `dynamic`,
  dicionário de objetos ou conversão textual usados pela ponte.

Variáveis `$`, valores `#` e conversões primitivas estão detalhados no
[contrato tipado](typed-interop.md). Arrays e objetos aguardam contratos próprios.
Globais e funções não podem compartilhar o mesmo nome na ponte. Não há suporte
a parâmetros opcionais/default, passagem por referência ou importação configurável
de assemblies externos. Os tipos C# disponíveis vêm das referências do host.
Funções sem `Return` retornam o valor inicial do tipo. `End` dentro de uma função termina o processo
com código zero; a limpeza completa do runtime legado ainda não foi implementada.

## Delimitadores e diagnósticos

O fechamento `?>` só termina um bloco fora de literais e comentários C#.
Literais comuns, verbatim, interpolados e raw precisam preservar seu conteúdo.
O lexer não deve interpretar marcadores dentro de strings ou comentários BASIC.

Erros BASIC indicam o `.bb` e sua posição; erros C# usam mapeamentos `#line` para
o mesmo arquivo e linha. Blocos incompletos são erros, sem geração parcial do
assembly. C# embutido é código do próprio programa, com as permissões normais
desse programa; não há sandbox de execução.

## Critérios de aceitação

Testes de componentes devem cobrir escopos, resolução de funções, declarações
inválidas e captura de blocos. Integração deve executar o exemplo `42`, verificar
estado compartilhado nos dois sentidos e rejeitar tipos incompatíveis com
diagnósticos no `.bb`. Todos os testes e o exemplo legado devem continuar passando.
Compatibilidade diferencial com o Blitz3D original não é alegada.

## Evidência da primeira entrega inteira

`scripts/verify.sh` passou em Release com 84 testes (49 do compilador e 35 de
emissão/CLI), sem avisos ou erros de build. O exemplo híbrido gerou
`artifacts/answer.dll` e imprimiu `42`; o exemplo legado preservou sua saída.

A ampliação atual é descrita em [typed-interop.md](typed-interop.md); os totais
de teste atualizados ficam em [validation.md](validation.md).
