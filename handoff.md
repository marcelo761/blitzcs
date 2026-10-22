# Handoff M13 — integração FMOD do SCP ainda pendente

## Estado reproduzível

Base Git `2e7b1eb`, tag `blitzcs-m12-checkpoint`; trabalho M13 na branch
`codex/milestone-13`, checkpoint final identificado pela tag `blitzcs-m13-checkpoint`.
SDK .NET 10.0.401, processo Linux x64. Compilador independente de engine.
Novo `BlitzCS.NativeInterop`: loader explícito, bindings Cdecl tipados de headers
confiáveis, lifetime de chamadas/recursos, UTF-8 explícito e publicação de assets.
Perfil Linux executado; PE preflight presente, sem execução .NET Windows validada.

## Problema e evidências

Main do SCP passa na sintaxe, mas a análise semântica para em FMod.bb:15,
FSOUND_Init não registrado. Nenhum assembly integrado do jogo foi emitido.
DLL do próprio commit auditado do jogo: FMOD 3.75, PE32/i386,
SHA-256 `1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22`.
Ela não pode ser carregada diretamente pelo processo Linux x64 disponível.
Uma shim dentro desse mesmo processo não muda essa restrição de OS/bitness.

`docs/evidence/m13-fmod.json` preserva cabeçalho, imports/exports e capturas.
O inspetor REA rejeitou o layout; objdump e execução Win32 independente forneceram
as evidências, sem tratar a rejeição do inspetor como DLL inválida.

A execução original via Proton confirmou GetVersion 3.75, Init com byte baixo 1,
GetError 0 e Close, tanto com saída padrão quanto NOSOUND explícito. Não houve
teste de playback ou de integração FMOD com .NET. Na rota .decls, retorno
signed-char foi lido como Int32, expondo bits altos não estáveis. Assinaturas
reais precisam vir do header, não apenas do alias/decorador ou do `.decls`.

## Alternativas e decisão desta entrega

A. Worker Windows x86/Wine mantendo FMOD 3.75: melhor preservação do binário,
mas exige protocolo, implantação e ownership de sessão/streams. O worker deve
permanecer vivo entre Init e Close; iniciar e fechar um processo a cada chamada
não implementa a semântica do jogo.

B. ABI C estável com bindings gerenciados explícitos: é a abordagem implementada
e comprovada para bibliotecas da arquitetura hospedeira. Uma shim sobre a FMOD
histórica ainda precisa de processo compatível; sozinha não converte PE32 em ELF64.

C. Backend moderno com fachada FSOUND compatível: evita a dependência Win32,
mas requer implementação real de streams, canais, looping, volume, formatos,
erros e handles. Não é substituição por nomes de funções ou retornos constantes.

M13 escolheu B para a infraestrutura reutilizável e A somente como referência
executada de ABI/compatibilidade. A integração produtiva FMOD não foi simulada.
Escolher e implementar o worker A ou o backend C é a próxima decisão de produto e runtime. Não foi solicitado escalonamento automático para modelo caro.

## Tentativas e reprodução

- DLL obtida do URL pinado registrado na evidência, nunca adicionada ao Git.
- Inspeção PE via REA: invalid_format; fallback estático documentado.
- Dois probes reais com original Blitz3D em instalação temporária, sem editar SCP.
- Carregamento desse PE pelo perfil .NET Linux foi rejeitado antes de chamar exports;
  diagnóstico manteve o arquivo BASIC de origem.
- Biblioteca C real do fixture executada via catálogo, com heap/UTF-8/free e
  publicação gerenciada/nativa; testes e demonstração passaram.

Com variáveis Proton/display configuradas conforme docs/original-adapter.md:

```sh
python3 tools/probe_fmod_original.py --dll /caminho/fmod.dll \
  --report artifacts/fmod-probe.json --evidence-dir artifacts/fmod-probe-raw
# Acrescente --output default para repetir o perfil de inicialização padrão.
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH dotnet test BlitzCS.slnx -c Debug
```

## Restrições para a próxima etapa

Sem dependências de engine no compilador, sem alterar SCP ou criar stubs.
Ponteiros/streams devem permanecer privados, com handles tipados/validados,
sem truncar nint para Int32. Ownership precisa incluir DLL, sessão, streams e
threads/callbacks antes de unload. Licenciamento/distribuição da FMOD é separado
da licença do fonte do jogo. Não implementar renderer/física para contornar este bloqueio.
