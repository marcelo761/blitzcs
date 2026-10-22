# Lifetime gerenciado — proposta e contrato

## Evidência e problema

No fonte original (`bbruntime/basic.cpp`, commit
`6beb288cb5962393684a59a4a44ac11524894939`), New adiciona um objeto à lista usada
com referência de propriedade. Delete limpa campos, marca `fields=0` e libera
essa referência. Aliases retêm o invólucro na lista; somente `_bbObjRelease`
com contador zero remove sua posição. Before/After saltam campos nulos.
Insert religa posições, incluindo a localização dos invólucros excluídos ainda
referenciados. Delete Each salva o próximo antes da exclusão. Strings, campos
objeto e vetores são liberados; excluir o pai não exclui um filho vivo.

Na BlitzCS anterior, a raiz estática da lista alcançava diretamente cada objeto
excluído por Previous/Next. O GC não podia recuperá-los. Limpar apenas campos
não resolve essa raiz.

## Alternativas avaliadas

1. Remover imediatamente e guardar vizinhos no alias: simples, mas incorreto
   quando vizinhos são movidos e novas posições são inseridas.
2. Reproduzir refcount em todo armazenamento BASIC: exigiria alterar variáveis,
   parâmetros, arrays, campos, retornos e fronteiras híbridas. Complexidade e
   riscos de ciclos/temporários desnecessários para um runtime gerenciado.
3. Separar identidade e posição, com referência fraca somente após exclusão:
   preserva a ordem enquanto o alias é observável e deixa o GC decidir quando
   essa identidade já não existe no programa. Escolhida, sem finalizers.

## Estratégia

Cada objeto aponta para seu registro de posição. A lista por tipo mantém os
registros em ordem. Um registro mantém seu objeto fortemente enquanto vivo;
após Delete, limpa campos e troca essa ligação por WeakReference. Registros
não guardam referências fortes aos objetos excluídos, nem aos seus campos.
Aliases, arrays e campos BASIC continuam sendo referências gerenciadas fortes.

Uma posição excluída só pode ser removida quando TryGetTarget comprova que o
objeto já foi coletado. Antes disso, deve participar das mudanças de ordem.
As operações fazem limpeza incremental com orçamento fixo; a travessia também
remove posições vazias que encontra. Limpeza não depende de finalizers, timers
ou refcount. Após uma coleta sem atividade subsequente, registros vazios podem
permanecer até a próxima operação/varredura; não mantêm objetos BASIC vivos.
A instrumentação de memória distingue esses registros dos objetos de linguagem.

New, Delete e Insert têm trabalho constante mais um orçamento constante de
limpeza. First/Last/Before/After e iteração custam os registros percorridos;
aliases excluídos ainda observáveis podem impor saltos historicamente necessários.
Uma varredura completa de diagnóstico é linear e não ocorre implicitamente em
cada chamada.

## Critérios de teste

- Comparar no original navegação após exclusões e movimentos de vizinhos.
- Preservar Delete Null, exclusão repetida, invalidação e exclusão em For Each.
- Provar estruturalmente ausência de caminhos fortes da lista a objetos mortos.
- Forçar coleta em testes isolados sem limites de tempo/RSS; comprovar que um
  alias guardado continua utilizável e objetos sem aliases são coletáveis.
- Exercitar 100.000+ operações, ciclos, campos e arrays; medir alocações, memória
  após coleta e memória comprometida pelo GC separadamente.
- Comparar antes/depois com o mesmo probe, sem assertions de bytes ou timing.

Esta proposta não afirma equivalência de reutilização de endereços nativos,
parâmetros emprestados adversariais, handles ou finalização externa. Objetos vivos
continuam pertencendo à lista do tipo até Delete, mesmo sem variável apontando
para eles: coletá-los violaria First/For Each.
