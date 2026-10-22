# Runtime error contract

BlitzCS keeps process failures useful to scripts and build automation. A
program that finishes normally exits with status `0`. A compile or emission
failure is reported by the `blitzcs build` command with a nonzero status and a
diagnostic on standard error. An uncaught runtime failure, including an array
bounds violation or an exception crossing the C# bridge, is left as an
unhandled .NET exception: the process exits nonzero, writes the exception and
mapped `.bb` source location to standard error, and retains output written
before the failure on standard output. The exact nonzero runtime status is
owned by the .NET host and is not a stable BlitzCS API value.

There is no Legacy error profile. In particular, BlitzCS does not convert a
runtime failure into exit status `0` to match Blitz3D. The genuine Blitz3D
1.118 run of `upper-bound-error` displayed its native bounds diagnostic but
returned status `0` under the installed Proton/Wine setup. The differential
harness therefore records an intentional mismatch on success/failure status;
it does not erase the diagnostic or treat a local fixture as differential
evidence. The original's status is preserved as observed by the adapter.

The integration tests in `RuntimeErrorContractTests` exercise the contract at
the executable boundary: successful completion, a BASIC array bounds failure
after prior output, and an exception raised by a C# method called from BASIC.
These checks make sure failures stay diagnosable without requiring the .NET
host's implementation-specific numeric exception code.

## RuntimeError externo (Milestone 9)

O catálogo samples/Native/runtime.json registra a implementação real de
BlitzCS.LegacyRuntime.LegacyErrors.RuntimeError. O serviço lança uma exceção
gerenciada com a mensagem fornecida, termina com status não zero e mantém a
posição .bb na stack. Registro e empacotamento são opt-in, documentados em
[external-functions.md](external-functions.md). O núcleo não fornece mock nem
implementação silenciosa dos comandos de engine.

## Checkpoint M10

Um probe original isolado de upper-bound-error voltou a produzir a falha nativa
de bounds com status0. O timeout M9 permanece histórico/inconclusivo; não foi
reclassificado como equivalência. O adaptador agora preserva estágio, comando,
stdout/stderr parciais e arquivos instrumentados em erros/timeouts. A lógica de
dismissal não foi alterada para forçar sucesso.

Exaustão dos IDs positivos de Handle falha antes de reutilizar um ID. Read além
do Data também termina com erro gerenciado não zero. Um probe original de
exaustão Data mostrou diagnóstico Out of data mas teve timeout, portanto seu
status de término nativo não está comprovado por essa execução.
