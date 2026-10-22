# Compilação multifile

`Include "arquivo.bb"` processa o arquivo na posição da diretiva e no escopo
corrente. Os arquivos formam um único programa BASIC e um único assembly .NET,
com as mesmas tabelas de tipos, globais, funções e arrays. Os módulos C# também
compartilham esse assembly; `CS::` e `BB.` permanecem disponíveis.

```bash
dotnet run --project src/BlitzCS.CLI -- build samples/Hybrid/MultiFile/main.bb -o artifacts/multifile.dll
dotnet artifacts/multifile.dll
```

O exemplo tem quatro arquivos e imprime `42`: BASIC chama C#, C# chama uma
função BASIC incluída, e um bloco C# modifica a global compartilhada.

## Semântica histórica verificada

A CLI original muda CWD para a pasta do arquivo principal antes de chamar o
parser. Assim, **todos os caminhos relativos usam a pasta principal**, inclusive
Includes encontrados em subpastas. Não usam a pasta do arquivo que os contém.
A BlitzCS conserva essa base em um contexto por compilação, sem mudar o CWD
global do host. `Compile(source, sourcePath)` usa a pasta de `sourcePath` como
base; os arquivos incluídos são lidos do filesystem durante a compilação.

`/` e `\` são aceitos como separadores. A identidade de includes usa caminho
absoluto normalizado e comparação sem distinção de caixa, como o original
Windows. Em hosts sensíveis a caixa, o resolvedor procura componentes ignorando
caixa quando necessário; uma busca ambígua gera diagnóstico. Caminhos absolutos
seguem as regras do host: não há emulação de letras de unidade Windows no Linux.
Links simbólicos não são convertidos em identidade por inode.

A primeira inclusão registra o caminho antes de processar seu conteúdo. Includes
repetidos e ciclos entre arquivos já incluídos são ignorados. O arquivo principal
**não** começa nesse conjunto: um ciclo que volta ao principal pode processá-lo
uma segunda vez, inclusive gerando erros históricos de declaração duplicada.
O compilador não transforma isso em erro genérico de ciclo nem em include-once
implícito para o principal.

Cada include é analisado separadamente no escopo herdado. Um bloco não pode
começar num arquivo e terminar no pai; Function e Type mantêm suas restrições de
escopo. Não se faz concatenação textual que permita atravessar essas fronteiras.
Strings, comentários e blocos C# são opacos a diretivas BASIC.

## Diagnósticos e arquitetura

Posições de origem carregam arquivo, linha e coluna desde o lexer até AST/IR.
Erros de léxico, parser, tipos, funções e resolução `CS::` apontam ao arquivo
original. A emissão usa `#line` por origem para erros Roslyn, PDB e guards de
runtime, incluindo erros em módulos C# incluídos.

A mudança fica na infraestrutura de linguagem BlitzCS. Nenhuma biblioteca de
renderização, áudio, física ou engine foi adicionada. **Blitz3DCS consumirá
BlitzCS**, fornecendo integração/runtime específico; o compilador não depende
da futura integração. O compilador não interpreta comandos de engine como
substitutos silenciosos nem injeta stubs para aparentar compatibilidade.

## Escopo entregue e trabalho restante

Este milestone conclui Include e infraestrutura multifile, com regressões e
comparações reais. A [auditoria SCP](scp-compatibility.md) identifica as próximas
capacidades necessárias. Data/Read/Restore, labels, Goto e Gosub continuam
pendentes: as regras consultadas estão em [include-research.md](include-research.md).
O Return atualmente implementado é o de funções BASIC. Não é tratado como Return
de uma pilha Gosub inexistente. Esses recursos não foram implementados a partir
de suposições nem reclamados como diferencialmente validados.

O adaptador nativo preserva Include e a árvore de arquivos, sem pré-expandir o
fonte do original; suas restrições de staging são diferentes das capacidades do
compilador e estão em [original-adapter.md](original-adapter.md). A distribuição
SCP auditada permanece intacta fora do repositório. Não se afirma compilação
integral do jogo nem compatibilidade completa com BlitzBasic.
