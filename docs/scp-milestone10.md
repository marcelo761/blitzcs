# Relatório SCP — Milestone 10

## Escopo e método

Esta etapa mantém a matriz de compilação individual dos 30 arquivos `.bb` e acrescenta uma compilação integrada de `Main.bb`, na qual o CLI resolve os `Include` pelo compilador BlitzCS. Nenhum arquivo do jogo é reescrito e nenhum comando de engine é simulado. O probe registra o primeiro diagnóstico devolvido pelo compilador; ele não conta erros posteriores.

O snapshot é `/tmp/scpcb-audit-source`, declarado como `Regalis11/scpcb` no commit `edb8fe0840b78f14d1aef3a0bf6174630e7be296`. `provenance_verified_by_probe` permanece `false`. Cada compilação individual preserva SHA-256 e fase. A integração usa o catálogo externo runtime passado em `--externals` e a projeção de símbolos nativos em `tools/scp_native_catalog.json`.

Para repetir a captura Release:

```sh
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Native/runtime.json \
  --integrated-entry Main.bb \
  --report artifacts/scp-m10-after.json \
  --timeout 60
```

O baseline M9 em [`artifacts/scp-m9-after.json`](../artifacts/scp-m9-after.json) fica preservado. A captura inicial antes de M10 está em [`artifacts/scp-m10-before.json`](../artifacts/scp-m10-before.json), o checkpoint após Handles/Abs está em [`artifacts/scp-m10-handles.json`](../artifacts/scp-m10-handles.json), e a captura final após Data/Read/Restore e ElseIf está em [`artifacts/scp-m10-after.json`](../artifacts/scp-m10-after.json). Os três relatórios usam os mesmos hashes dos 30 fontes do snapshot.

## Como ler a métrica integrada

`integrated.first_error` contém o arquivo, a linha, a coluna, a categoria e o texto do primeiro diagnóstico. `observed_errors_lower_bound` vale 1 quando o compilador para com erro; `additional_errors` fica `null`, pois o CLI não informa quantos diagnósticos posteriores existiriam. Portanto, esse resultado não é um total de erros.

`integrated.diagnostic_include_depth` descreve a profundidade do arquivo que contém esse primeiro diagnóstico na árvore estática de Includes. `diagnostic_include_paths` guarda os caminhos testemunha usados para chegar ao arquivo. Se houver caminhos com profundidades distintas, o campo registra o menor e o maior nível. A árvore em `static_include_closure` tem sua própria lista, quantidade e profundidade máxima. A contagem inclui os alvos transitivos únicos referenciados a partir da entrada e exclui o próprio `Main.bb`; ela não conta diretivas repetidas ou Include em arquivos fora dessa árvore. Assim, os 23 alvos no snapshot correspondem à árvore alcançável de Main. O inventário M8 registrou 24 diretivas no conjunto examinado, incluindo `MusicPlayer.bb → BlitzAL.bb`, que não é alcançado por Main, portanto as contagens têm escopos diferentes.

O compilador Blitz3D original resolve Includes relativos ao diretório do arquivo principal, ignora uma segunda referência ao mesmo caminho normalizado e processa o conteúdo no ponto da diretiva e no escopo atual. O arquivo principal não começa marcado como incluído, então um ciclo de volta ao principal pode processá-lo uma vez. A nota de pesquisa [`docs/include-research.md`](include-research.md) documenta as fontes e capturas diferenciais. A métrica do probe é deliberadamente descritiva da árvore estática e do primeiro local observado, sem inferir quantos arquivos o parser percorreu.

## Matriz individual e construções

O JSON `scp-m10-after.json` contém uma linha de resultado para cada um dos 30 arquivos, com fase, hash, diagnóstico e categoria (`lexical`, `syntactic`, `semantic_type`, `native_unregistered` ou `external_missing`). Estas categorias descrevem somente o primeiro diagnóstico observado em cada compilação isolada. Falha de infraestrutura e resultado não classificado permanecem categorias separadas.

No baseline M9, o primeiro erro individual de `StrictLoads.bb` está na linha 59: `Local snd.Sound = Object.Sound(sndHandle)`. O diagnóstico é `Unsupported keyword 'object' cannot be used as a variable.` Após Handles/Abs e no checkpoint final, o primeiro erro individual avançou para a linha 153, na assinatura `Function StreamSound_Strict(file$,volume#=1.0,custommode=Mode)`, com `Const initializer must use previously declared constants.` Isso identifica a construção observada em cada checkpoint; não generaliza o restante do arquivo. A taxa de compilação individual não equivale a compilação integrada nem valida execução do jogo.

No checkpoint final, 2/30 arquivos compilam; os 28 primeiros bloqueios se distribuem em cinco sintáticos, sete semânticos, 14 nativos não registrados e dois externos ausentes. A matriz completa está no JSON; a tabela abaixo resume o primeiro resultado observado para cada arquivo.

| Arquivo | Resultado e primeiro diagnóstico |
|---|---|
| `AAText.bb` | semantic_type: `AAText.bb(1,24): Unknown BASIC function or external symbol 'getiniint'.` |
| `Achievements.bb` | semantic_type: `Achievements.bb(19,26): Unknown BASIC function or external symbol 'getiniint'.` |
| `Blitz_Basic_Bank.bb` | native_unregistered: `Blitz_Basic_Bank.bb(21,21): Unknown BASIC function or external symbol 'banksize'.` |
| `Blitz_File_FileName.bb` | native_unregistered: `Blitz_File_FileName.bb(28,5): Unknown BASIC function or external symbol 'right'.` |
| `Blitz_File_ZipApi.bb` | native_unregistered: `Blitz_File_ZipApi.bb(247,29): Unknown BASIC function or external symbol 'banksize'.` |
| `Converter.bb` | native_unregistered: `Converter.bb(1,1): Unknown BASIC function or external symbol 'graphics3d'.` |
| `CPU_Details.bb` | native_unregistered: `CPU_Details.bb(1,1): Unknown BASIC function or external symbol 'graphics'.` |
| `DevilParticleSystem.bb` | native_unregistered: `DevilParticleSystem.bb(50,15): Unknown BASIC function or external symbol 'createpivot'.` |
| `Difficulty.bb` | compiled: `Compilado` |
| `DrawPortals – Kopio.bb` | native_unregistered: `DrawPortals – Kopio.bb(25,12): Unknown BASIC function or external symbol 'createtexture'.` |
| `DrawPortals.bb` | native_unregistered: `DrawPortals.bb(21,12): Unknown BASIC function or external symbol 'createtexture'.` |
| `Dreamfilter.bb` | native_unregistered: `Dreamfilter.bb(6,15): Unknown BASIC function or external symbol 'createcamera'.` |
| `FMod.bb` | external_missing: `FMod.bb(15,1): Unknown BASIC function or external symbol 'fsound_init'.` |
| `fullscreen_window_fix.bb` | native_unregistered: `fullscreen_window_fix.bb(45,23): Unknown BASIC function or external symbol 'systemproperty'.` |
| `Items.bb` | native_unregistered: `Items.bb(37,59): Unknown BASIC function or external symbol 'copyentity'.` |
| `KeyName.bb` | compiled: `Compilado` |
| `LightMapPNG.bb` | native_unregistered: `LightMapPNG.bb(1,1): Unknown BASIC function or external symbol 'graphics3d'.` |
| `LoadAllSounds.bb` | syntactic: `LoadAllSounds.bb(4,15): Expected Close; found ','.` |
| `Main.bb` | syntactic: `Skybox.bb(11,38): Expected an expression.` |
| `MapSystem.bb` | syntactic: `Skybox.bb(11,38): Expected an expression.` |
| `Menu.bb` | semantic_type: `Menu.bb(1,20): Unknown BASIC function or external symbol 'loadimage_strict'.` |
| `MusicPlayer.bb` | external_missing: `MusicPlayer.bb(1,1): Unable to open include file 'BlitzAL.bb': File or directory not found.` |
| `NPCs.bb` | semantic_type: `NPCs.bb(38,8): Unknown Type 'waypoints'.` |
| `Particles.bb` | semantic_type: `Particles.bb(95,8): Unknown Type 'rooms'.` |
| `RMesh_Model_Viewer.bb` | native_unregistered: `RMesh_Model_Viewer.bb(714,16): Unknown BASIC function or external symbol 'input'.` |
| `Save.bb` | syntactic: `Save.bb(594,14): Expected Close; found ','.` |
| `Skybox.bb` | syntactic: `Skybox.bb(11,38): Expected an expression.` |
| `StrictLoads.bb` | semantic_type: `StrictLoads.bb(153,58): Const initializer must use previously declared constants.` |
| `Update.bb` | native_unregistered: `Update.bb(9,19): Unknown BASIC function or external symbol 'chr'.` |
| `UpdateEvents.bb` | semantic_type: `UpdateEvents.bb(2,2): Unknown BASIC function or external symbol 'catcherrors'.` |

## Resultado integrado

Na captura inicial com o Release M9, a compilação integrada de `Main.bb` para em `StrictLoads.bb:59:20`, na construção `Local snd.Sound = Object.Sound(sndHandle)`. Após Handles/Abs, ela avança até `Update.bb:838:5`. No checkpoint final, chega a `Skybox.bb:11:38`, na chamada `LoadBrush_Strict(fname$,%110001)`, com categoria `syntactic`, profundidade 2 e witness `Main.bb → MapSystem.bb → Skybox.bb`. O relatório registra `observed_errors_lower_bound=1` e `additional_errors=null`: erros posteriores são desconhecidos. O fechamento estático contém 23 alvos, sem Includes ausentes, e tem profundidade máxima 2.

A compilação não executa o jogo. Nenhum número de erros totais será inferido de um diagnóstico único.

## Última captura de verificação

O probe foi repetido com o Release final após separar o folding de Abs do
helper Float de runtime. O artefato final registra **2 compilados, 5 sintáticos,
7 semânticos, 14 nativos não registrados e 2 externos ausentes**, sem falhas
lexicais ou de infraestrutura. Os 30 hashes permanecem idênticos. O diagnóstico
integrado permanece Skybox.bb:11:38 no literal binário `%110001`. As capturas
anteriores são checkpoints; a tabela por arquivo acima foi gerada do JSON final.
