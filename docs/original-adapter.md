# Comparação com Blitz3D original

`tools/original_blitz3d_adapter.py SOURCE.bb` executa o compilador e o runtime
originais e entrega somente `{phase, stdout, exit_code}` em JSON. Uma captura
válida encerra o adaptador com código 0; falha de infraestrutura encerra com 2,
escreve o motivo em stderr e não fabrica uma captura.

## Procedência verificada em 21/10/2026

O [README oficial](https://github.com/blitz-research/blitz3d/blob/master/README.TXT)
indica a [distribuição da Blitz Research](https://blitzresearch.itch.io/blitz3d).
Foi baixado dessa página `Blitz3D-V1.118.zip`, upload oficial `11383919`, publicado
em 04/09/2024, com 26.982.421 bytes. Não se trata de Blitz3D NG ou BlitzForge.
O download gratuito usa o endpoint POST `/blitz3d/download_url` e depois POST
`/blitz3d/file/11383919?source=game_download`; a URL de armazenamento retornada
é temporária e não é uma fonte de procedência independente.

| Arquivo | SHA-256 |
| --- | --- |
| `Blitz3D-V1.118.zip` | `eb5b07c3f2ccf5142ddf866c1c4caec5c4ee262b6a3ab0bcc718a397a822cc02` |
| `bin/blitzcc.exe` | `bb9ec2c0f8311f82a76b424bdf03cf1d736911d2083d2a575d6b7ad0311f063a` |
| `bin/runtime.dll` | `ba120a4a0f15f2ac48e1e86f6465137bd480d0b4a288cae2534a6fe0ecad1a4e` |
| `bin/linker.dll` | `2608b431226d170269e4ec9bc9c6215980f392f80199bc644a7001a44c378752` |

Esses são hashes calculados do download, não assinaturas publicadas pelo autor.
`blitzcc.exe -v` informou literalmente:

```text
Compiler version:11.18:SoLoud
Runtime version:11.18:SoLoud
Debugger version:0.0:
Linker version:11.18:SoLoud
```

A versão comercial é V1.118; `11.18:SoLoud` é a identificação que esse binário
imprime. O código legado consultado separadamente em
`/tmp/blitz-research/blitz3d`, commit
`6beb288cb5962393684a59a4a44ac11524894939`, não é apresentado como código exato
desses binários. A distribuição oficial atual usa o runtime SoLoud; o
[repositório original](https://github.com/blitz-research/blitz3d/blob/6beb288cb5962393684a59a4a44ac11524894939/LICENSE.TXT)
declara licença zlib/libpng, copyright Blitz Research Ltd. Nenhum binário foi
adicionado ao repositório BlitzCS e o runtime não foi modificado/recompilado.

## Ambiente utilizado

O Wine local Xodus 11.0 iniciou, mas não conseguiu carregar o executável PE32:
`failed to load ... syswow64/ntdll.dll error c0000135`. Não houve instalação de
pacotes nem alteração de prefixos pessoais. O Wine já instalado pelo Steam
Proton 11.0 possui suporte i386 e executou o original usando um prefixo separado.

```bash
export BLITZ3D_HOME=/tmp/blitz-original/v1.118/Blitz3D-V1.118
export BLITZ3D_WINE='/home/marcelo/.local/share/Steam/steamapps/common/Proton 11.0/files/bin/wine'
export BLITZ3D_WINEPREFIX=/tmp/blitz-original/proton-prefix
export BLITZ3D_DISPLAY=:1
export WINEPREFIX="$BLITZ3D_WINEPREFIX"
export blitzpath='Z:\tmp\blitz-original\v1.118\Blitz3D-V1.118'
WINEDEBUG=-all "$BLITZ3D_WINE" "$BLITZ3D_HOME/bin/blitzcc.exe" -v
```

O prefixo novo precisou de três DLLs que já estavam na instalação do Proton:

```bash
cp '/home/marcelo/.local/share/Steam/steamapps/common/Proton 11.0/files/lib/vkd3d/i386-windows/libvkd3d-1.dll' \
   '/home/marcelo/.local/share/Steam/steamapps/common/Proton 11.0/files/lib/vkd3d/i386-windows/libvkd3d-shader-1.dll' \
   '/home/marcelo/.local/share/Steam/steamapps/common/Proton 11.0/files/lib/vkd3d/i386-windows/libvkd3d-utils-1.dll' \
   "$BLITZ3D_WINEPREFIX/drive_c/windows/syswow64/"
```

Antes dessa cópia, o compilador retornava `Unable to open runtime.dll`; o log
de carregamento identificou exatamente essas três dependências do `wined3d.dll`.
O Xvfb disponível foi iniciado com
`/home/marcelo/.local/bin/Xvfb -displayfd 1 -screen 0 800x600x24 -nolisten tcp`,
que escolheu o display `:1`. O display do desktop não foi alterado.
O adaptador exige um display e um prefixo dedicados: execute as capturas em
série, pois ele dispensa o único diálogo visível `Error!` nesse display.
Arquivos em `/tmp` e o processo Xvfb são temporários; não são pré-requisitos
portáveis garantidos para futuras sessões.
O Xvfb criado para esta verificação foi encerrado ao terminar. Para repetir os
comandos, reinicie-o e ajuste `BLITZ3D_DISPLAY` ao número retornado por
`-displayfd 1`, que pode ser diferente de `:1`. O arquivo oficial e o prefixo
Proton de trabalho foram preservados.

## Captura e limites

O `Print` original desenha texto em uma janela. Para evitar OCR, o adaptador
cria uma cópia temporária do fonte e troca somente a chamada `Print expressão`
por `WriteLine BlitzCSCaptureFile, expressão`. Ambas as funções recebem string
no próprio original, verificado com `blitzcc +k`: `Print [string$]` e
`WriteLine stream,string$`. A expressão, sua avaliação, conversões, operadores,
funções, arrays e controle continuam sendo compilados e executados pelo original.
Não existe interpretador Python nem consulta aos resultados esperados.

O scanner preserva strings entre aspas e comentários `;`, suporta `Then Print`,
separadores `:` e `Print` vazio. Acrescenta um Global de captura, uma função de
fechamento e um arquivo marcador de término normal; `End` explícito chama esse
fechamento, enquanto `End Function`, `End Type`, `End If` e `End Select` são
preservados. Em fixtures sem Include, a linha extra inicial desloca diagnósticos
em uma linha. Dialect, comentários de bloco e nomes `BlitzCSCapture*` são
rejeitados. A API de instrumentação isolada (`instrument`) ainda rejeita Include;
`capture` suporta projetos com Include.

## Captura de projetos com Include

O parser original resolve todos os caminhos Include contra o diretório de
trabalho definido pelo driver para a pasta do arquivo de entrada. Inclui arquivos
recursivamente no ponto da diretiva, preserva o escopo do chamador, canonicaliza
os caminhos completos em minúsculas e registra cada caminho antes de analisar o
arquivo. Includes repetidos e ciclos entre arquivos já incluídos são ignorados.
O arquivo principal não é registrado antes da primeira análise; um include dele
feito por um filho pode analisá-lo novamente e então produzir os mesmos efeitos,
inclusive declarações duplicadas. Esses comportamentos foram consultados no
código do parser e verificados com o compilador V1.118 via Proton.

Para executar a semântica de CWD sob Wine/Linux, `capture` cria uma árvore
temporária com apenas o fechamento transitivo de arquivos alcançados por Include, preserva as
diretivas e caminhos relativos, e passa a pasta temporária como CWD. Os nomes
estagiados são minúsculos para reproduzir a resolução Windows em filesystem
case-sensitive. Cada arquivo mantém sua posição relativa na árvore. O manifesto
`project_sources_sha256` no log de evidência associa os caminhos relativos aos
hashes dos arquivos originais. Includes que escapam da pasta do projeto,
colisões de nomes que diferem apenas por maiúsculas/minúsculas e Includes
absolutos são limitações explícitas do adaptador; não são convertidos em
resultados do compilador.

A captura inclui um arquivo auxiliar com identificadores reservados e troca
`Print` por `WriteLine` em todos os arquivos alcançáveis. Apenas o arquivo de
entrada ajusta a profundidade: includes são código inline e podem conter
`Return` de função que não volta ao ponto textual seguinte do include.
O contador do arquivo de entrada permite que uma reanálise cíclica dele não feche
a saída antes da execução externa terminar.
Expressões, ordem das diretivas Include e texto das demais instruções permanecem
no compilador original. O helper também permite um ciclo que reanalisa o arquivo
principal sem fechar o canal de captura na análise aninhada. As linhas dos
diagnósticos ficam deslocadas em duas linhas no arquivo de entrada e não mudam
nos includes. As quatro capturas descritas em
[`include-research.md`](include-research.md) verificaram resolução relativa ao
diretório do principal, deduplicação, ciclos e erro em Include ausente. A captura não
cobre terminal interativo, gráficos, posicionamento de texto ou efeitos de
`Print` sobre o cursor.

O comando é `blitzcc +q -d -o program.exe input.bb`: `-d` habilita as verificações
originais, incluindo os limites de arrays. CP1252 é usado no fonte e na saída;
apenas quebras CRLF/CR são normalizadas para LF. O adaptador não reescreve
números ou mensagens para coincidir com o BlitzCS.

Erros de compilação só são aceitos quando o original imprime um diagnóstico no
formato `"arquivo":linha:coluna:linha:coluna:mensagem`. Erros de DLL, processo
sem executável, ausência do marcador e timeout são falhas do adaptador.

Para os erros de runtime suportados, `WINEDEBUG=-all,+msgbox` fornece o texto
nativo em stderr, por exemplo
`trace:msgbox:MSGBOX_OnInit L"Array index out of bounds"`. Somente textos
explícitos conhecidos do runtime são reconhecidos. `xdotool` dispensa o diálogo
`Error!` no display dedicado; a captura só é válida depois do término natural
do processo. Timeout com ou sem mensagem continua sendo falha do adaptador.
O código de saída nativo é preservado: esse original retorna **0** após o erro
de limite de array. A divergência em relação ao código não zero do BlitzCS fica
visível no relatório; não é convertida artificialmente em equivalência.

`BLITZ3D_TIMEOUT` configura o limite de cada processo (padrão: 6 segundos).
`BLITZ3D_EVIDENCE_DIR=artifacts/original-evidence` conserva comandos, códigos,
stdout/stderr nativos e hash do fonte em arquivos separados; esses registros
não fazem parte do JSON de comparação.

## Verificação real

O smoke compilou e executou `WriteLine capture,1+2`, produzindo `3\r\n` e
código 0. O primeiro relatório completo, antes das correções posteriores do
BlitzCS, foi gerado com:

```bash
python3 tools/differential.py \
  --dotnet /var/home/marcelo/.local/share/causoryn-dotnet/dotnet \
  --original-runner /run/media/system/SSD_SATA/BlitzCS/tools/original_blitz3d_adapter.py \
  --report artifacts/original-differential.json --timeout 20 --require-original
```

Resultado: **15 fixtures locais passaram; 13 comparações coincidiram; 2
divergiram; 0 falhas de adaptador e 0 pendências**. O original formata `0.0001`
como `1.e-004`, enquanto aquele build do BlitzCS emitia `1e-004`. O segundo
caso é o código de saída 0 do erro nativo de array descrito acima. Os registros
reais desses dois casos estão em `artifacts/original-evidence/`.
Esse relatório inicial foi preservado em
`artifacts/original-differential-before.json`.

Depois da correção do formato científico no BlitzCS, o mesmo comando com o
Release final produziu `artifacts/original-differential.json`: **15 fixtures
locais passaram; 14 comparações coincidiram; 1 divergência real de código de
saída; 0 falhas de adaptador e 0 pendências**. `float-format` agora coincide com
o original. `upper-bound-error` mantém a fase `runtime_error` e a saída vazia em
ambos, mas código 0 no original e código não zero no BlitzCS.

Também foram executados três fixtures de objetos: `object-order` produziu
`3\n2\n1\n`; `object-lifetime`, `1\n1\n42\n1\n1\n`; e `object-each`,
`1\n2\n3\n1\n`, todos com término normal e código 0. Os resultados estão em
`artifacts/original-object-captures.json`. Probes adicionais, com fonte e captura
em `artifacts/original-object-probes.json`, confirmaram que `Delete p` quando
`p.Node=Null`, `Delete Null` literal e repetir `Delete` no mesmo objeto são
operações válidas sem efeito. `After b` e `Before b` em um alias para o objeto
deletado ainda encontram seus vizinhos vivos. Esses resultados foram observados
no original; não foram deduzidos dos testes do BlitzCS.

A comparação final com o Release do BlitzCS foi executada também com
`--manifest tests/Compatibility/objects.json --report
artifacts/original-object-differential.json`: **3 fixtures locais passaram;
3 comparações coincidiram; 0 divergências, falhas de adaptador ou pendências**.
Assim, os dois relatórios finais totalizam 18 execuções reais do original,
17 equivalências observadas e a divergência documentada do código de saída.

`python3 -m unittest discover -s tests/differential -v` valida o protocolo e a
instrumentação com doubles identificados como tais; esses testes unitários não
constituem execuções do Blitz3D original. O relatório diferencial e os logs
nativos, gerados por processos reais, são a evidência de execução.

## Milestone 6 — nova execução com lifetime gerenciado

O Release posterior à alteração de lifetime foi comparado novamente em série:
`fixtures.json` 14/15 coincidências, `objects.json` 3/3 e `lifetime.json` 10/10.
Total: **27/28 coincidências**, nenhuma falha de adaptador ou pendência. A única
divergência permanece o status de término em `upper-bound-error`, intencional
segundo `runtime-errors.md`. Os resultados 17/18 acima são o checkpoint anterior.

As capturas finais e os limites ficam em `lifetime-original-results.md` e
`validation.md`. A medição de memória antes/depois da BlitzCS é independente,
registrada em `memory-methodology.md`; as fixtures nativas não medem GC.
O Xvfb criado para a nova rodada foi encerrado; binários oficiais e prefixo
isolado foram preservados para reprodução, sem alterar prefixos pessoais.

## Milestone 7 — comparação multifile final

O adaptador atualizado e o Release final repetiram os 28 casos anteriores e cinco
casos de Include: **32/33 coincidências**, preservando todas as 27 anteriores,
com **5/5 multifile**, sem falhas do adaptador ou pendências. Permanece apenas a
divergência explícita de término em upper-bound-error. Veja `validation.md` e
`include-research.md` para a matriz e limites; o staging preserva Include e não
pré-expande os arquivos para o compilador original.
