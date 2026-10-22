# M12 — Integração de runtime e compilação semântica SCP

## Resultado verificado

O módulo independente `Blitz3DCS.Runtime` fornece seis métodos reais: FileSize,
FileType, CurrentDir, GetEnv, Chr e Len. O catálogo opt-in registra esses métodos
e o RuntimeError já existente. O compilador não ganhou dependência da engine;
nenhum comando gráfico, de áudio ou de física foi simulado.

| Etapa | M11 | M12 |
|---|---|---|
| Sintaxe integrada Main + Includes | Aprovada | Aprovada |
| Primeiro erro integrado | Main.bb:12 FileSize não registrado | FMod.bb:15 FSOUND_Init não registrado |
| Arquivos isolados com assembly emitido | 2/30 | 2/30 |
| Arquivos isolados com sintaxe validada | 27/30 | 27/30 |
| Assembly integrado do SCP emitido | Não | Não |
| Vinculação e execução funcional do jogo | Não alcançadas | Não alcançadas |

Progressão observada: FileSize em Main.bb:12 → Chr na mesma linha → resolução
desses comandos e Len → FSOUND_Init em FMod.bb:15, include de profundidade 1.
`fmod.decls:3` declara `FSOUND_Init%(freq%,channels%,flags%)` na biblioteca
`fmod.dll`, símbolo `_FSOUND_Init@12`. O catálogo gerenciado não importa .decls
automaticamente, e não há uma implementação FMOD registrada. A classificação
`external_missing` significa dependência/vínculo ausente, não prova de que um
arquivo DLL específico foi procurado ou carregado.

A árvore estática mantém 23 alvos Include, além de Main, profundidade máxima 2 e
nenhum Include ausente. A compilação para no primeiro erro; o total posterior é
desconhecido. Não se comprova resolução semântica completa da linguagem utilizada
pelo jogo. `MusicPlayer.bb`, fora dessa árvore, continua sem `BlitzAL.bb`.
LoadAllSounds/Save isolados continuam precisando do contexto de declarações Dim.

Os mesmos 30 SHA-256 do snapshot M11 foram mantidos. Snapshot declarado:
Regalis11/scpcb, commit `edb8fe0840b78f14d1aef3a0bf6174630e7be296`;
o probe continua sem atestar a procedência Git desse diretório externo.

Primeiros resultados por categoria, não contagem total de todos os erros:

| Categoria | M11 | M12 |
|---|---:|---:|
| Compilado | 2 | 2 |
| Léxico | 0 | 0 |
| Sintático/contexto isolado | 2 | 2 |
| Semântico/tipos | 8 | 9 |
| Nativo não registrado | 16 | 14 |
| Dependência externa ausente | 2 | 3 |
| Infraestrutura | 0 | 0 |

Além de Main, o primeiro diagnóstico de Update.bb isolado avançou de Chr para
GetINIInt em Update.bb:12. A mudança de categorias reflete novos primeiros erros;
não implica que todos os demais símbolos ou tipos desses arquivos foram resolvidos.

## Catálogo e contratos

O [inventário de APIs](scp-native-catalog-m12.md) registra as 593 entradas
originais, assinaturas, tipos, frequências candidatas, subsistemas e estados,
além das declarações externas do aplicativo. Ele percorre 32 arquivos
recursivamente; os dois arquivos de Map Creator excedem a baseline de 30 arquivos
isolados. Frequências são candidatos sintáticos, com resolução semântica ainda
não realizada. Dependências de serviço comprovadas ficam separadas das ainda
não investigadas; não se apresenta um call graph completo inventado.

O catálogo foi produzido antes da implementação dos métodos; seu mapa de suporte
foi atualizado somente após implementação/testes. A [documentação do runtime](native-runtime-m12.md)
explica os comportamentos comparados e limites de host/codepage. O mecanismo de
registro continua genérico para futuras fachadas de engine. Nenhum Vulkan foi implementado.

A cadeia de assemblies A → B → C é descoberta por metadados, publicada e
executada em diretório isolado. Dependências ausentes, identidade incompatível e
colisões possuem diagnóstico; os testes preservam as saídas existentes nos casos
negativos. Assets, bibliotecas nativas e recursos satélite ainda exigem distribuição
explícita; não estão cobertos por esse fechamento gerenciado.

## Memória e regressões

A [investigação Debug](debug-lifetime-m12.md) identificou uma raiz introduzida
pela própria enumeração de valores no teste. A correção enumera somente chaves
inteiras, preservando cardinalidade, coleta forçada, WeakReference e raiz da
assembly/lista. Nenhum código do runtime de objetos/handles foi alterado.

A validação e as capturas resumidas ficam em
[evidence/m12-validation.json](evidence/m12-validation.json). Os 81 casos anteriores
são comparados com capturas originais M11 de hashes idênticos, explicitamente
reutilizadas, e os cinco novos casos foram executados novamente no original via
Proton em M12. Resultados: 85/86 coincidentes, permanecendo apenas o contrato de
exitstatus de upper-bound-error; zero erro de adaptador nos cinco casos novos.
Fixtures locais não são apresentadas como capturas novas do original.

## Reproduzir

```sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH dotnet test BlitzCS.slnx -c Debug
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Native/blitz3dcs.json --check-syntax \
  --report artifacts/scp-m12-after.json
```

Próximo passo: definir uma integração real para os símbolos FMOD ou um módulo
compatível implementado de fato, com ABI/licença/distribuição verificadas. Depois,
reexecutar a compilação integrada e tratar o próximo diagnóstico. A ausência dessa
integração não foi mascarada com stubs, importações fictícias ou alterações no SCP.
