# Object, Handle e memória gerenciada

```blitzbasic
Type Sound
 Field value%
End Type
sound.Sound=New Sound
sound\value=42
id%=Handle(sound)
alias.Sound=Object.Sound(id)
Print alias\value
Delete sound
Print Handle(alias)
Print Object.Sound(id)=Null
```

A saída é `42`, `0`, `1`. `Handle` devolve Integer estável para o mesmo objeto
vivo, atribuído somente na primeira consulta. `Object.Tipo` recupera uma referência
somente se o número está registrado e o tipo nominal é exatamente o solicitado.
Zero, números desconhecidos, objetos de outro Type e handles removidos retornam
Null. Handle aceita referência tipada ou Null; números/strings não são objetos.
Object.Tipo converte o argumento para Integer segundo as regras BASIC existentes,
incluindo arredondamento Float e prefixos numéricos de String.

As formas sem parênteses `Handle objeto` e `Object Tipo numero` também seguem
a gramática unária histórica. `Object.Sound(sndHandle)` é a construção original
em `StrictLoads.bb:59`; o jogo auditado não foi reescrito para aceitá-la.
Handles podem passar como Integer pela ponte C# e ser armazenados em campos,
arrays e vetores; referências recuperadas continuam tipadas. A ponte não expõe
objetos BASIC diretamente nem interpreta handles como endereços nativos.

## Exclusão, aliases e identidade

Delete remove o registro imediatamente. Aliases excluídos continuam preservando
identidade e navegação M6 enquanto observáveis, mas têm Handle zero. O inteiro
antigo não mantém o objeto vivo nem volta a resolver depois de New. Delete Null,
Delete repetido e Delete Each mantêm seus contratos anteriores. Campos e vetores
são limpos conforme a implementação já validada de lifetime.

O registro mantém referências fortes somente a objetos vivos, que já pertencem
às listas de tipos até Delete. Nenhum novo tombstone, finalizer, refcount ou weak
map foi introduzido. A capacidade da tabela pode refletir o pico de handles vivos;
não contém uma entrada para cada objeto excluído. Referências circulares de
objetos excluídos sem aliases continuam coletáveis.

Os números são monotônicos e pertencem ao programa/assembly gerado, não são IDs
globais entre programas. Reuso de endereço do allocator nativo não implica reuso
de handle. Int32 positivo esgotado gera erro gerenciado antes de reutilizar IDs;
essa é uma divergência deliberada do overflow nativo sem contrato seguro.
Não há suporte a acesso concorrente às listas/tabela nesta etapa.

## Evidência e reprodução

[Pesquisa original](handles-original-research.md), [decisão de lifetime](handles-design.md)
e [validação](validation.md) distinguem fonte, capturas Proton e testes gerenciados.
`tests/Compatibility/handles.json` contém casos de handles nulos/inválidos,
tipagem, conversões, arrays, exclusão, vizinhos de aliases e 1.000 criações.
Os testes gerenciados incluem GC com a assembly ainda enraizada, exaustão simulada
e 100.000 ciclos. A demonstração multifile está em `samples/Hybrid/Handles/`.

Para medir alocações e memória viva/comprometida, após build Release:

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/Diagnostics/handle-memory.bb -o artifacts/handle-memory.dll
dotnet artifacts/handle-memory.dll > artifacts/handle-memory.json
```

A medição aquece o JIT, executa duas etapas de100.000 operações e coleta/limpa
posições entre elas. Valores de bytes são observações, não asserts de vazamento;
os invariantes estruturais exigem zero registros vivos após cada etapa.
