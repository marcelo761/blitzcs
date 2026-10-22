---
title: Milestone 6 lifetime gerenciado e memoria verificavel
author: Codex
date: 2026-10-21
touches:
  - src/BlitzCS.Compiler/ObjectRuntime.cs
  - tests/BlitzCS.Compiler.Tests/ObjectMemoryTests.cs
  - tests/BlitzCS.Compiler.Tests/Fixtures/ObjectRuntime.Before.txt
  - tests/BlitzCS.CLI.Tests/ManagedObjectLifetimeTests.cs
  - tests/Compatibility/lifetime.json
  - docs/memory-methodology.md
  - docs/runtime-errors.md
---

## Decidido

Este milestone substitui a retenção permanente registrada no Milestone 5.
Separar `__BlitzObject` (identidade) de `__BlitzObjectNode` (posição) é necessário:
um alias excluído conserva sua posição durante Insert, não uma fotografia de
vizinhos. A lista mantém Value forte somente para vivos. Delete limpa campos e
troca Value por WeakReference; aliases e campos BASIC permanecem fortes.
As posições ligam outras posições, sem possuir os objetos excluídos.

A referência fraca tem escopo justificado: o GC decide quando não há identidade
observável. Sem refcount manual ou finalizers próprios. Maintain visita dois
registros por operação, Find limpa vazios no percurso e Scavenge é linear apenas
para diagnóstico. Após GC sem atividade pode restar metadata vazia, sem objetos
BASIC excluídos vivos. Vivos continuam pertencendo ao tipo até Delete.

O contrato moderno mantém exceções runtime não tratadas com saída não zero,
stderr e localização de origem. O valor numérico depende do host .NET. Sem perfil
Legacy de término por erro; não normalizar a divergência nativa para status zero.

## Verificado

Release: zero avisos/erros, 354 testes .NET (345 anteriores + 5 memória + 3 erros
+ 1 híbrido com GC), 16 Python e 28 fixtures locais. Proton original: 27 das 28
comparações coincidem, incluindo 10/10 lifetime; única divergência intencional é
upper-bound-error com saída nativa 0 versus não zero. Sem erros de adaptador ou
pendências. Capturas reais, sem OCR, separadas das expectativas locais.

O original confirmou: após Delete b e Insert c Before a, After b é Null e
Before b tem valor 1; Insert c After a mantém After b Null e Before b passa a 3.
A fonte consultada basic.cpp, commit 6beb288, libera ownership em Delete e mantém
shell na used list enquanto aliases refcount ainda existem. Não se afirma que
essa fonte seja exatamente o binário oficial V1.118 observado.

Comparação isolada Release, 100.000 New/Delete: referências fortes a excluídos
100.000 → 0, alvos fracos após GC 0. Registros 12.473 → 0 com 6.237 Delete null
tipados, antes do Scavenge. Alocações da thread 4.002.472 → 10.402.928 bytes;
memória viva do processo 19.095.392 → 15.429.120; GC committed 29.675.520 →
26.644.480. Estes últimos são snapshots do processo, não limites por objeto.

## Cuidados

Alocações aumentaram cerca de 2,6 vezes neste workload. A comparação é entre
versões da BlitzCS, não um perfil de memória do Blitz3D nativo. O baseline exato
está em tests/BlitzCS.Compiler.Tests/Fixtures/ObjectRuntime.Before.txt; use
BLITZCS_MEMORY_RUNTIME_SOURCE somente com o filtro do probe de churn.
Não confundir committed/available/RSS com reserva virtual: a API portátil usada
não fornece bytes reservados pelo GC, e essa grandeza não foi medida.

Proposta e alternativas: docs/object-lifetime-design.md. Comandos reproduzíveis:
docs/memory-methodology.md. Resultados finais: docs/validation.md. Próximas
cargas: listas vivas grandes e aliases persistentes. Reutilização de endereços
nativos, handles e vetores em campos permanecem fora do contrato.
