---
title: Milestone 10 handles seguros Data e compilação integrada SCP
author: Codex
date: 2026-10-22
touches:
  - src/BlitzCS.Compiler/ObjectRuntime.cs
  - src/BlitzCS.Compiler/Syntax.cs
  - src/BlitzCS.Compiler/Semantics.cs
  - src/BlitzCS.Compiler/CSharpEmitter.cs
  - src/BlitzCS.Compiler/CompatibilityRuntime.cs
  - src/BlitzCS.Compiler/DataRuntime.cs
  - tools/scp_compile_probe.py
  - tools/original_blitz3d_adapter.py
  - docs/handles.md
  - docs/scp-milestone10.md
---

## Semântica e lifetime

Original stock basic.cpp423-438 atribui handle Integer monotônico, sob demanda,
e lookup exige tipo nominal exato. Delete remove mapas (basic.cpp268-293).
Object.Tipo converte argumento para Integer; Handle aceita objeto/Null, não Int.
Null/deleted aliases retornam0, stale/wrongtype/unknown lookup retornaNull.
BlitzForge tem herança; não usar sua semântica para afirmar stock compatível.

__BlitzHandles mantém Dictionary<int,__BlitzObject> somente de vivos. Objeto tem
HandleId; Delete Release remove chave antes de limpar/enfraquecer registro M6.
Não há ponteiro, refcount, finalizer ou novo weak map. IDs nunca reciclam;
esgotar Int32 positivo falha seguro, divergência deliberada do overflow nativo.
Testes GC mantêm assembly/tabela enraizadas e coletam alvo excluído sem alias.
Estresse100k e medição2x100k verificam zero entradas e capacidade3 (pico1), sem
usar RSS ou threshold instável de memória. Memória comprometida não é memória viva.

## Intrínsecos e Data

Abs/Sgn são unários polimórficos no compiler original, não comandos de engine.
Tipo Int/Float preservado. V1.118 Abs Float constante -0 -> -0, runtime variável
-0 -> +0. Dobrar constantes no Bind e MathF.Abs runtime resolve esse detalhe;
não confiar somente no ternário C++ fonte. IntMin real usa $80000000, não literal
decimal positivo fora da faixa. Fonte/binário oficial são evidências distintas.
Else If em duas palavras equivale a ElseIf (toker.cpp26) e não cria If aninhado.

Data/Read/Restore: CollectData apósConst junta escalares constantes e labels
main-only na ordem Includes; cursor único por programa, Restore opcionaldefault0.
Read é lvalue primitivo (variável/array/field/vector), não objeto nem Const.
Float DATA->Int trunca pelo C runtime, diferente do cast BASIC que arredonda.
Não implementar Goto/Gosub para satisfazer um probe: corrigir fixture Data para
não exigir recurso fora do escopo, recapturando original com novo hash.

## Evidência e auditoria

510 .NET e32 Python passam;79 fixtures locais. Native fresh hash-matched79,
78 coincidências,1 mismatch intencional upper-bound-error status0 vs nãozero.
Tentativa de handle-arrays crashou; retry atual por hash correto confirmou5
linhas1. Basename de arquivos não é identidade de fixture: comparar path+SHA.
Cada tentativa tem evidence dir distinto; nunca substituir timeout por capture
histórico. Adapter agora salva estágio e saídas parciais/arquivos instrumentados
no finally, mantendo timeout como AdapterError. Dismissal inalterado.

SCP30 hashes iguais,2/30 compilados. Sintaxe15->5; final7 semânticos,14 nativos
não registrados,2 externos. Main StrictLoads59 Object.Sound ->Update838 Else If
->Skybox11 %110001. Literal binário é próximo bloqueio; não há renderer/mocks.
Métrica diagnostic_include_depth=2 witness Main->MapSystem->Skybox é profundidade
do arquivo do primeiro diagnóstico, NÃO traço completo de parser. Closure
estática separada23Includes; errosobservadoslowerbound1, adicionaisunknown/null.
Consultar artifacts/m10-final-differential.json e docs/validation.md para prova.

Pipeline final completo verify.sh status0:510.NET,32Python,79locais,exemplos e
handle-memory. Native final79capturas=78match1mismatch(statusupperbounds0).
Trail build/check green,164 arquivos749nós748arestas; deepnãoexecutado.
