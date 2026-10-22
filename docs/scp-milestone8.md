# Relatório SCP — Milestone 8

## Escopo e método

Esta auditoria compila cada arquivo `.bb` selecionado separadamente; ela não executa o jogo nem simula comandos de engine. A amostra contém os 30 arquivos de topo do snapshot em `/tmp/scpcb-audit-source`, declarado como `Regalis11/scpcb` no commit `edb8fe0840b78f14d1aef3a0bf6174630e7be296`. Os relatórios preservam SHA-256 por arquivo. `provenance_verified_by_probe` está marcado `false`, portanto a origem do snapshot não foi verificada pelo próprio probe.

O probe usa o CLI Release atual e timeout de 60 segundos por arquivo. Ele registra apenas o primeiro diagnóstico de cada compilação; erros posteriores no mesmo arquivo permanecem desconhecidos. O baseline em [`artifacts/scp-m8-before.json`](../artifacts/scp-m8-before.json) e o checkpoint de vetores em [`artifacts/scp-m8-vectors.json`](../artifacts/scp-m8-vectors.json) são capturas históricas preservadas; executar o comando atual não os reproduz nem os sobrescreve. A captura Release mais recente está em [`artifacts/scp-m8-after.json`](../artifacts/scp-m8-after.json). O inventário de construções está em [`artifacts/scp-m8-inventory.json`](../artifacts/scp-m8-inventory.json).

Para repetir a auditoria no snapshot local e gravar um novo relatório:

```sh
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --report artifacts/scp-m8-after.json \
  --timeout 60
```

## Antes e depois

| Medição | Compilados | Diagnóstico de `Main.bb` |
|---|---:|---|
| Baseline | 1/30 | Linha 291, `Global SCP1025state#[6]`: caractere `[` não suportado |
| Checkpoint de vetores | 1/30 | Linha 16, chamada `RuntimeError` com a mensagem legítima sobre DLLs |
| Release atual, vetores e `Const` | 2/30 | Linha 16, mesma chamada `RuntimeError` ainda não suportada |

`KeyName.bb` compila nos três checkpoints. `Difficulty.bb` passou a compilar após a implementação de `Const`. O diagnóstico lexical de caractere `[` na linha 291 deixou de aparecer; a compilação atual para na análise sintática da linha 16, em `RuntimeError`. O probe não valida o restante de `Main.bb` nem o projeto completo.

### Matriz por arquivo

Os relatórios JSON trazem os diagnósticos completos e hashes. A tabela resume os 19 arquivos cujo primeiro resultado mudou entre baseline e Release atual; “próximo diagnóstico” é apenas o primeiro que o probe observou depois do avanço.

| Arquivo | Primeiro bloqueio no baseline | Resultado/primeiro bloqueio atual |
|---|---|---|
| `AAText.bb` | `[` em 3 | `CameraViewport` em 31 |
| `Converter.bb` | `[` em 142 | `Graphics3D` em 1 |
| `DevilParticleSystem.bb` | `[` em 10 | `SeedRnd` em 51 |
| `Items.bb` | `[` em 356 | assinatura com parâmetro padrão em 31 |
| `Main.bb` | `[` em 291 | `RuntimeError` em 16 |
| `MapSystem.bb` | `[` em 177 | `TextureBlend` em 35 |
| `Menu.bb` | `[` em 740 | `MaskImage` em 6 |
| `NPCs.bb` | `[` em 38 | inicializador `Field` em 42 |
| `Particles.bb` | `[` em 233 | assinatura com parâmetro padrão em 17 |
| `RMesh_Model_Viewer.bb` | `[` em 568 | `WriteLine` em 19 |
| `Save.bb` | `[` em 66 | `WriteString` em 20 |
| `StrictLoads.bb` | `[` em 30 | `RuntimeError` em 17 |
| `UpdateEvents.bb` | `[` em 22 | `EntityAlpha` em 45 |
| `Achievements.bb` | `Const` em 3 | assinatura com parâmetro padrão em 37 |
| `Blitz_File_ZipApi.bb` | `Const` em 94 | assinatura com parâmetro padrão em 234 |
| `Difficulty.bb` | `Const` em 20 | compila |
| `FMod.bb` | `Const` em 1 | `FSOUND_Init` em 15 |
| `fullscreen_window_fix.bb` | `Const` em 29 | literal hexadecimal em 30 |
| `Update.bb` | `Const` em 10 | assinatura com parâmetro padrão em 21 |

Os outros 11 arquivos mantiveram o primeiro resultado: `KeyName.bb` compila; `Blitz_Basic_Bank.bb`, `Blitz_File_FileName.bb`, `CPU_Details.bb`, `DrawPortals – Kopio.bb`, `DrawPortals.bb`, `Dreamfilter.bb`, `LightMapPNG.bb`, `LoadAllSounds.bb`, `MusicPlayer.bb` e `Skybox.bb` continuam bloqueados nos diagnósticos do JSON. Os SHA-256 dos 30 arquivos coincidem entre baseline e snapshot atual; nenhum fonte do jogo foi alterado.

O diagnóstico da linha 16 não indica que a mensagem de DLL tenha sido lida como conteúdo-fonte. O texto está dentro do argumento literal de `RuntimeError` no snapshot. A BlitzCS ainda não reconhece essa instrução e falha ao analisá-la. A verificação das DLLs é lógica real do jogo e não foi removida nem contornada.

## Construções encontradas e situação

O inventário encontrou 93 linhas com declarações de vetor fixo em 11 arquivos, 127 declarações `Const` em 13 arquivos e 118 assinaturas com parâmetros padrão em 20 arquivos. Também encontrou 30 linhas `Data`, duas linhas `Read` e uma `Restore`, todas em `Skybox.bb`; não encontrou `Goto` nem `Gosub` nos 30 arquivos examinados. As contagens são linhas de ocorrência, não uma contagem de símbolos únicos.

| Recurso | Estado nesta etapa | Evidência e limite |
|---|---|---|
| Arrays fixos numéricos em globais, locais e campos | Implementado no compilador | A declaração `Global SCP1025state#[6]` em `Main.bb:291` não bloqueia mais a análise. A cobertura geral também inclui testes unitários. Dimensões/limites e expressões de limite continuam restritos ao que os testes atuais comprovam. |
| Vetores em campos, incluindo referências a objetos | Parcial | Há suporte e testes de vetor em campos, inclusive limpeza de referências ao excluir o objeto proprietário. O SCP contém limites calculados como `[(gridsize*gridsize)+11]`; o probe individual não chegou a validar todas essas declarações no contexto do projeto. |
| `Const` | Parcial | `Difficulty.bb` passou e `Const` deixou de ser o primeiro erro em diversas unidades. O literal hexadecimal `$80000000` em `fullscreen_window_fix.bb:30` ainda produz `Expected an expression`. As 127 linhas inventariadas não equivalem a 127 casos semânticos verificados. |
| Parâmetros opcionais | Ausente | Oito dos 28 primeiros erros são `Expected Close` em assinaturas com valores padrão; o inventário encontra esse padrão em 20 arquivos. |
| `Data`, `Read`, `Restore` | Ausente | As 33 linhas encontradas em `Skybox.bb` não foram compiladas até esse ponto devido a bloqueios anteriores. |
| `Goto`, `Gosub` | Não encontrado nesta amostra | Zero ocorrências no inventário; isso não demonstra ausência em outras revisões ou módulos. |
| `Include` | Implementado, com dependência ausente | Há 24 diretivas em três arquivos. `MusicPlayer.bb` não compila isoladamente porque `BlitzAL.bb` não existe no snapshot local. |
| Comandos de engine / extensões nativas | Não verificado de ponta a ponta | A auditoria para no primeiro erro. Os primeiros erros mostram comandos como `Graphics`, `CameraViewport`, `TextureBlend`, `MaskImage` e `EntityAlpha`; não houve integração/mocks de renderer, áudio ou física para ocultá-los. |
| Bibliotecas externas | Parcial / dependente do ambiente | `FMod.bb` para em `FSOUND_Init`; `BlitzAL.bb` está ausente. O probe não determina se cada símbolo externo está disponível em runtime. |

## Erros observados

No checkpoint Release atual, 2 arquivos compilam e 28 param com erro de compilação; não houve timeout nem falha de infraestrutura. Agrupando somente a forma do primeiro diagnóstico, há 14 `Expected Equal`, oito `Expected Close`, dois `Unknown function` e quatro casos únicos (`Expected an expression`, sintaxe de `=`, include ausente e `Expected Separator`). Essa classificação descreve os diagnósticos observados, não necessariamente a causa semântica completa.

Exemplos que ajudam a priorizar a próxima fatia:

- `Main.bb:16`: instrução `RuntimeError`, bloqueando o arquivo principal antes de seus includes.
- Assinaturas com argumentos padrão em `Achievements.bb`, `Blitz_File_ZipApi.bb`, `DrawPortals.bb`, `Items.bb`, `Particles.bb`, `Skybox.bb` e `Update.bb` (o segundo `DrawPortals` é uma cópia).
- Chamadas de comandos de engine no início de `AAText.bb`, `Converter.bb`, `CPU_Details.bb`, `Dreamfilter.bb`, `MapSystem.bb`, `Menu.bb`, `Particles.bb` e outros arquivos.
- Funções não reconhecidas `Right` (`Blitz_File_FileName.bb`) e `FSOUND_Init` (`FMod.bb`).
- Um include ausente: `BlitzAL.bb` (`MusicPlayer.bb`).
- Literal hexadecimal ainda não aceito: `fullscreen_window_fix.bb:30`.
- Inicializador em campo rejeitado: `NPCs.bb:42`, `Field GravityMult# = 1.0`.

Os 28 diagnósticos são o primeiro bloqueio de cada unidade. Não devem ser interpretados como uma lista completa dos problemas de cada módulo. A taxa de 2/30 mede compilação individual dos arquivos selecionados, não compilação do SCP como projeto e não compatibilidade em runtime.

## Bloqueios observados nesta captura

Na captura descrita acima, os bloqueios visíveis eram `RuntimeError` em `Main.bb`, parâmetros padrão como primeiro diagnóstico em oito arquivos, literal hexadecimal em `fullscreen_window_fix.bb` e inicializador de `Field` em `NPCs.bb`. Esses itens descrevem o estado naquele relatório; após alterações posteriores, repita o probe e use o relatório novo para escolher a próxima fatia. A cobertura de `Data/Read/Restore` e comandos de engine também precisa ser reavaliada quando a compilação alcançar esses trechos. Comandos gráficos, áudio e física pertencem a Blitz3DCS/runtime de engine, não ao núcleo independente BlitzCS.

Nenhum arquivo de SCP foi editado. Nenhum comando de engine foi substituído por mock. Esta etapa não demonstra que SCP compila integralmente.
