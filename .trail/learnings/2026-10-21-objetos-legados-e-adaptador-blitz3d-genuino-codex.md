---
title: Objetos legados e adaptador Blitz3D genuino
author: Codex
date: 2026-10-21
touches:
  - src/BlitzCS.Compiler/ObjectRuntime.cs
  - src/BlitzCS.Compiler/Semantics.cs
  - src/BlitzCS.Compiler/CSharpEmitter.cs
  - tools/original_blitz3d_adapter.py
  - docs/legacy-objects.md
---

Type/Field usa tipos nominais e classes gerenciadas; New adiciona a lista por tipo, Delete limpa campos e invalida aliases que comparam Null. Before/After devem aceitar alias excluido, rejeitando apenas referencia realmente nula: confirmado por execucao oficial V1.118 via Proton. Delete Null e no-op. For Each acompanha links atuais e suporta exclusao do atual e inclusao no fim. Nos excluidos ficam retidos ate termino: crescimento de memoria real, sem equivalencia de recuperacao refcount nativa. Field vetores e Handle/Object casts pendentes; ponte publica C# continua Integer/Single/String por fachadas BASIC. Distribuicao oficial V1.118 11.18:SoLoud executa pelo Wine PE32 instalado no Proton11, prefixo isolado e Xvfb; Wine Xodus local nao carregou ntdll32. Adaptador instrumenta apenas Print -> WriteLine e marcador de fim, nunca interpreta expressoes ou usa expectativas locais. Captura erros nativos via Wine msgbox e conserva codigo de saida real; timeout e falha do adaptador. Original formata 0.0001 como 1.e-004: corrigidos runtime e folding, regressao RED confirmada. verify.sh Release passou0avisos/erros,345.NET(177compilador+168CLI),16Python,18fixtureslocais e6exemplos. Relatorios originais separados de expectativas locais; consultar docs/original-adapter.md e docs/validation.md para resultados reais finais. Fonte consultada6beb288 e binario oficial2024 nao sao assumidos identicos.

## Resultado diferencial final verificado

Relatorios originais finais: fixtures.json14match/1mismatch; objects.json3match.
Total17/18coincidencias,0adaptererrors,0pending. Unica divergencia: bounds runtime_error
originalexit0 versusBlitzCSexit-6; mantida e nao normalizada. Scientificfloat corrigido
agora coincide. Capturas17match incluem compilacoes rejeitadas como esperado, nao17
programas de sucesso. docs/validation.md registra comandos e limites; ampliar casos
adversariais antes de afirmar equivalencia de ciclo de vida nativo.
