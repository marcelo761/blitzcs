# Objetos legados

O sistema usa tipos nominais declarados por `Type / Field / End Type`, com campos
Integer, Float, String ou referências `.NomeDoTipo`. Campos aceitam referências
próprias, cruzadas e a tipos declarados posteriormente. Valores iniciais são
`0`, `0.0`, String vazia e `Null`. Acesso a campos usa `objeto\campo`.

`New Tipo` adiciona ao fim da lista daquele tipo. `First`, `Last`, `Before` e
`After` percorrem essa ordem; as extremidades retornam `Null`. `Insert objeto
Before/After outro` muda a ordem entre objetos do mesmo tipo. `For objeto.Tipo =
Each Tipo` acompanha a lista durante a execução, permitindo exclusão do atual,
exclusão dos seguintes e inclusão no fim. Alterar a variável de iteração altera
qual objeto será usado para encontrar o próximo.

`Delete` invalida a identidade e limpa seus campos; não apaga automaticamente
objetos referenciados por esses campos. Aliases do objeto excluído comparam iguais
a `Null`. Exclusões repetidas e `Delete Null` não fazem nada. `Delete Each Tipo`
exclui todos os objetos vivos daquele tipo. Navegação a partir de alias excluído
continua usando sua posição na lista, saltando objetos excluídos. Acesso a campos
de objetos excluídos ou `Null` falha com localização no `.bb`.

Referências podem estar em variáveis, parâmetros, retornos, campos e arrays.
Comparações `=`/`<>` exigem tipos nominais compatíveis ou `Null`; aritmética,
comparações ordenadas, condições numéricas implícitas e seletores de `Select`
com objetos produzem diagnósticos de compilação.

## Integração híbrida

`<?cs ?>`, módulos, `CS::` e a ponte primitiva `BB.` permanecem no mesmo assembly
Roslyn. Funções BASIC com assinaturas primitivas podem manipular os objetos e ser
chamadas por C#. `samples/Hybrid/objects.bb` demonstra essa fachada e imprime
`42` e `1`. Referências BASIC não são expostas como ponteiros nativos; a ponte
pública suportada continua sendo Integer/Float/String.

## Limites e diferenças

O runtime mantém registros de posição separados dos objetos. Posições vivas
possuem o objeto; após Delete, guardam apenas uma referência fraca a ele. Aliases,
campos e arrays BASIC continuam fortes. Quando o objeto já foi coletado, a posição
pode ser removida sem apagar informação observável. A limpeza é incremental
(dois registros por operação) e também ocorre durante travessias.

O GC recupera objetos excluídos sem aliases, inclusive ciclos cujos campos foram
limpos. Uma coleta sem atividade subsequente pode deixar registros de posição
vazios até a próxima limpeza; esses registros não mantêm objetos BASIC vivos.
Aliases ainda observáveis exigem preservar posição e identidade excluída. Objetos
vivos pertencem à lista até Delete mesmo sem variável apontando para eles.
Consulte [o projeto de lifetime](object-lifetime-design.md) e
[a metodologia de memória](memory-methodology.md). Não se reproduz a reutilização
de endereços ou o refcount nativo.

Vetores de campo `Field valores[10]` estão implementados; veja
[vetores fixos](fixed-vectors.md). Conversões `Object.Tipo` e handles inteiros estão implementados no M10; veja
[o contrato de handles](handles.md). A tabela registra somente objetos vivos e
remove os identificadores em Delete. Não se afirma equivalência em sequências adversariais
com parâmetros emprestados, reutilização de nós ou aliases cíclicos do runtime
nativo. Guards gerenciados de campo e inserção são mais seguros que acessos
nativos sem checks em release. Não há renderização 3D.

A pesquisa anterior à implementação está em [legacy-object-research.md](legacy-object-research.md).
O manifesto `tests/Compatibility/objects.json` separa expectativas locais de
capturas executadas pelo adaptador original; consulte [original-adapter.md](original-adapter.md)
para distribuição, instrumentação e limitações da comparação real.
