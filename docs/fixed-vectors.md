# Vetores fixos e Const

Milestone 8 implementa vetores fixos unidimensionais, distintos dos arrays `Dim`.

```blitzbasic
Const SIZE%=2*3
Global states#[SIZE]
Type Holder
 Field names$[2], children.Holder[1]
End Type
states[6]=1.25
Print states[0]
```

O limite superior é inclusivo: `[6]` possui sete elementos. Global, Local e Field
aceitam elementos Integer, Float, String e referências nominais a objetos. Os
valores iniciais são zero, zero Single, string vazia e Null, respectivamente.
Globais são alocadas antes do programa, locais na entrada de cada chamada/escopo,
e campos em cada New. Declarações locais em laços não recriam o vetor a cada volta.
Delete limpa os campos vetoriais e libera suas referências para o GC; não exclui
recursivamente os objetos vivos armazenados neles. Um teste de lifetime verifica
que excluir o proprietário permite coletar um filho já excluído, preservando o
alias do proprietário.

O tamanho deve ser expressão constante. Conversões para Integer usam a semântica
legada existente: `[2.5]` termina em 2 e `[2.8]` em 3. Índices constantes fora do
limite são diagnosticados na compilação. Índices dinâmicos têm guarda gerenciada
com arquivo/linha do `.bb`; cada expressão de índice executa uma vez. Guardas
contra índices negativos são intencionalmente mais fortes que acessos nativos
inseguros. O teste inválido dinâmico no original sofreu timeout do adaptador;
não há equivalência comprovada de sua mensagem ou término.

`Const` aceita escalares `%`, `#`, `$`, inicializador obrigatório e declarações
separadas por vírgula no programa principal. Expressões podem usar constantes
anteriores e conversões constantes Int/Float/Str. Os protótipos são resolvidos
antes de campos, funções e corpo principal, permitindo limites de campos com
Const. Inicializadores não podem referenciar Const posteriores ou executar
chamadas arbitrárias. Atribuições são rejeitadas. Parâmetros/locais de funções
podem sombrear nomes de constantes, conforme teste no original.

Não há cópia de vetores inteiros, parâmetros vetoriais ou exportação direta por
`BB.`. Use funções BASIC com argumentos/retornos primitivos para interop; o
exemplo multifile `samples/Hybrid/FixedVectors/main.bb` imprime `42` e `3.5`.
`Data/Read/Restore`, labels, Goto/Gosub e parâmetros opcionais seguem pendentes.
Não foram adicionados comandos fictícios de engine.

A [pesquisa](fixed-vectors-research.md) registra fonte e evidências originais.
Os manifestos reproduzíveis são `tests/Compatibility/vectors.json` e
`tests/Compatibility/constants.json`; resultados locais não substituem capturas
originais. A evolução do SCP está em [scp-milestone8.md](scp-milestone8.md).
