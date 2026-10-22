# M13 — ABI nativa e integração SCP

## Entrega comprovada

`BlitzCS.NativeInterop` é infraestrutura independente de engine. O host carrega
bibliotecas da plataforma, declara delegates Cdecl de headers confiáveis e expõe
uma fachada Integer/Float/String pelo registro externo existente. O compilador
não recebeu dependência de engine, ponteiros BASIC ou importação automática de
`.decls`. O perfil executado usa Linux x64 e biblioteca C real compilada com cc.

A [fatia nativa](native-interop-m13.md) valida inteiros, float32, retorno signed-byte,
UTF-8, buffers, malloc/free, erros de export/carregamento e sincronização de unload.
A publicação combina assets nativos explícitos e assemblies gerenciados transitivos.
A demonstração C# → BASIC → C devolveu 42, 3.5, 9, native:Olá 🌱, -1, 42, 1;
a última operação fecha explicitamente o owner. Um teste negativo confirma que
um erro nativo preserva o arquivo e a linha BASIC no diagnóstico de runtime.

## FMOD histórica: observação, escolha e limite

A [pesquisa original](native-original-m13.md) cobre parser `.decls`, linker,
StdCall x86, conversões, ponteiros, strings, lifetime e tratamento de falhas.
A DLL do commit do jogo foi obtida sem adicioná-la ao Git: FMOD 3.75, PE32/i386,
162.816 bytes, SHA-256
`1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22`.
Os 232 exports decorados e oito dependências Win32 constam na
[evidência](evidence/m13-fmod.json). O inspetor REA recusou seu layout; essa
limitação foi registrada e não foi confundida com invalidade do DLL.

Dois probes novos com o compilador/runtime original via Proton executaram a DLL:

| Perfil Win32 real | GetVersion | Init, byte baixo | GetError | Close |
|---|---:|---:|---:|---|
| FMOD_OUTPUT_NOSOUND explícito | 3.75 | 1 | 0 | completou |
| Saída padrão, sem SetOutput | 3.75 | 1 | 0 | completou |

Não houve solicitação/medição de playback. Esses probes não são uma integração
FMOD com .NET, nem comparações de equivalência BlitzCS. Retornos signed-char
lidos como Int32 pelo `.decls` exibiram bits altos não estáveis; as capturas
preservam os valores brutos, mas não os transformam em contrato de teste.
Aliases StdCall indicam busca/argumentos de pilha, não a largura do retorno.

Foram comparados: A, reutilização histórica em processo Win32; B, bindings/ABI C
estável; C, backend moderno com implementação compatível FSOUND. A escolha M13
foi **B para a infraestrutura nativa executável e A como referência real de ABI**.
Uma shim isolada não converte PE32 em ELF64. Um worker x86 persistente poderia
preservar a DLL, com custo de IPC/implantação/lifetime; um backend moderno evita
esse ambiente, mas precisa implementar e validar streams, canais e formatos.
Nenhum deles foi fingido com retornos constantes. Licenciamento e distribuição
da FMOD continuam separados da licença do fonte SCP; o DLL não foi redistribuído.
A [pesquisa de interop nativa](native-original-m13.md) registra limites e alternativas para o próximo trabalho.

## Compilação integrada do SCP: sem avanço artificial

| Etapa | M12 | M13 |
|---|---|---|
| Sintaxe Main + 23 alvos Include | Aprovada | Aprovada |
| Primeiro erro semântico/externo | FMod.bb:15, FSOUND_Init | FMod.bb:15, FSOUND_Init |
| Arquivos isolados com assembly | 2/30 | 2/30 |
| Arquivos isolados com sintaxe validada | 27/30 | 27/30 |
| Assembly integrado do jogo | Não emitido | Não emitido |
| Vinculação/execução do jogo | Não alcançada | Não alcançada |

FSOUND_Init permanece não registrado: a DLL Win32 não tem binding funcional
.NET no perfil Linux x64 disponível. Reconhecer sua assinatura não bastaria.
Os seis comandos do runtime M12 e RuntimeError continuam registrados; o exemplo
C nativo usa símbolos próprios e não fornece implementações de comandos SCP.
Os mesmos 30 hashes do jogo foram preservados, sem edição dos fontes.

Categorias dos primeiros diagnósticos continuam: 2 compilados, 2 sintáticos de
contexto isolado, 9 semânticos/tipos, 14 nativos não registrados e 3 dependências
externas. Não há contagem inventada de erros posteriores. O [catálogo](scp-native-catalog-m12.md)
permanece inventário de assinaturas/candidatos; não comprova resolução semântica
completa, implementação de engine ou disponibilidade das DLLs externas.

## Regressões e reprodução

Release e Debug compilaram sem avisos/erros e passaram **552 testes .NET**
(281 compilador, 260 CLI, 11 interop). Os **40 Python** e **86 fixtures locais**
passaram. Os 86 casos foram comparados com capturas originais M11/M12 preservadas
de fontes idênticos: **85 coincidências**, mantendo upper-bound-error como a
única divergência. Esses 86 oráculos não foram recapturados neste milestone.
Os dois probes FMOD novos ficam separados dessa contagem.

[Evidência de validação](evidence/m13-validation.json) registra hashes, capturas,
resultados e o diagnóstico negativo ao tentar usar o PE no perfil .NET Linux.
A memória de objetos/handles manteve os testes e o stress 2×100.000 anterior;
nenhum código de lifetime de objetos BASIC foi alterado.

```sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH bash scripts/verify.sh
PATH=/var/home/marcelo/.local/share/causoryn-dotnet:$PATH dotnet test BlitzCS.slnx -c Debug
python3 tools/scp_compile_probe.py \
  --source-dir /tmp/scpcb-audit-source \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --cli src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll \
  --externals samples/Native/blitz3dcs.json --check-syntax \
  --report artifacts/scp-m13-after.json
```

Próximo passo: implementar uma sessão FMOD real por processo compatível ou
escolher/implementar um backend compatível. Depois, registrar somente métodos
funcionais e repetir a compilação integrada para revelar o próximo erro real.
