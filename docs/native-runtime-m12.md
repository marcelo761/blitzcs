# M12 — Runtime nativo inicial e fronteira de vinculação

`src/Blitz3DCS.Runtime` é uma biblioteca .NET independente, sem referência ao
compilador. O compilador e a CLI também não a referenciam. Um host opta por suas
funções através do contrato genérico `ExternalFunctionRegistry`; o catálogo
`samples/Native/blitz3dcs.json` demonstra o registro explícito. Não há registro
automático de comandos, funções vazias ou implementação de gráficos/áudio.

## Superfície funcional

| Método público estático | Assinatura BASIC | Contrato e evidência |
|---|---|---|
| `LegacyFiles.FileSize` | Integer(String) | Tamanho baixo de 32 bits reinterpretado como Int32; diretório, inexistente ou consulta indisponível → 0. Comparados 7 bytes, vazio, 2.147.483.651 bytes, NUL e wildcard único. |
| `LegacyFiles.FileType` | Integer(String) | Arquivo → 1, diretório → 2, indisponível → 0; wildcard → 0. Comparados caminho exato, caixa alternativa, inexistente e NUL. |
| `LegacyFiles.CurrentDir` | String() | Diretório de trabalho do processo, separadores legados e barra invertida final; o caminho retornado pode ser consultado pelos métodos de arquivos. |
| `LegacySystem.GetEnv` | String(String) | Variável do processo, ausente → string vazia, nome terminado por NUL como no CRT. Busca alternativa de caixa sem distinguir maiúsculas. |
| `LegacyStrings.Chr` | String(Integer) | Um byte, oito bits baixos; CP1252 é a codificação explícita da fronteira gerenciada. Verificados 0, 128, -1, 256 e 321. |
| `LegacyStrings.Len` | Integer(String) | Conta bytes CP1252, incluindo NUL. Texto não representável causa `EncoderFallbackException`, sem substituição silenciosa. |

O catálogo também registra o `RuntimeError` real da biblioteca anterior
`BlitzCS.LegacyRuntime`. `ChangeDir` e `SetEnv` não estão implementados. O registro
atual aceita resultados Integer/Float/String; suporte genérico a comandos void
continua pendente. Não se convertem comandos void em funções falsas para vinculá-los.

Referências consultadas: original `bbruntime/bbfilesystem.cpp:96–124`,
`gxruntime/gxfilesystem.cpp:39–64`, `bbruntime/bbruntime.cpp:53–67` e
`bbruntime/bbstring.cpp:85–88,114–117`, commit
`6beb288cb5962393684a59a4a44ac11524894939`. Capturas executadas com os binários
oficiais V1.118 via Proton, de procedência descrita em
[original-adapter.md](original-adapter.md). Fonte consultado e binário não são
apresentados como builds idênticos.

## Limites semânticos

O runtime traduz separadores e resolve caixa alternativa quando há um único
caminho correspondente no host. Sistemas que permitem dois nomes diferentes
somente pela caixa não possuem equivalente único no modelo Windows; caminhos
exatos são preferidos e buscas alternativas ambíguas retornam indisponível.
Ordenação de múltiplos resultados wildcard, Unicode/ANSI em outros locales,
reparse points/symlinks, permissões e particularidades de caminhos Win32 não têm
compatibilidade diferencial completa comprovada. Letras de unidade Windows não
são mapeadas para volumes Linux. `CurrentDir` e valores de ambiente refletem o
processo hospedeiro, não um sistema Windows virtual.

CP1252 é uma fronteira explícita para estes dois comandos de strings, não uma
mudança do tipo String do compilador ou da ponte C#. Texto Unicode arbitrário
continua atravessando a ponte; `Len` rejeita o que não cabe na codificação legada
suportada. Não se afirma equivalência de todos os codepages do Blitz3D original.

## Distribuição e futura integração gráfica

As chamadas emitidas são estáticas e tipadas, sem reflexão no caminho de cada
chamada. A seleção de MethodInfo ocorre somente na configuração do catálogo.
A CLI percorre referências de assembly para publicar dependências gerenciadas
transitivas, com identidade validada e erros antes de sobrescrever saídas.
[external-functions.md](external-functions.md) descreve as regras e limitações.

Um futuro módulo gráfico poderá fornecer outra fachada de métodos públicos
estáticos e registrá-los pelo mesmo mecanismo. Vulkan, janelas, dispositivos,
recursos e suas vidas úteis ficam no runtime específico; tipos Vulkan e
ponteiros não entram no compilador nem nos handles BASIC. A fachada histórica
pode usar IDs inteiros validados pelo runtime de engine. A proposta completa
continua em [blitz3dcs-architecture.md](blitz3dcs-architecture.md).
Bibliotecas nativas e assets não são descobertos pela cadeia de metadados .NET;
sua distribuição exige um contrato explícito futuro. Nenhum Vulkan foi implementado.

## Reproduzir a demonstração híbrida

Após build Release:

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/Hybrid/NativeFiles/main.bb -o artifacts/native-files-hybrid.dll \
  --externals samples/Native/blitz3dcs.json
dotnet artifacts/native-files-hybrid.dll
```

Saída esperada e executada: `42`, `1`, `42`, em linhas separadas. Um módulo C#
cria um arquivo real; C# chama BASIC, BASIC chama FileSize e modifica uma global;
o programa usa Include e remove o arquivo de demonstração ao terminar.

Para fixtures locais, prepare os arquivos esparsos (requer filesystem compatível):

```sh
python3 tools/prepare_native_fixtures.py artifacts/m12-assets
env -u BLITZCS_M12_MISSING BLITZCS_M12_ROOT="$PWD/artifacts/m12-assets" \
  python3 tools/differential.py --manifest tests/Compatibility/milestone12.json \
    --externals samples/Native/blitz3dcs.json --report artifacts/m12-native-local.json
```

O programa original recebe os mesmos bytes em um caminho acessível no Wine:
use `BLITZCS_M12_ROOT='Z:\run\media\system\SSD_SATA\BlitzCS\artifacts\m12-assets'`
no ambiente do adaptador, além das variáveis documentadas para Proton/display.
Isso configura os caminhos dos fixtures para cada host; não altera as expressões
ou resultados dos programas BASIC. O harness possui `--externals` apenas para a
compilação BlitzCS; o original resolve suas próprias funções nativas.
