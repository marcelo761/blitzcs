---
title: Milestone 7 Include multifile e auditoria SCP
author: Codex
date: 2026-10-21
touches:
  - src/BlitzCS.Compiler/IncludeResolver.cs
  - src/BlitzCS.Compiler/Syntax.cs
  - src/BlitzCS.Compiler/Semantics.cs
  - src/BlitzCS.Compiler/CSharpEmitter.cs
  - src/BlitzCS.Compiler/CSharpBridge.cs
  - tools/original_blitz3d_adapter.py
  - docs/scp-compatibility.md
  - docs/multifile.md
---

## Decidido

Include usa a pasta do arquivo principal para TODOS os caminhos relativos,
inclusive os escritos em subpastas. Fonte original blitz/main.cpp:213-217 muda
CWD antes do parser; parser.cpp:93-121 normaliza/lowercase e registra o caminho
antes de incluir. O principal não começa registrado: ciclos de volta a ele podem
processá-lo novamente. Duplicatas e ciclos entre incluídos são ignorados.
Regras verificadas em binários oficiais V1.118 via Proton, não apenas inferidas.

IncludeResolver guarda base e conjunto por compilação, sem mudar CWD do host.
Parsers filhos herdam escopo e arrayNames e exigem EOF próprio, evitando blocos
atravessando fronteiras. Statements são incorporados em ordem na mesma IR.
SourcePosition.File acompanha léxico, parser, semântica, CS::, #line e guards.
Parâmetros de função incluída vazia precisam usar a posição da declaração, não
um fallback para o principal. Blocos C# são opacos a diretivas BASIC.

Blitz3DCS consome BlitzCS; nenhuma dependência de engine entrou no compilador.
Data/Read/Restore, labels/Goto/Gosub foram pesquisados, mas adiados para manter a
fatia Include completa. Return atual é o de funções, sem pilha Gosub simulada.

## Verificado

Release sem avisos/erros: 375 testes .NET (354 anteriores + 14 compiler + 7 CLI),
20 Python (16 anteriores + 4), 33 fixtures locais. Sete exemplos executados;
novo samples/Hybrid/MultiFile/main.bb imprime 42 com BASIC → C# → BASIC e bloco
C# modificando a global compartilhada num único assembly.
Diferenciais reais: 32/33 coincidem, mantendo 27 anteriores e 5/5 novos Include.
Única divergência é upper-bound-error: original status0 e BlitzCS não zero.
Sem adapter_error/pending nas capturas reais; expectativas locais ficam separadas.

Adaptador copia a closure e preserva Include; nunca pré-expande o fonte nativo.
Somente o principal recebe brackets de profundidade para conclusão. Inserir
brackets em cada filho quebra Return antecipado dentro de função; bug reproduzido
em teste e confirmado por fixture original com saída 7,42. Includes fora da
árvore e caminhos absolutos são limites do adaptador, não do compilador.

## Auditoria e cuidados

Fonte oficial Regalis11/scpcb, commit edb8fe0840b78f14d1aef3a0bf6174630e7be296,
intacta em /tmp/scpcb-audit-source; não redistribuída no repo. 30 arquivos BB,
~52 mil linhas; closure estática de Main tem 24 arquivos. Apenas KeyName.bb
compilou isoladamente; wrapper externo incluiu o original e imprimiu Esc,
Caps Lock, Up. Main para na lexagem de '[' em Global SCP1025state#[6], linha291.
O lexer é eager: isso ocorre ANTES de expandir includes, portanto não afirmar
que a closure ou o texto anterior passou parsing/binding. MusicPlayer chega ao
resolvedor e encontra BlitzAL.bb realmente ausente no checkout.

Bloqueios medidos: Const, vetores fixos, parâmetros opcionais, formas de chamada,
Data/Read/Restore, biblioteca e APIs de engine. Não afirmar que SCP inteiro
compila; 1/30 arquivos isolados não é percentual de compatibilidade.
Docs/scp-compatibility.md classifica cobertura; tools/scp_compile_probe.py gera
hashes/primeiros erros sem executar o jogo. Metadados declarados de versão não
substituem validação de origem. Trail indexa BB como Clojure: usar o mapa para
contexto C#/Python, nunca como evidência de suporte sintático BlitzBasic.
