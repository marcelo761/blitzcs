# BlitzBasic Include e construções adiadas

## Evidência e escopo

A semântica de `Include` foi estudada no código-fonte do Blitz3D em
`/tmp/blitz-research/blitz3d`, commit `6beb288cb5962393684a59a4a44ac11524894939`,
e executada com o compilador oficial da distribuição Blitz3D V1.118 sob Proton.
A execução usa o adaptador `tools/original_blitz3d_adapter.py`: ele copia somente a
árvore transitiva de includes, preserva os comandos `Include` e as expressões, e
instrumenta `Print` para gravar em arquivo. Assim, o compilador e o runtime originais
continuam processando a árvore BlitzBasic; os arquivos de saída não dependem de OCR.

## Semântica observada de Include

O driver CLI (`blitz/main.cpp:213-217`) define o diretório de trabalho para o diretório
do arquivo principal antes de iniciar o parser. O parser (`compiler/parser.cpp:93-121`)
resolve cada caminho com `GetFullPathName`, portanto todos os caminhos relativos,
inclusive os escritos em arquivos incluídos, são relativos ao diretório do arquivo
principal. Eles não são relativos ao diretório do arquivo que contém a instrução.

O caminho completo é convertido para minúsculas e comparado em um conjunto do parser
(`compiler/parser.h:23`). O parser registra um arquivo antes de processá-lo. Uma segunda
referência ao mesmo caminho normalizado é ignorada, e ciclos entre arquivos já incluídos
terminam silenciosamente. A árvore importada é processada no ponto da instrução e no
escopo atual. O parser troca o arquivo de origem durante a leitura, e `IncludeNode`
preserva essa identidade para labels de depuração (`compiler/stmtnode.cpp:56-74`). Um
include inexistente reporta o nome do arquivo que contém a instrução, a posição depois
do nome do include e `Unable to open include file`.

O arquivo principal não é inserido inicialmente no conjunto `included`. Se um arquivo
incluído voltar a incluir o arquivo principal, o original processa o principal novamente
uma vez. Ciclos envolvendo apenas arquivos incluídos são quebrados pelo conjunto global.
Isso é comportamento observado, não uma recomendação: código que inclui o principal
pode repetir declarações e falhar com `Duplicate variable name`.

## Capturas diferenciais executadas

As cinco fixtures em `tests/Compatibility/multifile.json` foram capturadas com o
adaptador real e o compilador V1.118. Resultado original verificado:

| Fixture | Resultado original |
| --- | --- |
| `include-main-directory-and-deduplication` | `root-before`, `child`, `nested-root-relative`, `root-after`; o alias `child/../child/one.bb` não executa duas vezes |
| `include-cycle-between-included-files` | `one-before`, `two-before`, `two-after`, `one-after`; ciclo encerrado sem erro |
| `include-cycle-through-main-file` | arquivo principal executado duas vezes na ordem capturada, confirmando que ele não é inicialmente marcado como incluído |
| `include-missing-nested-source-location` | fase `compile_error`, exit code original `255`; a saída JSON do adaptador omite o texto de diagnóstico, mas a execução manual confirmou arquivo incluído e linha como local da mensagem |
| `include-inside-function-early-return` | `7`, `42`; `Return 42` no arquivo filho sai da função e evita `Print 99` no arquivo principal |

Saídas completas capturadas e esperadas ficam no manifesto e nos arquivos `.bb`. A
captura real valida apenas o compilador V1.118 no ambiente Proton usado; não prova
compatibilidade total com todas as versões do Blitz3D.

Na validação Release final, as cinco fixtures deste manifesto passaram localmente e
coincidiram com a captura original (**5/5**). O relatório por fixture está em
[`artifacts/multifile-differential.json`](../artifacts/multifile-differential.json).
Os outros manifestos, executados em série na mesma sessão, preservaram os resultados
anteriores: fixtures gerais **14/15** (divergência conhecida em `upper-bound-error`),
objetos **3/3** e lifetime **10/10**. No total, houve **32/33** coincidências reais e
**33/33** aprovações locais; nenhum caso ficou pendente ou teve erro de adaptador. Os
relatórios estão em `artifacts/original-differential.json`,
`artifacts/original-object-differential.json` e `artifacts/lifetime-differential.json`.

O adaptador instrumenta apenas o arquivo principal com o contador de conclusão. Os
arquivos incluídos mantêm o fluxo de controle original; isso é necessário porque um
`Return` dentro de um include pode sair da função antes de executar comandos inseridos
ao fim do arquivo filho.

## Referência para trabalho futuro

`Data` só é aceito no programa principal (`compiler/parser.cpp:300-306`), e cada item
precisa ser uma expressão constante (`compiler/declnode.cpp:210-218`). O executável
armazena itens tipados em `__DATA`: tags 1, 2 e 4 representam Integer, Float e String,
seguidos pelo terminador (declnode.cpp:220-236; prognode.cpp:132-134). `Read` converte
entre esses tipos: número para Integer trunca segundo a conversão C++; texto usa `atoi`
ou `atof`; leitura após o terminador gera `Out of data` (`bbruntime/basic.cpp:448-475`).
`Restore` sem label move o cursor para o início; com label usa a quantidade de itens
`Data` anteriores à label (parser.cpp:296-300; stmtnode.cpp:177-193; LabelNode recebe
`datas->size()` em parser.cpp:353).

`Goto` e `Gosub` criam referências de label resolvidas durante a análise semântica;
labels não definidos são reportados ao final (stmtnode.cpp:196-220; prognode.cpp).
`Gosub` só é aceito no nível principal. `Return` sem expressão no programa principal
retorna de um `Gosub`; dentro de função `Return` devolve o valor, ou o valor padrão
quando omitido. `Return` com valor no programa principal produz `Main program cannot
return a value` (parser.cpp:262-265; stmtnode.cpp:406-439). Esses pontos foram
confirmados na fonte, mas ainda não têm fixtures de comparação diferencial nesta
etapa.
