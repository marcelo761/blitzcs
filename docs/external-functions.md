# Funções externas e módulos .NET

BlitzCS recebe símbolos tipados por compilação. O compilador não depende do
módulo que os implementa, nem de Blitz3DCS. BASIC e C# continuam no mesmo assembly
emitido; serviços externos são assemblies .NET referenciadas explicitamente.
A [decisão de arquitetura](external-functions-design.md) compara as alternativas.

## Host .NET

```csharp
var method = typeof(MyRuntime).GetMethod("Add", new[] { typeof(int), typeof(int) })!;
var registry = new ExternalFunctionRegistry(new[] { new ExternalFunction("NativeAdd", method) });
var generated = BasicCompiler.Compile(source, sourcePath, registry);
DotNetEmitter.Emit(generated, outputPath, "BlitzProgram", registry.ReferencePaths);
```

Métodos registrados precisam ser estáticos públicos, em tipos públicos não
genéricos de assemblies em disco. Argumentos e retornos aceitam Int32, Single,
String; comandos podem retornar Void, somente em chamadas como instruções.
Defaults CLR constantes desses tipos são copiados à chamada durante a
compilação; null String vira string vazia. Sem Boolean, Double, objetos,
vetores, arrays, ref/out, params ou métodos genéricos nesta fatia.

Cada nome BASIC é único e case-insensitive, começa por letra e usa letras ASCII,
dígitos e underscore. Palavras reservadas não são registráveis. O host escolhe
a sobrecarga .NET exata; o registro não adiciona sobrecargas ao BASIC. Nomes que
colidem com Const, Global ou Function são diagnosticados. Omissões finais e
conversões de argumentos usam o contrato BASIC, enquanto CS:: mantém sua fronteira
mais estrita. Funções BB continuam com ABI de aridade fixa para chamadas C#.

MethodInfo inspeciona apenas a configuração. O código gerado chama diretamente o
método estático, sem reflexão por chamada. Métodos não registrados produzem
`Unknown BASIC function or external symbol`, com posição do `.bb`, inclusive
Includes. Aridade, sufixo de retorno e tipos incompatíveis produzem diagnósticos.

## CLI

```sh
dotnet src/BlitzCS.CLI/bin/Release/net10.0/BlitzCS.CLI.dll build \
  samples/Native/runtime-error.bb -o artifacts/native-runtime-error.dll \
  --externals samples/Native/runtime.json
dotnet artifacts/native-runtime-error.dll
```

O catálogo contém `functions`, cada uma com `name`, `assembly`, `type`, `method`
e `parameters` (`Integer`, `Float`, `String`; ponteiros de struct em [native-struct-abi.md](native-struct-abi.md)). O caminho da assembly é relativo
ao catálogo. A seleção do método verifica a assinatura exata; nenhum símbolo
é registrado automaticamente. Catálogos são configuração confiável do host.
A CLI usa contexto de carga coletável para inspeção, permitindo compilações
independentes com assemblies de mesmo nome simples. Solicita unload após criar
o registro; os metadados permanecem disponíveis enquanto o registro é observado.

O carregador e o emissor percorrem as referências de assembly gravadas nos
metadados .NET. Cada dependência que não pertence ao runtime é procurada como
`<AssemblyName>.dll` ao lado do assembly que a referencia; o mesmo processo
continua pelas dependências transitivas. O build falha com o nome da dependência
se ela não estiver nessa pasta. O emissor compila contra essa cadeia e copia os
assemblies ao diretório de saída para que o programa rode sem acesso à pasta
original dos módulos.

O catálogo também pode declarar `nativeLibraries` como uma lista opcional de
caminhos relativos ao próprio catálogo. A CLI copia esses arquivos ao lado do
assembly gerado, junto com os assemblies gerenciados descobertos. Eles não são
referências de compilação nem entram automaticamente no registro BASIC; um
módulo gerenciado opt-in continua responsável por carregar e chamar a biblioteca
nativa. Caminhos ausentes e colisões de nome com dependências gerenciadas ou
arquivos de saída são rejeitados antes de alterar a saída.

```json
{
  "functions": [
    { "name": "NativeAdd", "assembly": "wrapper/NativeWrapper.dll",
      "type": "NativeWrapper", "method": "Add", "parameters": ["Integer", "Integer"] }
  ],
  "nativeLibraries": ["native/libexample.so"]
}
```

As referências do runtime .NET não são copiadas. Colisões de nome no diretório
de saída e uma saída que sobrescreveria uma referência são rejeitadas antes de
alterar arquivos do programa. A resolução usa referências de assembly, não varre
a pasta por arquivos arbitrários; arquivos auxiliares como assemblies de recursos
satélite precisam ser disponibilizados separadamente.

## RuntimeError real, opt-in

`BlitzCS.LegacyRuntime` fornece `LegacyErrors.RuntimeError(string)`, que lança
InvalidOperationException com a mensagem recebida. Retorna formalmente Integer
para a convenção de chamada BASIC, mas nunca retorna na execução. O exemplo
imprime `before native failure`, termina com erro e não imprime `unreachable`.
O status não zero segue o [contrato de erros](runtime-errors.md), sem reproduzir
o status zero de determinadas falhas do runtime original.

Essa biblioteca opcional não implementa gráficos, áudio ou física e não é
referenciada por BlitzCS.Compiler nem pela CLI. Não há implementações vazias de
comandos de engine. A integração futura continua na direção Blitz3DCS → BlitzCS.

## Módulo Blitz3DCS inicial

[samples/Native/blitz3dcs.json](../samples/Native/blitz3dcs.json) registra somente
métodos reais do [runtime M12](native-runtime-m12.md). A biblioteca permanece
independente do núcleo. O fechamento gerenciado de publicação não inclui DLLs
nativas, assets ou cargas dinâmicas sem referência em metadados. As referências
adicionais descobertas pelo emissor também não ampliam automaticamente a superfície
de símbolos `CS::` do probe semântico, que usa as referências explícitas do registro.

## Perfis de API e provedores

O campo opcional `profiles` compõe catálogos de declarações e bindings usando o
mesmo `--externals`. Os contratos podem identificar o provedor necessário sem
fornecer implementação; builds continuam rejeitando chamadas sem binding.
Veja [perfis de API](api-profiles.md) para o esquema, defaults, resolução de
caminhos e a distinção entre registros históricos `rtSym` e exports PE.
