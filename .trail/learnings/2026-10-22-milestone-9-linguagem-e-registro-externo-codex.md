---
title: Milestone 9 defaults hex Field e registro externo
author: Codex
date: 2026-10-22
touches:
  - src/BlitzCS.Compiler/Syntax.cs
  - src/BlitzCS.Compiler/Semantics.cs
  - src/BlitzCS.Compiler/ExternalFunctions.cs
  - src/BlitzCS.Compiler/ExternalBinding.cs
  - src/BlitzCS.Compiler/CSharpBridge.cs
  - src/BlitzCS.CLI/ExternalCatalog.cs
  - src/BlitzCS.CLI/DotNetEmitter.cs
  - src/BlitzCS.LegacyRuntime/LegacyErrors.cs
  - tools/scp_compile_probe.py
  - docs/scp-milestone9.md
---

## Semântica original, não C# presumido

Fonte Blitz3D 6beb288cb5962393684a59a4a44ac11524894939 e executável oficial
V1.118 via Proton são evidências distintas. Defaults de parâmetros devem ser
constantes convertidas para o tipo formal; Bind completa somente posições finais
omitidas. F(,2) é inválido. ABI de BB continua aridade fixa. Hex acumula low32
bits signed ($FFFFFFFF=-1, $100000000=0). Field aceita expressão inicializadora,
mas V1.118 cria campos zero/vazio/Null sem executá-la: validar tipo, não emitir
código de inicialização. Array/vector initializer segue gramática distinta.

Parser original121-151 distingue Foo(1,2) de Foo (1),2 pelo token depois do
parêntese pareado. HasStatementArgumentParentheses preserva essa regra.
Comando sem args termina em Separator ou EOF, inclusive ':', e Else/ElseIf.

## Extensibilidade sem dependência de engine

ExternalFunction valida método público estático de tipo público não genérico,
com parâmetros/retorno Int32, Single ou String. ExternalFunctionRegistry imutável
é opt-in por compilação. BindExternal converte argumentos BASIC e emite IrCall
estático direto. MethodInfo só configura o registro, nunca executa cada chamada.
CS:: mantém fronteira mais estrita. Bridge e Roslyn recebem referências explícitas.
CLI --externals catálogo JSON resolve método exato em ALC coletável; unload é
solicitado após inspeção, metadados vivem enquanto o registro é observado.
DotNetEmitter rejeita colisão de output com DLL de origem antes de self-copy skip,
e rejeita dois paths publicados com mesmo filename antes de copiar qualquer DLL.
Dependências transitivas são responsabilidade explícita do host nesta fatia.

BlitzCS.LegacyRuntime implementa RuntimeError real (int formal que sempre lança
InvalidOperationException). Nenhuma referência a essa biblioteca no núcleo/CLI.
Direção futura Blitz3DCS -> BlitzCS permanece. Não há renderer nem mocks de engine.

## Auditoria e evidência

SCP inalterado: 2/30 antes e depois, Difficulty+KeyName. Primeiro diagnóstico de
Main sai de RuntimeError16 e chega a StrictLoads.bb59 Object. Parser examina
Includes antes do bind; catálogo RuntimeError não altera esse primeiro erro.
25 falhas sintáticas viraram15, expondo10 nativos não registrados; GetIniInt é
uma função BASIC ausente no contexto isolado, não defeito provado do jogo.
Probe classifica Unsupported keyword como sintático. Unknown BASIC function or
external symbol usa rtSym original para nativos, .decls para externos, restante
semântico. tools/scp_native_catalog.json registra hashes das fontes originais.
Inventário de comandos exclui comentários/strings e é aproximado, não callgraph.

Release final 0 warnings/errors; 469 .NET e25 Python passaram,65 fixtures locais.
Detalhes e capturas nativas finais em docs/validation.md. Preservar divergência
upper-bound-error: original status0 versus contrato moderno não zero. Não afirmar
compatibilidade completa ou SCP compilado por ter aceitado sintaxe isolada.

Final nativo: 64 coincidências atuais (58 antigas +6 M9) e1 adapter_error
upper-bound-error após dois timeouts. Nunca substituir esse status por capture
histórico: M8 samehash fica em historical_comparison separado. Snapshot atual
por fixture com hash em artifacts/m9-original-capture-cache; consolidação em
artifacts/m9-final-differential.json. Trail build e build --check passaram,
grafo wiring sincronizado (135 arquivos,658 nós); deep summaries não executados.
