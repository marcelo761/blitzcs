# M11 — Literais e validação sintática

As duas alterações de linguagem foram orientadas por fontes SCP não alterados:
`Skybox.bb:11` usa `%110001`; `Main.bb:2707` usa `2147483648`.

O lexer original em `compiler/toker.cpp:155–158` reconhece `%` seguido de
`0`/`1` como literal binário. O parser em `compiler/parser.cpp:716–721`
acumula os bits; em V1.118 os resultados observados preservam os 32 bits baixos.
A BlitzCS agora usa acumulação `unchecked` em Int32. `%` de declaração de tipo
continua sendo reconhecido separadamente.

O parser original em `compiler/parser.cpp:701–704` chama `atoi` para literais
decimais. Não se infere uma regra portátil de C/C++ para overflow: a distribuição
oficial V1.118 foi executada. Ela satura o token positivo em 2147483647 antes
da aplicação de menos unário. Assim, `-2147483648` resulta em **-2147483647**,
enquanto `$80000000` e o binário correspondente representam Int32.MinValue.
Essa diferença deliberadamente preserva o comportamento medido desse compilador.
Não se afirma equivalência com todas as versões de BlitzBasic.

Referência consultada: `blitz-research/blitz3d`, commit
`6beb288cb5962393684a59a4a44ac11524894939`. O código-fonte consultado e o
binário oficial V1.118 não são apresentados como builds idênticos.
Procedência e instrumentação: [original-adapter.md](original-adapter.md).

As fixtures `tests/Compatibility/fixtures/m11/binary-values.bb` e
`decimal-overflow.bb` foram executadas no original via Proton. Seus resultados
capturados, incluindo limites e tokens de 50 dígitos, estão no manifesto
`tests/Compatibility/milestone11.json`. Resultados esperados não foram obtidos
por um interpretador alternativo.

`BasicCompiler.ValidateSyntax(source, sourcePath)` e
`blitzcs check-syntax source.bb` executam o lexer, parser e Include resolver
normais, preservando diagnósticos de origem. **Não executam análise semântica,
resolução de externos, Roslyn ou emissão.** Código C# embutido é delimitado pelo
scanner; seu conteúdo também depende da posterior validação pelo Roslyn.
A API é útil para separar bloqueios sintáticos de dependências do runtime,
mas sucesso não comprova compatibilidade completa da linguagem.
