---
title: Milestone 8 vetores fixos e Const SCP
author: Codex
date: 2026-10-21
touches:
  - src/BlitzCS.Compiler/Syntax.cs
  - src/BlitzCS.Compiler/Semantics.cs
  - src/BlitzCS.Compiler/VectorRuntime.cs
  - src/BlitzCS.Compiler/CSharpEmitter.cs
  - src/BlitzCS.Compiler/CSharpBridge.cs
  - docs/fixed-vectors.md
  - docs/fixed-vectors-research.md
  - docs/scp-milestone8.md
---

## Semântica e implementação

Main.bb:291 do SCP original declara Global SCP1025state#[6]: vetor fixo,
não Dim. Global/Local/Field aceitam [] com limite constante inclusivo, acesso
por [], elementos primitivos/objetos. ValueType.Vector guarda comprimento e
tipo; IsObject fica falso no vetor, ElementType mantém nominalidade. IR de
acesso/atribuição emite __BlitzVector<T>.Element por ref e avalia índice uma vez.
Globais iniciam antes do corpo Main; locais na entrada (não no ponto textual),
campos em New. Delete limpa a referência ao vetor: não Delete recursivo de filhos.
Teste FixedVectorLifetimeTests mantém alias do proprietário e verifica coleta de
filho já excluído após limpar campo. Vetores inteiros não são valores copiáveis,
parâmetros ou ponte BB pública nesta fatia; funções primitivas os encapsulam.

Const é main-only escalar tipado com inicializador. Passagem de protótipos em
ordem antes de campos/funções/main; constantes anteriores podem alimentar
expressões, posteriores não podem alimentar inicializadores. Bind substitui por
literais, escrita e colisões inválidas diagnosticadas. Sombreamento por locais e
parâmetros de função é permitido e foi confirmado no executável original.
Float->Integer do limite usa round-to-even, não cast truncado: [2.5]=>2,[2.8]=>3.

## Evidência e limites

Fonte original commit6beb288cb5962393684a59a4a44ac11524894939, parser.cpp414-431,
declnode.cpp241-253, basic.cpp167-185, varnode.cpp122-160. Detalhes e capturas
reais em docs/fixed-vectors-research.md. Manifestos vectors.json/constants.json
incluem execuções e erros de compilação. Guard negativo gerenciado é mais forte
que nativo inseguro; teste original de erro dinâmico sofreu timeout, não simular.

SCP inalterado passou de1/30 para2/30 arquivos independentes (Difficulty+KeyName).
O lexer deixou de falhar em13 primeiros colchetes; Main agora para em RuntimeError
linha16, antes do Include, logo NÃO afirmar que seu grafo inteiro compilou.
Auditoria before/vector-only/after e hashes em artifacts/scp-m8-*.json. Defaults,
hex, comandos nativos e BlitzAL externo continuam bloqueios; sem mocks de engine.
Consultar docs/validation.md para os totais finais medidos; verify.sh reproduz
Release, regressões, exemplos e expectativas locais. Comparações originais têm
relatórios artifacts/m8-*-differential.json e exigem Proton/distribuição genuína.

Resultado final medido: verify.sh status 0, Release com 0 warnings e 0 errors,
428 testes .NET, 20 Python e 59 fixtures locais aprovados. Proton: 26/26 novos
mais 32 anteriores, total 58/59. Precedence sofreu erro de infraestrutura antes
do compilador na primeira tentativa; retry isolado real coincidiu, ambos os
relatórios preservados. Única divergência: upper-bound-error (status original 0).
