# M16 — SystemProperty, capacidade nativa ausente e Windowed3D

Retomar por continue.md; relatório docs/system-runtime-m16.md. Implementação em
Blitz3DCS.Runtime.LegacySystem; compilador não mudou. Catálogos Native/Audio
registram SystemProperty(String)->String como chamada estática real.

Original gxruntime/gxruntime.cpp:1130–1190 e bbruntime/bbruntime.cpp:46/154:
case-insensitive, nomes exatos sem trim, desconhecida vazia, diretórios com barra
final. tolower em stdutil/stdutil.cpp:286 percorre a string inteira, inclusive NUL.
TempDir/AppDir usam diretórios reais do host com separadores legados; CPU/OS
modernos reportam arquitetura/descrição reais, divergindo da constante Intel e da
tabela antiga Windows (OS vazio no probe Wine). WindowsDir/SystemDir são BCL no
Windows e explicitamente indisponíveis no Linux; ramo Windows não executado.

AppHWND/AppHInstance/Direct* exigem host nativo e lançam PlatformNotSupportedException;
não inventar zero/handle nem truncar pointer x64 para o global Integer do SCP.
A assinatura vincula; isso não significa que o wrapper Win32 rode no Linux.
Original via Proton retornou AppHWND real não zero; quatro invariantes comuns
coincidem no assembly híbrido, e a diferença de capacidade foi registrada.

SCP sem alteração, mesmos 30 hashes: primeiro erro integrado avançou de
fullscreen_window_fix.bb:45:23 SystemProperty para :47:8 Windowed3D. 3/30 isolados
emitem assembly; Main+23 Includes passam sintaxe. Não há assembly/jogo integrado.
Windowed3D é capacidade gráfica: não implementar retorno falso/verdadeiro de fachada.
611 testes .NET Release/Debug,40 Python,86 fixtures;85/86 oráculos antigos preservados.
