# Native interop vertical slice

`BlitzCS.NativeInterop` owns an explicitly loaded native library, checks ELF or PE format and process architecture before loading, resolves named exports, and binds only managed delegate types declared with `CallingConvention.Cdecl`. That declaration checks the managed side only: export names and binary headers do not reveal a function's true parameter or return types. Define every delegate from a trusted C header. The implementation and integration fixture have been run on Linux/ELF; PE preflight is implemented but has not been exercised on Windows. Other formats and calling conventions are not supported by this slice.

`NativeFunction<TDelegate>.Invoke` scopes calls against concurrent library unload. Its callback is a trusted host boundary: do not retain the delegate or expose it to BASIC. BASIC sees only the sample facade's `int`, `float`, and `string` methods. `NativeDemoBindings.Close()` returns `1` after disposing the cached owner; call it after the final native-backed function. The sample uses `BLITZCS_NATIVEINTEROP_LIBRARY` when set, otherwise it looks for the platform library filename beside the managed application. A library owner refuses to unload while a `NativeUtf8Resource` still needs its matching free export. Adopt allocated resources inside the allocator's `Invoke` callback, and dispose them before closing the owner.

Strings cross the boundary as explicit UTF-8 bytes. The sample uses managed allocated output storage for `blitz_utf8_copy`, and pairs `blitz_make_message` with `blitz_free`. Both managed input encoding and native output decoding reject malformed UTF-16/UTF-8 rather than replacing data.

The integration project compiles `tests/BlitzCS.NativeInterop.Tests/native/demo.c` with Linux `cc`, then runs the actual generated BASIC program against that library and the managed binding project:

```sh
dotnet test tests/BlitzCS.NativeInterop.Tests/BlitzCS.NativeInterop.Tests.csproj
```

The C fixture includes a signed 8-bit return export. Its delegate returns `sbyte`, then the managed facade widens that value to BASIC's `int`; declaring it as a 32-bit return type would produce an ABI mismatch.

## Publicação e demonstração

Após `dotnet build BlitzCS.slnx -c Release`, execute:

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/NativeInterop/main.bb -o artifacts/native-interop.dll \
  --externals samples/NativeInterop/native.json
env -u BLITZCS_NATIVEINTEROP_LIBRARY dotnet artifacts/native-interop.dll
```

O catálogo combina métodos gerenciados tipados com `nativeLibraries`, uma lista
explícita de arquivos relativos ao catálogo. A CLI copia os arquivos nativos e o
fechamento de assemblies gerenciados para a saída; não os transforma em referências
Roslyn nem infere seus exports. O exemplo Linux publica o `.so` real compilado do
fixture C, além das duas bibliotecas gerenciadas. Saída verificada: `42`, `3.5`,
`9`, `native:Olá 🌱`, `-1`, `42`, `1`, em linhas separadas. O último `1` confirma
a chamada explícita do controle gerenciado de fechamento, não um comando Blitz3D.

Os testes também executam uma cadeia gerenciada transitiva junto com biblioteca C
publicada em diretório isolado. Arquivo ausente e colisões com DLLs gerenciadas ou
arquivos gerados falham antes de sobrescrever saídas. Dependências nativas
transitivas não são descobertas automaticamente: o host deve fornecer os assets e
as regras de busca exigidos pelo loader do sistema. Não há seleção automática de RID.

## Limites do contrato

Este perfil executado é Linux x64/Cdecl. Não houve validação de execução .NET
Windows x86/x64, StdCall, Mach-O, varargs, callbacks persistentes ou C++ classes.
Arquivos `.decls` não são importados automaticamente. Strings FMOD legadas e seus
ponteiros/streams não são reinterpretados como este contrato UTF-8.

A camada exige bibliotecas e bindings confiáveis; não isola falhas de código
nativo. Trabalho assíncrono nativo e callbacks precisam de contratos próprios de
shutdown/ownership; a proteção de chamadas síncronas não os rastreia. Os owners
exigem Dispose explícito. Buffers/ponteiros ficam no binding gerenciado; o registro
BASIC continua aceitando somente Integer/Float/String. Arrays e objetos BASIC não
passam diretamente pela ABI nativa nesta entrega.

Fontes primárias usadas para o contrato .NET:
[NativeLibrary lifecycle tests](https://github.com/dotnet/runtime/blob/main/src/tests/Interop/NativeLibrary/API/GetLibraryExportTests.cs),
[Marshal.GetDelegateForFunctionPointer](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Marshal.cs),
[calling conventions](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/calling-conventions).
