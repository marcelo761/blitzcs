# M13 — ABI explícita, publicação e bloqueio FMOD

BlitzCS.NativeInterop é independente de engine/Compiler: NativeLibraryOwner.Load
valida ELF/PE e host, Bind<T> exige Cdecl explicitamente, delegados tipados vêm de
headersconfiáveis, não inferidos deexports. Reflexão ocorre noBind, não porcall.
NativeFunction.Invoke protege unload concorrente; Dispose dentroInvoke falha,
recursos vivos impedemUnload atéfree. ContadorInterlocked evita perda com64owners.
NativeUtf8Resource exigeadotar noallocator.Invoke, comprimentoexplícito, UTF8estrito,
matchingfree eDispose. Hostcallbacks não podem reterdelegate; não há sandbox.

CLI catálogo opcional nativeLibraries copiaassets porbasename compreflightcontra
saídas/assemblies. Registry/Compiler continuamsóprimitivos eNodependengine.
Native transitives não são descobertos automaticamente. SDK.NET10, testesC Linuxcc.
Sample NativeInterop publica.so+ManagedBinding+NativeInterop.dll, C#→BB→C, fechaowner.

FMODbaixada do próprio Regalis11/scpcb commit edb8fe... fmod.dll; hash
1e08da161cf78746304a834973971ee47a24817ee4e28df74e3f37dbbb383b22,
PE32/i386,3.75,232exports,8importsWin32. REArecusoulayout; fallbackobjdump+runtime.
Probe tools/probe_fmod_original.py clonaSDKemtempuserlibs, nãoalteraSCP/originalSDK.
NOSOUND e saída padrão inicializaram(bytebaixo1/GetError0)/fecharamviaProtonWin32.
Semplaybackmedido ou binding.NET. signedchar noheader vsInt32 .decls expõe
upperEAX instáveis(31138561/31160833capturados), não goldenstable! Aliasstdcall
não prova larguraretorno. Ponteirostreamnão podevirarint32 nohostx64.

SCP permanece FMod.bb:15 FSOUND_Init externoausente,2/30assemblies/27sintaxes.
Não registrarstub/signatureonly.FMODWin32não carreganoLinuxx64; shimsozinhanãoresolve.
Handoff.mdopçõesworkerpersistente oubackendcompatível; semescalonamentoautomático.
Release/Debug552.NET,40Python,86fixtureslocais. 85/86oráculosM11/M12reutilizados
comSHAidêntico; apenasupper-bound-errordivergente. 2probesFMODnovosnão são2matches.
Docs/scp-milestone13.md,native-interop-m13.md,native-original-m13.md,evidence/m13*.
