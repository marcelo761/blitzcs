# M12 — Runtime real, publicação transitiva e raiz Debug do teste

Blitz3DCS.Runtime é biblioteca separada sem referência ao compilador. O host opta
por samples/Native/blitz3dcs.json: FileSize, FileType, CurrentDir, GetEnv, Chr, Len
mais RuntimeError anterior. Nenhum renderer/áudio/física ou stub criado.

LegacyFiles preserva baixoInt32 do tamanho, inexistente/diretório0, FileType1/2/0,
separadores e caixa alternativa não ambígua; NUL trunca como Win32 c_str. Barra
final exige diretório (bug reproduzido e corrigido por fixture original). FileSize
usa wildcard Win32 no componente final; só caso único diferencial comprovado.
LegacyStrings usa CP1252 explícito: Chr baixo8bits e Len bytes incluindoNUL;
Unicode não representável falha explicitamente. Não equivale a todos os locales.

ExternalAssemblyLoadContext no CLI descobre referências gerenciadas transitivas
mesma pasta com identidade validada, excluindo frameworkreal (não todaTPA).
DotNetEmitter publica fechamento, rejeita colisões antes de sobrescrever.
Recursos satélite/assets/nativeDLLs/cargas dinâmicas não são descobertos.
CSharpBridge continua com referências explícitas do registro, não todo fechamento.

HandleLifetimeTests Debug: Assert.Single(IDictionary) materializava valor contendo
objeto; frameDebug mantinha raiz atéGC. Originalassert em helperNoInlining passa,
assim como sóchaves. Teste final Assert.Single(Table.Keys) conserva cardinalidade
e GC/WeakReference/KeepAlive. Nenhum runtime de objetos/handles alterado.

Release e Debug537 .NET; Python40; fixtureslocais86. CapturasnovasM12 viaProton5/5
iguais. Casosanteriores81 repetidoslocalmente vsoráculosM11 hashesiguais:80iguais,
upper-bound-error divergenteintencional. Total85/86, não86capturasoriginaisnovas.

SCP Main passa sintaxe; FileSize→Chr→Len resolvidos; próximoerro FMod.bb:15
FSOUND_Init (fmod.decls para fmod.dll) externoausente. 2/30assembliesisolados,
nenhum assemblyintegrado ou semânticacompleta comprovados. Catálogo593registros
+declsexternas, 32BBrecursivos vs30baseline; frequências são candidatas, não binder.
Ver docs/scp-milestone12.md, native-runtime-m12.md, debug-lifetime-m12.md e
scp-native-catalog-m12.md. Evidências docs/evidence/m12-validation.json.
