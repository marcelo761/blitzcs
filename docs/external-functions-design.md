# Registro de funções externas — decisão M9

## Alternativas consideradas antes da implementação

1. Gerar adaptadores C# a partir de nomes livres em JSON. Integração simples, mas
   assinaturas incorretas só falham no Roslyn e nomes podem injetar código.
2. Registrar métodos .NET públicos estáticos, inspecionando MethodInfo durante
   configuração/compilação, e emitir chamadas diretas tipadas. Validação cedo,
   execução sem reflexão, reuso por engines independentes. Requer referências
   explícitas e distribuição das assemblies do módulo.
3. Resolver comandos por reflexão/delegates em cada execução. Flexível, porém
   adiciona dispatch dinâmico, enfraquece diagnósticos estáticos e encarece chamadas.

Escolhida a alternativa 2. O núcleo recebe um registro explícito e imutável de
assinaturas. Não descobre nem referencia Blitz3DCS. Um host pode registrar funções
de qualquer biblioteca .NET; a CLI oferece catálogo declarativo opt-in. O módulo
BlitzCS.LegacyRuntime implementa somente RuntimeError, uma falha real com mensagem;
não finge implementar serviços gráficos, de áudio ou física.

## Contrato da primeira fatia

Nomes BASIC case-insensitive e únicos; métodos estáticos públicos de tipos
públicos não genéricos, argumentos/retornos Int32, Single e String. Sem objetos,
arrays, ref/out, params ou dispatch de overloads BASIC. O host seleciona um método
.NET exato ao registrar. Conversões na chamada BASIC usam a semântica legada já
implementada, preservando a fronteira mais estrita de CS::. A emissão contém
chamada direta ao método. MethodInfo é usado somente na configuração do registro.
Tipos/namespaces/métodos são validados e escapados como identificadores C#.

Parâmetros opcionais .NET constantes podem fornecer defaults registrados; a
aplicação ocorre no compilador, sem Missing.Value na execução. O catálogo não
expõe serviços sem implementação. Ausência de registro permanece diagnóstico.

## Validação implementada

Testes cobrem nomes inválidos/duplicados, assinaturas não suportadas, aridade e tipos,
conversões BASIC, defaults, colisão com função BASIC, Includes, blocos C# e
referências Roslyn, colisões de caminhos de saída e agrupamento de argumentos.
RuntimeError foi executado fora do núcleo com mensagem e status não zero.
Regressões e auditoria foram executadas sem modificar SCP; resultados em
[validation.md](validation.md). Publicação/resolução de dependências transitivas
é responsabilidade explícita do host nesta primeira fatia.
