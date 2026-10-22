# Handles gerenciados — decisão Milestone 10

A fonte Blitz3D stock usa dois mapas, atribui identificadores Integer monotônicos
somente no primeiro Handle e remove ambos os registros em Delete. O endereço
nativo do objeto não é o identificador. Object.Tipo retorna Null para identificador
inexistente ou tipo nominal diferente. A captura V1.118 confirma estabilidade,
zero, exclusão e não reutilização de identificadores nas operações exercitadas.

## Representação escolhida

Cada objeto guarda um Integer inicialmente zero. Uma tabela por programa associa
identificador a objeto vivo; Handle atribui somente quando necessário. A lista de
tipos já mantém objetos vivos enraizados até Delete. A tabela não muda esse
lifetime: Delete remove o registro imediatamente, limpa campos e mantém os
metadados de navegação existentes somente pelos mecanismos M6 já testados.
Integer guardado em variável/array/campo não é uma referência forte ao objeto.

Object.Tipo consulta a tabela e retorna a referência apenas para o tipo exato.
Null e aliases excluídos têm Handle zero. Identificadores removidos não voltam a
resolver, mesmo depois de New; não há reciclagem de números. Se Int32 positivo
esgotar, falha com diagnóstico gerenciado em vez de wrap/reutilização silenciosa.
Esse limite é divergência deliberada do overflow nativo sem contrato seguro.

Não há ponteiros, refcount manual, finalizers ou novas weak references. Alternativa
com weak map seria redundante, pois objetos vivos pertencem à lista; alternativa
com tabela de tombstones reteria metadados sem necessidade observável. O mapa
contém somente handles vivos e mantém busca/remoção O(1) amortizado. Sua capacidade
reservada pode acompanhar o pico de handles simultâneos, não todos os New/Delete.
Acesso concorrente não é contrato do runtime BASIC sequencial nesta etapa.

## Validação necessária

Casos positivos/negativos de tipagem, conversões Integer conforme Blitz, aliases,
campos/arrays/vetores, Delete Each, lookup de handle inválido e tipo errado.
Estresse com 100 mil operações, contagem determinística de entradas vivas e teste
GC separado com alias liberado, mantendo a tabela estática enraizada. Registrar
alocações, memória viva após coleta e memória comprometida; RSS não prova coleta.
Comparações reais com V1.118, sem inferir equivalência de todo o sistema de objetos.

## Checkpoint verificado

Antes da fatia Data, passaram 497 testes .NET (263 compiler, 234 CLI) e 31 Python.
O teste estrutural de 100 mil ciclos deixou zero handles vivos; o teste de GC
coletou um objeto excluído e autorreferenciado, mantendo a assembly/tabela estática
vivas e o handle Integer ainda guardado. O teste de exaustão injetou o limite por
reflexão somente no teste e confirmou erro antes de registrar/reutilizar um ID.

`handle-memory.bb` aquece 1.000 ciclos e mede duas etapas de 100.000. Na medição
inicial, cada etapa alocou 11.200.448 bytes na thread; memória viva após coleta
foi 59.808 e 61.416 bytes, com 10.575.872 bytes comprometidos pelo GC. A tabela
terminou com zero entradas, capacidade3 e zero registros de posição após limpeza.
Esses valores são observações do ambiente, não thresholds de testes. O teste
não considera memória comprometida ou RSS como vazamento. A estimativa recuperada
antes/depois da coleta inclui ruído do host e não é contagem exata de objetos.
O JSON reproduzível fica em `artifacts/handle-memory.json`.
