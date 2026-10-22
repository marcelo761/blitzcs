# M11 — Sintaxe integrada SCP e checkpoint verificável

BasicCompiler.ValidateSyntax e CLI check-syntax usam Lexer/Parser/IncludeResolver
sem semântica/Roslyn/emissão. Sucesso não significa ligação dos símbolos ou
validação dos blocos C# pelo Roslyn. tools/scp_compile_probe.py --check-syntax
registra a fase separadamente; falha de host sem localização BASIC é infraestrutura.

Skybox.bb:11 exigiu %110001: TokenKind.Binary acumula unchecked Int32 preservando
bits baixos. Main.bb:2707 exigiu 2147483648: original V1.118 satura token decimal
positivo em Int32.MaxValue antes do menos unário. -2147483648→-2147483647,
mas $80000000 representa Int32.MinValue. Duas fixtures originais via Proton.

Árvore integrada Main (23 includes únicos, 24 arquivos com entrada) passa sintaxe.
Compilação real para Main.bb:12 FileSize não registrado; assembly do jogo ausente,
sem prova de semântica completa. 27/30 sintaxes isoladas; 2/30 assemblies isolados.
LoadAllSounds/Save precisam contexto Dim; MusicPlayer precisa BlitzAL.bb ausente.

Manifesto tools/scp_native_manifest.json contém metadados rtSym agrupados e
ocorrências lexicais, NÃO implementações ou chamada resolvida. Gerador verifica
hashes upstream. BlitzCS permanece independente da proposta docs/blitz3dcs-architecture.md.

Release: 523 .NET, 37 Python, 81 fixtures locais. Capturas reais novas81:
80 iguais + upper-bound-error divergência intencional de exitstatus; nenhum erroadapter.
Teste lifetime Debug falha também em checkpoint M10 62c1dbb; Releasepassa.
Não alterar o runtime nem enfraquecer teste a partir de suspeita de JIT.
Relatório docs/scp-milestone11.md, evidências docs/evidence/m11-validation.json.
