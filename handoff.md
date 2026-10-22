# Handoff M14 — Init/Close reais; SCP para em SystemProperty

## Checkpoint e validação

Base M13 `5484fb3`, tag `blitzcs-m13-checkpoint`; trabalho na branch
`codex/milestone-14`, checkpoint final identificado pela tag
`blitzcs-m14-checkpoint`. .NET 10.0.401/net10, Linux x64.
O relatório [M14](docs/scp-milestone14.md) e suas evidências são a referência
para resultados, limites e reprodução. Não há impasse estrutural que justifique
escalonamento para um modelo caro.

## Entrega e fronteiras

- BlitzCS: assinaturas externas primitivas sem implementação, CheckSemantics,
  CLI check-semantics e erro de vínculo em chamada usada sem implementação.
  Não executa inicializador estático ou código nativo na inspeção/compilação.
- Blitz3DCS.Audio: biblioteca independente, SDL3 Linux x64, FSOUND.Init/Close
  reais; LastError com falhas de driver/loader. Init normaliza retorno para 0/1;
  Close retorna 0 como adaptação ao registro (original nativo é void).
- Tabela interna de handles preserva nint, IDs Int32 positivos monotônicos,
  invalidação e liberação. Ainda não consumida por streams; não expõe ponteiros
  ao BASIC nem altera os handles de objetos existentes.
- Exemplo e catálogos: samples/Audio. Catalog scp.json registra somente as
  implementações reais Init/Close e os comandos anteriores. declarations.json
  demonstra Stream_Play sem implementação: semântica passa, build falha.

## SCP e próximo obstáculo

Main +23 alvos Include passam sintaxe. Com o catálogo Audio/scp.json, o primeiro
erro semântico integrado agora é fullscreen_window_fix.bb:45:23 SystemProperty,
comando nativo ainda não registrado. FMod.bb passa a emitir assembly isolado:
3/30 no total (também Difficulty e KeyName); mesmos 30 hashes. Não há assembly
integrado, vínculo completo ou execução do jogo. Erros posteriores desconhecidos.

## Referência e limitações

FMOD 3.75 original PE32/i386 permanece somente em processo x86 via Proton.
Novo probe NOSOUND usa original verdadeiro; docs/evidence/m14-fmod contém
fonte/captura/assinaturas. Cinco retornos Init coincidem com SDLdummy; duas flags
aceitas no original são recusadas explicitamente. Não houve playback, comparação
de mixer, streams ou códigos GetError. Trocar configuração exige Close; repetir
a mesma configuração é aceito. Apenas flags=0; canais ainda não reservam mixer.

A auditoria conta13 declarações FSOUND, 10 nomes ativos, 14 pontos de chamada e 0 FMUSIC e registra
a divergência Stream_Open3 args no .decls versus4 no header/export. Investigar antes
de implementar streams; nunca converter FSOUND_STREAM* x64 para Integer.
SDL3 é dependência de sistema no exemplo; teste de publicação copia SDL nativo
e assembly para diretório isolado. Distribuição entre SOs/RIDs ainda pendente.

## Próximos passos

1. Implementar SystemProperty fora do núcleo após auditar chaves reais utilizadas.
2. Repetir a compilação integrada sem mocks e separar erro semântico, vínculo e runtime.
3. Investigar Stream_Open e implementar stream/canal real com tokens e ownership.
4. Ampliar comparação FMOD somente sobre comportamento efetivamente executado.

Não implementar renderer, Vulkan, física ou áudio fictício para remover erros.
