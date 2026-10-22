# Compilador e interoperabilidade inicial

Objetivo verificável: um arquivo BlitzBasic com sintaxe original do subconjunto
abaixo gera C# legível, um assembly .NET executável e símbolos de depuração.
Não é uma implementação completa do Milestone 1.

## Contratos

`BasicCompiler.Compile(source, sourcePath)` recebe texto e não grava arquivos:
lexer → parser → AST → análise semântica → IR tipada → C#. A análise das chamadas
C# consulta os metadados das referências .NET do host. Erros de entrada incluem
arquivo, linha e coluna. O código gerado usa `#line` para preservar a origem.

`DotNetEmitter.Emit(generatedCode, outputPath)` compila com Roslyn e escreve
`.dll`, `.pdb`, `.generated.cs` e `.runtimeconfig.json`. O assembly depende do
runtime .NET 10 instalado, sem dependência da CLI para executar. Nesta primeira
fatia as referências vêm do runtime que hospeda o compilador; cross-targeting e
reference assemblies são trabalho posterior.

A CLI aceita `build arquivo.bb -o saída.dll`. Ela não executa automaticamente o
programa do usuário. O assembly pode ser executado com `dotnet saída.dll`.

A interoperabilidade inicial vive no compilador: a análise resolve símbolos
primitivos Integer/Float/String, a emissão cria a ponte estática tipada `BB` e inclui definições C# e
instruções embutidas no mesmo código-fonte gerado. Roslyn tokeniza os blocos para
preservar literais/comentários e depois compila as duas linguagens já unificadas.
Cada bloco de instruções tem seu próprio escopo; somente globais e funções são
exportadas por `BB`. A resolução estática usa uma compilação de análise Roslyn
com assinaturas tipadas e overloads primitivos; a emissão materializa as
conversões escolhidas. Helpers de compatibilidade são incluídos no mesmo assembly
para casts e formatação. Veja [o contrato tipado](typed-interop.md).

Um ponto de entrada explícito `BlitzProgram` impede que métodos `Main` de módulos
C# tornem ambígua a entrada do programa. A API de emissão permite escolher esse
tipo opcionalmente; usos diretos que não o informam preservam a seleção de Roslyn.

Interop e runtime gráfico ainda não precisam de projetos adicionais. No futuro,
runtime preservará identidade de objetos e comandos legados, separado do backend
gráfico. Não há compromisso de API para funcionalidades não implementadas.

## Controle de fluxo e arrays

A AST e IR preservam blocos e tipos primitivos. Condições usam casts históricos;
For mantém o limite reavaliado e Step constante; Select usa seletor temporário e
um fluxo que permite Exit apontar ao laço envolvente. A emissão não pressupõe
escopo de variáveis C# para os blocos BASIC: locais são declarados no escopo da
função/programa. Retornos condicionais não suprimem os demais caminhos.

Arrays vivem em armazenamento gerado interno, separado de BB. O helper tipado
aplica limites inclusivos, descarte antes dos novos limites de Dim, defaults e
checks localizados. Funções BASIC acessam o mesmo armazenamento global. Essa
camada não altera a API escalar de interop.

O harness Python compara resultados locais com capturas de um adaptador original
opcional, registrando comparações pendentes sem fabricar capturas. Consulte
[legacy-language.md](legacy-language.md) e [differential.md](differential.md).

## Aceitação da primeira fatia

- `Print` de texto ou expressão inteira; atribuição inteira com `%` opcional;
  identificadores sem distinção entre maiúsculas e minúsculas; `End`;
  comentários `;`, separadores `:` e linhas LF/CRLF.
- Operadores `+ - * /`, sinais unários e parênteses; inteiros de 32 bits.
- Sintaxe não suportada falha explicitamente, sem tradução aproximada.
- Testes de componentes e integração executam o assembly em processo separado.
- Sem alegação de equivalência completa: validação diferencial depende do
  Blitz3D original, que não foi executado neste ambiente.

## Próximas tarefas do Milestone 1

1. Ampliar fixtures e executar comparação diferencial no original.
2. Validar fronteiras dos tipos `#` e `$` e implementar operadores restantes.
3. Ampliar construções legadas restantes, incluindo For Each, Goto/Gosub e Data.
4. Ampliar os escopos e arrays primitivos para objetos Type.
5. Ampliar os tipos legados e a extensão híbrida formalizada
   em `hybrid.md` com conversões e ciclo de vida definidos para esses tipos.

Cada tarefa deve preservar os contratos da fatia anterior e deixar build e testes
passando. Callbacks por delegates tipados já são exercitados entre C# e funções
BASIC; um sistema de eventos de entidades, backend 3D e AOT continuam fora desta
fatia.

## Objetos gerenciados e execução diferencial

`ValueType` distingue os primitivos de tipos nominais de objeto. O binder resolve
os tipos antes dos campos, rejeita misturas nominais e produz operações tipadas
na IR. O emitter gera classes gerenciadas e listas por tipo; `ObjectRuntime`
centraliza invalidação, igualdade e iteração. A ponte pública de interoperabilidade
continua primitiva; fachadas BASIC manipulam objetos no mesmo assembly Roslyn.
No Milestone 6, identidades e registros de posição foram separados: a lista
mantém objetos vivos fortemente, objetos excluídos fracamente e remove posições
sem identidade observável em limpeza incremental. Isso substitui a retenção
permanente anterior. `object-lifetime-design.md` justifica a referência fraca
sem refcount manual nem finalizers; `memory-methodology.md` mede o resultado.

O harness chama um adaptador externo por processo. O adaptador original executa
binários oficiais via Proton, instrumentando apenas saída e término normal; erros
de infraestrutura não viram resultados da linguagem. A pesquisa de fonte e a
versão binária são registradas separadamente em `original-adapter.md`.
As seções de aceitação anteriores representam checkpoints históricos.

A conversão Float → String mantém o ponto decimal da notação científica MSVC
(`1.e-004`) tanto no runtime quanto no folding de constantes; uma comparação
executável revelou a diferença anterior. Não se usa essa correção pontual para
inferir equivalência de todas as conversões CRT.

## Multifile e integrações independentes

Cada compilação possui um IncludeResolver com base na pasta do arquivo principal
e um conjunto de includes normalizados. Parsers filhos herdam o escopo e o
registro de nomes de arrays; cada arquivo é uma unidade sintática completa.
O stream ordenado de statements forma uma única IR e emissão Roslyn.
SourcePosition transporta arquivo/linha/coluna até diagnósticos e #line.
Não há mutação do CWD global nem dependência de renderer, áudio ou física.

A direção de dependências é **Blitz3DCS consome BlitzCS**. BlitzCS é a
infraestrutura de linguagem; integrações independentes com engines fornecem
APIs específicas. A auditoria SCP não adiciona símbolos fictícios de engine ao
compilador. Data/Read/Restore, labels e Gosub permanecem trabalho de linguagem
posterior; Include não pressupõe sua implementação.

## Vetores fixos e constantes

ValueType distingue vetores de escalares e mantém tipo de elemento e comprimento.
A análise resolve Const antes dos protótipos de campos e valida limites constantes;
a IR recebe literais dobrados, acessos e atribuições de elementos tipados.
O emissor inclui __BlitzVector<T> somente quando necessário e usa retorno por
referência para avaliar cada índice uma vez. Globais são inicializadas antes
do corpo principal; locais na entrada do escopo e campos em New. Delete limpa
referências a vetores sem excluir recursivamente seus objetos. A ponte pública
continua limitada aos tipos primitivos; funções BASIC encapsulam os acessos.
Nenhuma dependência de engine foi adicionada.

## Registro externo (M9)

ExternalFunctionRegistry é imutável e pertence à compilação. ExternalFunction
valida MethodInfo e deriva assinatura primitiva e alvo estático; ExternalBinding
produz IrCall direto com argumentos convertidos pela semântica BASIC. O registro
fornece referências para o probe CSharpBridge e para Roslyn no host. A CLI carrega
um catálogo opt-in; o compilador não carrega nem referencia LegacyRuntime ou
Blitz3DCS. Decisão e limites em [external-functions-design.md](external-functions-design.md).
Defaults de Function são dobrados e expandidos na chamada, preservando ABI fixa BB.
O lexer acumula hexadecimais com wrap Int32; Field inicializers são analisados e
validados, mas a emissão preserva alocação zerada comprovada na V1.118.

## Milestone 10: identidade e dados

Handle/Object.Tipo são operações tipadas da IR. ObjectRuntime emite uma tabela
Integer→objeto vivo, compartilhada pelos tipos do programa; o objeto mantém seu
ID atribuído sob demanda. Delete remove o registro antes de limpar os campos e
preservar a posição M6. Não há ponteiro, novo weak map nem refcount.

Abs/Sgn são operadores unários polimórficos no parser original. A IR preserva
o tipo, dobra constantes e usa helpers de execução por tipo. O caso de -0 Float
exige separar folding de execução, conforme capturas V1.118. Funções comuns de
math/engine continuam extensões por registro externo; não foram inseridas como
comandos artificiais na gramática.

IrProgram transporta Data tipado e UsesData; CollectData resolve labels e
expressões constantes após Const, na ordem achatada dos Includes. DataRuntime
é emitido somente quando utilizado, com cursor estático e reads tipados. Float
Read para Integer trunca (contrato original de Read), diferente da conversão
Float→Integer comum que arredonda. Labels desse contrato não habilitam Goto/Gosub.
